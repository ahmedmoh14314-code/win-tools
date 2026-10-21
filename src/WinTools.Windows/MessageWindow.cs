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
    private const uint InvokeMessage = Msg.APP + 40;
    private readonly WndProc _wndProc;
    private readonly Func<uint, nuint, nint, bool> _handler;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public nint Handle { get; private set; }

    /// <param name="handler">Return true when the message was handled.</param>
    public MessageWindow(Func<uint, nuint, nint, bool> handler)
    {
        _handler = handler;
        _wndProc = (h, m, w, l) => m == Msg.CLOSE ? Quit() : m == InvokeMessage ? RunPosted() : _handler(m, w, l) ? 0 : User32.DefWindowProcW(h, m, w, l);
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

    /// <summary>Runs <paramref name="action"/> on this window's thread, after the messages already queued; callable from any thread.</summary>
    public void Post(Action action)
    {
        _posted.Enqueue(action);
        User32.PostMessageW(Handle, InvokeMessage, 0, 0);
    }

    /// <summary>Receives any exception a posted action throws (it runs inside the window procedure, where it would end the process).</summary>
    public Action<Exception>? OnError { get; set; }

    private nint RunPosted()
    {
        while (_posted.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { OnError?.Invoke(ex); }
        }
        return 0;
    }

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

    /// <summary>
    /// Shows a plain message box for a problem the user must see before the app can run at all (no tray icon or toast
    /// exists yet). Not used once the host is running.
    /// </summary>
    public static void ShowStartupError(string text) => _ = User32.MessageBoxW(0, text, "WinTools", 0x10 /* MB_ICONERROR */);

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
