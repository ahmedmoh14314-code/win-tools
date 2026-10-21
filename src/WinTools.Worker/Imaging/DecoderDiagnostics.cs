using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// The JPEG codec reports damaged scan data ("Corrupt JPEG data: premature end of data segment") only as text on the
/// process's standard error, and still returns success with invented pixels. The worker therefore points its standard
/// error at a pipe it owns before any codec is loaded, and treats any text a decode writes there as damage.
/// Enabled by the worker's entry point only; in-process callers (unit tests) run without it.
/// </summary>
internal static partial class DecoderDiagnostics
{
    private const int StdErrorHandle = -12;
    private static SafeFileHandle? _read;
    private static SafeFileHandle? _write; // kept open for the process lifetime

    public static void Enable()
    {
        if (_read is not null) return;
        if (!CreatePipe(out var read, out var write, 0, 1 << 20)) throw new InvalidOperationException($"CreatePipe failed ({Marshal.GetLastPInvokeError()}).");
        if (!SetStdHandle(StdErrorHandle, write.DangerousGetHandle())) throw new InvalidOperationException($"SetStdHandle failed ({Marshal.GetLastPInvokeError()}).");
        // C runtimes cache the standard error handle when they initialise, before this code runs, so their file
        // descriptor 2 is re-pointed too. The system codecs write through msvcrt.dll; ucrtbase.dll is covered as well.
        Redirect(write.DangerousGetHandle(), MsvcrtOpenOsfHandle, MsvcrtDup2);
        Redirect(write.DangerousGetHandle(), UcrtOpenOsfHandle, UcrtDup2);
        (_read, _write) = (read, write);
    }

    private static void Redirect(nint handle, Func<nint, int, int> openOsfHandle, Func<int, int, int> dup2)
    {
        try
        {
            var fd = openOsfHandle(handle, 0x0001 /* _O_WRONLY */);
            if (fd >= 0) _ = dup2(fd, 2);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static int MsvcrtOpenOsfHandle(nint h, int flags) => msvcrt_open_osfhandle(h, flags);
    private static int MsvcrtDup2(int a, int b) => msvcrt_dup2(a, b);
    private static int UcrtOpenOsfHandle(nint h, int flags) => ucrt_open_osfhandle(h, flags);
    private static int UcrtDup2(int a, int b) => ucrt_dup2(a, b);

    [LibraryImport("msvcrt.dll", EntryPoint = "_open_osfhandle")] private static partial int msvcrt_open_osfhandle(nint handle, int flags);
    [LibraryImport("msvcrt.dll", EntryPoint = "_dup2")] private static partial int msvcrt_dup2(int fd1, int fd2);
    [LibraryImport("ucrtbase.dll", EntryPoint = "_open_osfhandle")] private static partial int ucrt_open_osfhandle(nint handle, int flags);
    [LibraryImport("ucrtbase.dll", EntryPoint = "_dup2")] private static partial int ucrt_dup2(int fd1, int fd2);

    /// <summary>Discards anything written so far (called at the start of each job).</summary>
    public static void Reset() => _ = Drain();

    /// <summary>Throws DecodeFailed if a codec wrote a diagnostic since the last call.</summary>
    public static void ThrowIfWarned()
    {
        var text = Drain();
        if (text is not null) throw new ImagingException(ErrorCode.DecodeFailed, $"The decoder reported damaged image data: {text}");
    }

    private static unsafe string? Drain()
    {
        if (_read is null) return null;
        var first = new StringBuilder();
        var buf = stackalloc byte[4096];
        while (PeekNamedPipe(_read, 0, 0, 0, out var available, 0) && available > 0)
        {
            if (!ReadFile(_read, buf, (uint)Math.Min(available, 4096u), out var got, 0) || got == 0) break;
            if (first.Length < 200) first.Append(Encoding.ASCII.GetString(buf, (int)got));
        }
        if (first.Length == 0) return null;
        var line = first.ToString().Trim().ReplaceLineEndings(" ");
        return line.Length > 200 ? line[..200] : line;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int which, nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekNamedPipe(SafeFileHandle pipe, nint buffer, uint size, nint read, out uint available, nint left);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(SafeFileHandle file, byte* buffer, uint toRead, out uint read, nint overlapped);
}
