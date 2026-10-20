using System.Diagnostics;
using System.Runtime.InteropServices;
using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Telemetry;

namespace Spike.Interaction.Core.Diagnostics;

/// <summary>
/// Spike 002C diagnostic only (not product code): was the UI thread stalled, and in what?
/// A background thread sends two heartbeats every period: a framework work item (<c>postWork</c>, e.g. a dispatcher
/// operation at Send priority) and a plain posted message to a message-only window owned by the UI thread. A work item
/// that runs later than the stall threshold is logged together with whether the raw message pump kept running meanwhile.
/// While a work item is overdue, the UI thread is sampled up to three times: suspend, copy its registers and the top of
/// its stack into preallocated buffers, resume. Symbols (dbghelp, exports only, local path only) are resolved after the
/// resume, so nothing that could need a lock held by the suspended thread runs while it is suspended.
/// Construct on the UI thread.
/// </summary>
public sealed unsafe partial class StallProbe : IDisposable
{
    private const uint WM_PROBE = 0x8000 + 0x51;
    private const int StackCopyBytes = 128 * 1024;
    private const int MaxFrames = 400;
    private static readonly int[] SampleAtMs = [1000, 2500, 4000, 6500];

    private readonly SpikeLog _log;
    private readonly Action<long> _postWork;
    private readonly int _periodMs, _stallMs;
    private readonly nint _thread;
    private readonly nint _hwnd;
    private readonly WndProc _proc;
    private readonly Thread _worker;
    private readonly byte* _ctx;
    private readonly byte[] _stack = new byte[StackCopyBytes];
    private volatile bool _stop;
    private bool _symInit;

    private long _workSeq, _workSentTs, _workAckSeq;
    private int _samplesTaken;
    // message channel: written on the UI thread, read by the worker (diagnostic, benign races)
    private long _msgPrompt, _msgPromptAtSend, _msgLastAckTs;
    private double _msgMaxMs;

    public StallProbe(SpikeLog log, Action<long> postWork, int periodMs = 50, int stallMs = 1000)
    {
        _log = log; _postWork = postWork; _periodMs = periodMs; _stallMs = stallMs;
        var tid = Kernel32.GetCurrentThreadId();
        _thread = OpenThread(0x0002 | 0x0008 | 0x0040, 0, tid); // SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
        _ctx = (byte*)NativeMemory.AlignedAlloc(1232, 16);
        NativeMemory.Clear(_ctx, 1232);
        _proc = WndProc;
        var hInst = Kernel32.GetModuleHandleW(0);
        var wc = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = hInst, lpszClassName = Marshal.StringToHGlobalUni("SpikeStallProbe") };
        User32.RegisterClassExW(ref wc);
        _hwnd = User32.CreateWindowExW(0, "SpikeStallProbe", null, 0, 0, 0, 0, 0, new nint(-3), 0, hInst, 0);
        _worker = new Thread(Run) { IsBackground = true, Name = "stall-probe", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
        _log.Event("probe.start", new { uiTid = tid, periodMs, stallMs, threadHandle = _thread != 0, hwnd = _hwnd != 0 });
    }

    /// <summary>Call from the posted work item, on the UI thread.</summary>
    public void OnWork(long seq)
    {
        var now = Stopwatch.GetTimestamp();
        var ms = SpikeLog.MsBetween(_workSentTs, now);
        if (ms >= _stallMs)
            _log.Event("probe.stall", new
            {
                seq, workLatencyMs = Math.Round(ms, 1), samples = _samplesTaken,
                // raw window messages handled within 150 ms of being posted while the work item was waiting
                msgPromptDuringStall = _msgPrompt - _msgPromptAtSend, msgMaxLatencyMs = Math.Round(_msgMaxMs, 1),
            }, _workSentTs);
        Volatile.Write(ref _workAckSeq, seq);
    }

    private nint WndProc(nint h, uint msg, nuint w, nint l)
    {
        if (msg != WM_PROBE) return User32.DefWindowProcW(h, msg, w, l);
        var now = Stopwatch.GetTimestamp();
        var ms = SpikeLog.MsBetween(l, now);
        if (ms <= 150) _msgPrompt++;
        if (ms > _msgMaxMs) _msgMaxMs = ms;
        _msgLastAckTs = now;
        return 0;
    }

    private void Run()
    {
        while (!_stop)
        {
            Thread.Sleep(_periodMs);
            var now = Stopwatch.GetTimestamp();
            User32.PostMessageW(_hwnd, WM_PROBE, 0, (nint)now);
            if (Volatile.Read(ref _workAckSeq) == _workSeq)
            {
                _workSentTs = now; _samplesTaken = 0; _msgPromptAtSend = _msgPrompt; _msgMaxMs = 0;
                _postWork(++_workSeq);
                continue;
            }
            var age = SpikeLog.MsBetween(_workSentTs, now);
            if (_samplesTaken < SampleAtMs.Length && age >= SampleAtMs[_samplesTaken]) { Sample(age); _samplesTaken++; }
        }
    }

    private void Sample(double ageMs)
    {
        *(uint*)(_ctx + 0x30) = 0x100003; // CONTEXT_AMD64 | CONTROL | INTEGER
        int len = 0; bool gotCtx = false;
        var t = Stopwatch.GetTimestamp();
        if (SuspendThread(_thread) == uint.MaxValue) { _log.Event("probe.sampleFailed", new { error = Marshal.GetLastPInvokeError() }); return; }
        try
        {
            if (GetThreadContext(_thread, _ctx) != 0)
            {
                gotCtx = true;
                var rsp = *(ulong*)(_ctx + 0x98);
                MEMORY_BASIC_INFORMATION mbi;
                if (VirtualQuery((void*)rsp, &mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) != 0)
                {
                    len = (int)Math.Min((ulong)mbi.BaseAddress + mbi.RegionSize - rsp, StackCopyBytes);
                    fixed (byte* dst = _stack) Buffer.MemoryCopy((void*)rsp, dst, StackCopyBytes, len);
                }
            }
        }
        finally { ResumeThread(_thread); }
        var suspendedUs = Math.Round(SpikeLog.MsBetween(t, Stopwatch.GetTimestamp()) * 1000, 1);
        if (!gotCtx) { _log.Event("probe.sampleFailed", new { error = "GetThreadContext" }); return; }

        ulong R(int off) => *(ulong*)(_ctx + off);
        ulong rip = R(0xF8), sp = R(0x98);
        ulong? Deref(ulong p) => p >= sp && p + 8 <= sp + (ulong)len ? BitConverter.ToUInt64(_stack, (int)(p - sp)) : null;
        var frames = new List<string>();
        for (var off = 0; off + 8 <= len && frames.Count < MaxFrames; off += 8)
        {
            var v = BitConverter.ToUInt64(_stack, off);
            if (IsImageCode(v)) frames.Add($"+0x{off:X}: {Symbol(v)}");
        }
        _log.Event("probe.sample", new
        {
            ageMs = Math.Round(ageMs, 1), suspendedUs,
            rip = Symbol(rip),
            regs = new { rax = $"0x{R(0x78):X}", rcx = $"0x{R(0x80):X}", rdx = $"0x{R(0x88):X}", r8 = $"0x{R(0xB8):X}", r9 = $"0x{R(0xC0):X}", r10 = $"0x{R(0xC8):X}" },
            r8Deref = Deref(R(0xB8)) is { } a ? (long?)(long)a : null,          // NtWaitForSingleObject: R8 -> timeout (100 ns, negative = relative)
            stackArg5 = Deref(sp + 0x28) is { } b ? $"0x{b:X}" : null,         // 5th syscall argument
            stackArg5Deref = Deref(sp + 0x28) is { } c && Deref(c) is { } d ? (long?)(long)d : null,
            msgLastAckAgoMs = Math.Round(SpikeLog.MsBetween(_msgLastAckTs, Stopwatch.GetTimestamp()), 1),
            frames,
        });
    }

    private static bool IsImageCode(ulong v)
    {
        if (v < 0x10000 || v > 0x7FFF_FFFF_FFFF) return false;
        MEMORY_BASIC_INFORMATION mbi;
        if (VirtualQuery((void*)v, &mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) return false;
        return mbi.Type == 0x1000000 && (mbi.Protect & 0xF0) != 0; // MEM_IMAGE, PAGE_EXECUTE*
    }

    private string Symbol(ulong addr)
    {
        if (!_symInit)
        {
            SymSetOptions(0x2 | 0x4 | 0x200 | 0x80000); // UNDNAME | DEFERRED_LOADS | FAIL_CRITICAL_ERRORS | NO_PROMPTS
            _symInit = SymInitializeW(Process.GetCurrentProcess().Handle, AppContext.BaseDirectory, 1) != 0; // local path only: no symbol server
        }
        string module = "?";
        if (GetModuleHandleExW(0x4 | 0x2, (void*)addr, out var hMod) != 0) // FROM_ADDRESS | UNCHANGED_REFCOUNT
        {
            var buf = stackalloc char[260];
            var n = GetModuleFileNameW(hMod, buf, 260);
            module = Path.GetFileName(new string(buf, 0, (int)n));
            if (_symInit)
            {
                SymRefreshModuleList(Process.GetCurrentProcess().Handle);
                var si = stackalloc byte[88 + 2 * 512];
                *(uint*)si = 88; *(uint*)(si + 80) = 512;
                if (SymFromAddrW(Process.GetCurrentProcess().Handle, addr, out var disp, si) != 0)
                    return $"{module}!{new string((char*)(si + 84), 0, (int)*(uint*)(si + 76))}+0x{disp:X}";
            }
            return $"{module}+0x{addr - (ulong)hMod:X}";
        }
        return $"0x{addr:X}";
    }

    public void Dispose()
    {
        _stop = true;
        _worker.Join(1000);
        if (_hwnd != 0) User32.DestroyWindow(_hwnd);
        if (_thread != 0) CloseHandle(_thread);
        NativeMemory.AlignedFree(_ctx);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress; public nint AllocationBase; public uint AllocationProtect; public uint PartitionIdAndPad;
        public ulong RegionSize; public uint State; public uint Protect; public uint Type; public uint Pad;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint OpenThread(uint access, int inherit, uint tid);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint SuspendThread(nint thread);
    [LibraryImport("kernel32.dll")] private static partial uint ResumeThread(nint thread);
    [LibraryImport("kernel32.dll")] private static partial int GetThreadContext(nint thread, void* ctx);
    [LibraryImport("kernel32.dll")] private static partial nuint VirtualQuery(void* addr, MEMORY_BASIC_INFORMATION* mbi, nuint len);
    [LibraryImport("kernel32.dll")] private static partial int GetModuleHandleExW(uint flags, void* addr, out nint module);
    [LibraryImport("kernel32.dll")] private static partial uint GetModuleFileNameW(nint module, char* name, uint size);
    [LibraryImport("kernel32.dll")] private static partial int CloseHandle(nint h);
    [LibraryImport("dbghelp.dll")] private static partial uint SymSetOptions(uint options);
    [LibraryImport("dbghelp.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int SymInitializeW(nint process, string? searchPath, int invade);
    [LibraryImport("dbghelp.dll")] private static partial int SymRefreshModuleList(nint process);
    [LibraryImport("dbghelp.dll")] private static partial int SymFromAddrW(nint process, ulong addr, out ulong displacement, void* symbol);
}
