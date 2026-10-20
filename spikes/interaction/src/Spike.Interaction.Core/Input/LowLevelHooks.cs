using System.Diagnostics;
using System.Runtime.InteropServices;
using Spike.Interaction.Core.Native;

namespace Spike.Interaction.Core.Input;

/// <summary>
/// Installs WH_KEYBOARD_LL and WH_MOUSE_LL on a dedicated thread with its own message loop, as the
/// documentation recommends, so a busy UI thread can never delay the hook past LowLevelHooksTimeout.
/// Callbacks must be fast: they are invoked on the hook thread and should only post work elsewhere.
/// </summary>
public sealed class LowLevelHooks : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly HookProc _kbProc;   // kept alive so the GC never collects the delegate
    private readonly HookProc _mouseProc;
    private uint _threadId;
    private nint _hKb, _hMouse;
    private long _maxKbTicks, _maxMouseTicks;
    private long _kbCalls, _mouseCalls;
    private volatile bool _swallowTriggerKey;

    public Func<KeyEvent, bool>? OnKey;       // return true to swallow the key
    public Action<MouseEvent>? OnMouse;
    public Exception? StartupError { get; private set; }

    public LowLevelHooks(bool installMouse = true, bool installKeyboard = true)
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
        _thread = new Thread(() => Run(installMouse, installKeyboard)) { Name = "LowLevelHooks", IsBackground = true };
    }

    public bool SwallowTriggerKey { get => _swallowTriggerKey; set => _swallowTriggerKey = value; }

    public void Start()
    {
        _thread.Start();
        _ready.Wait();
        if (StartupError is not null) throw StartupError;
    }

    /// <summary>Worst-case time spent inside the hook callbacks, in milliseconds (diagnostic).</summary>
    public (double KbMaxMs, double MouseMaxMs, long KbCalls, long MouseCalls) Stats =>
        (TicksToMs(_maxKbTicks), TicksToMs(_maxMouseTicks), _kbCalls, _mouseCalls);

    private static double TicksToMs(long t) => t * 1000.0 / Stopwatch.Frequency;

    private void Run(bool installMouse, bool installKeyboard)
    {
        _threadId = Kernel32.GetCurrentThreadId();
        var hMod = Kernel32.GetModuleHandleW(0);
        if (installKeyboard)
        {
            _hKb = User32.SetWindowsHookExW(WH.KEYBOARD_LL, _kbProc, hMod, 0);
            if (_hKb == 0) { StartupError = new InvalidOperationException($"SetWindowsHookEx(KEYBOARD_LL) failed: {Marshal.GetLastWin32Error()}"); _ready.Set(); return; }
        }
        if (installMouse)
        {
            _hMouse = User32.SetWindowsHookExW(WH.MOUSE_LL, _mouseProc, hMod, 0);
            if (_hMouse == 0) { StartupError = new InvalidOperationException($"SetWindowsHookEx(MOUSE_LL) failed: {Marshal.GetLastWin32Error()}"); _ready.Set(); return; }
        }
        _ready.Set();

        while (User32.GetMessageW(out var msg, 0, 0, 0))
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessageW(ref msg);
        }

        if (_hKb != 0) User32.UnhookWindowsHookEx(_hKb);
        if (_hMouse != 0) User32.UnhookWindowsHookEx(_hMouse);
    }

    private nint KeyboardProc(int nCode, nuint wParam, nint lParam)
    {
        if (nCode < 0) return User32.CallNextHookEx(_hKb, nCode, wParam, lParam);
        var t0 = Stopwatch.GetTimestamp();
        var swallow = false;
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = (uint)wParam;
            var isDown = msg is WM.KEYDOWN or WM.SYSKEYDOWN;
            var ev = new KeyEvent((int)k.vkCode, isDown, (k.flags & LLKHF.INJECTED) != 0, t0);
            swallow = OnKey?.Invoke(ev) ?? false;
        }
        catch { /* never let an exception escape a hook */ }
        finally
        {
            var dt = Stopwatch.GetTimestamp() - t0;
            if (dt > _maxKbTicks) _maxKbTicks = dt;
            _kbCalls++;
        }
        return swallow ? 1 : User32.CallNextHookEx(_hKb, nCode, wParam, lParam);
    }

    private nint MouseProc(int nCode, nuint wParam, nint lParam)
    {
        if (nCode < 0) return User32.CallNextHookEx(_hMouse, nCode, wParam, lParam);
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var msg = (uint)wParam;
            MouseButton? button = msg switch
            {
                WM.LBUTTONDOWN or WM.LBUTTONUP => MouseButton.Left,
                WM.RBUTTONDOWN or WM.RBUTTONUP => MouseButton.Right,
                WM.MBUTTONDOWN or WM.MBUTTONUP => MouseButton.Middle,
                WM.XBUTTONDOWN or WM.XBUTTONUP => ((m.mouseData >> 16) & 0xFFFF) == 1 ? MouseButton.X1 : MouseButton.X2,
                _ => null,
            };
            if (button is not null || msg == WM.MOUSEMOVE)
            {
                var isDown = msg is WM.LBUTTONDOWN or WM.RBUTTONDOWN or WM.MBUTTONDOWN or WM.XBUTTONDOWN;
                OnMouse?.Invoke(new MouseEvent(button, isDown, m.pt.X, m.pt.Y, (m.flags & LLMHF.INJECTED) != 0, t0));
            }
        }
        catch { }
        finally
        {
            var dt = Stopwatch.GetTimestamp() - t0;
            if (dt > _maxMouseTicks) _maxMouseTicks = dt;
            _mouseCalls++;
        }
        return User32.CallNextHookEx(_hMouse, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadQuit();
            _thread.Join(2000);
        }
    }

    private void PostThreadQuit()
    {
        PostThreadMessageW(_threadId, WM.QUIT, 0, 0);
    }

    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);
}
