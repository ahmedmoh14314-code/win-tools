using System.Runtime.InteropServices;
using WinTools.Windows.Native;

namespace WinTools.Windows;

/// <summary>
/// A message-only window on the UI thread: receives the hook thread's posted signals and the session timers, and runs
/// the thread's message loop. Everything the interaction does happens on this thread.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const string ClassName = "WinTools.MessageWindow";
    private readonly WndProc _wndProc;
    private readonly Func<uint, nuint, nint, bool> _handler;

    public nint Handle { get; private set; }

    /// <param name="handler">Return true when the message was handled.</param>
    public MessageWindow(Func<uint, nuint, nint, bool> handler)
    {
        _handler = handler;
        _wndProc = (h, m, w, l) => m == Msg.CLOSE ? Quit() : _handler(m, w, l) ? 0 : User32.DefWindowProcW(h, m, w, l);
        var instance = Kernel32.GetModuleHandleW(0);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };
        if (User32.RegisterClassExW(wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
        Handle = User32.CreateWindowExW(0, ClassName, null, 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, instance, 0);
        if (Handle == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
    }

    private static nint Quit() { User32.PostQuitMessage(0); return 0; }

    /// <summary>Asks the loop to end; callable from any thread.</summary>
    public void RequestClose() => User32.PostMessageW(Handle, Msg.CLOSE, 0, 0);

    public void StartTimer(nuint id, TimeSpan due) => User32.SetTimer(Handle, id, (uint)due.TotalMilliseconds, 0);
    public void StopTimer(nuint id) => User32.KillTimer(Handle, id);

    public static bool IsTimer(uint msg) => msg == Msg.TIMER;

    /// <summary>Pumps messages for this thread until <see cref="RequestClose"/>.</summary>
    public static void RunLoop()
    {
        while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(msg);
            User32.DispatchMessageW(msg);
        }
    }

    /// <summary>Audible failure cue (no notification UI in this slice).</summary>
    public static void FailureBeep() => User32.MessageBeep(User32.MB_ICONHAND);

    /// <summary>
    /// Shows a plain informational message box (topmost, owned by no window) and returns when it is closed. Call it off
    /// the UI thread: it runs its own modal loop. Used for "Image info" until the UI phase designs a proper surface.
    /// </summary>
    public static void ShowInfo(string title, string text) => _ = User32.MessageBoxW(0, text, title, 0x40 | 0x40000 /* MB_ICONINFORMATION | MB_TOPMOST */);

    /// <summary>Audible "nothing to do" cue (the image already fits, or compression would not shrink it).</summary>
    public static void InfoBeep() => User32.MessageBeep(0x40 /* MB_ICONASTERISK */);

    public void Dispose()
    {
        if (Handle == 0) return;
        User32.DestroyWindow(Handle);
        Handle = 0;
    }
}

/// <summary>COM/OLE apartment for the UI thread (needed by RegisterDragDrop).</summary>
public static class OleApartment
{
    public static void Initialize()
    {
        var hr = Ole32.OleInitialize(0);
        if (hr < 0) throw new InvalidOperationException($"OleInitialize failed (0x{hr:X8}).");
    }

    public static void Uninitialize() => Ole32.OleUninitialize();
}
