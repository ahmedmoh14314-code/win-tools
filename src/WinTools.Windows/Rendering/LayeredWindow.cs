using System.Runtime.InteropServices;
using WinTools.Windows.Native;

namespace WinTools.Windows.Rendering;

/// <summary>
/// A topmost, non-activating, per-pixel-alpha popup (WS_EX_LAYERED with UpdateLayeredWindow): it never takes focus or
/// appears in the taskbar or Alt+Tab. Pixels with alpha 0 let the mouse and OLE drags through; with
/// <paramref name="clickThrough"/> the whole window does (WS_EX_TRANSPARENT). The owner draws on a
/// <see cref="Surface"/> and calls <see cref="Present"/>; content is always set before the window is shown, so a stale
/// frame is never seen. Create, use and dispose on one thread with a message loop.
/// </summary>
internal sealed class LayeredWindow : IDisposable
{
    private static readonly HashSet<string> Registered = [];
    // One static window procedure for every class (kept alive for the process), routing by handle to the instance.
    private static readonly WndProc Proc = Route;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, LayeredWindow> Windows = new();
    private readonly Func<uint, nuint, nint, nint?>? _handler;

    public nint Handle { get; private set; }
    public int Left { get; private set; }
    public int Top { get; private set; }

    /// <param name="handler">Optional message handler; return a value to mark the message handled.</param>
    public LayeredWindow(string className, bool clickThrough, Func<uint, nuint, nint, nint?>? handler = null)
    {
        _handler = handler;
        var instance = Kernel32.GetModuleHandleW(0);
        lock (Registered)
        if (Registered.Add(className))
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = instance,
                hCursor = User32.LoadCursorW(0, 32512 /* IDC_ARROW */),
                lpszClassName = Marshal.StringToHGlobalUni(className),
            };
            if (User32.RegisterClassExW(wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
        }
        var ex = User32.WS_EX_TOPMOST | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW | Ui.WS_EX_LAYERED | (clickThrough ? Ui.WS_EX_TRANSPARENT : 0);
        Handle = User32.CreateWindowExW(ex, className, "WinTools", User32.WS_POPUP, 0, 0, 1, 1, 0, 0, instance, 0);
        if (Handle == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        Windows[Handle] = this;
    }

    public bool IsVisible => Handle != 0 && User32.IsWindowVisible(Handle);

    /// <summary>Sets the window's content and position from <paramref name="surface"/>; does not show or hide it.</summary>
    /// <summary>Returns false when the window refused the frame (UpdateLayeredWindow failed).</summary>
    public bool Present(Surface surface, int left, int top, byte opacity = 255)
    {
        Left = left;
        Top = top;
        return surface.Present(Handle, left, top, opacity);
    }

    /// <summary>Shows the window on top of the other topmost windows without activating it.</summary>
    public void Show()
    {
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW | Ui.SWP_NOMOVE | Ui.SWP_NOSIZE);
    }

    public void Hide()
    {
        if (IsVisible) User32.ShowWindow(Handle, User32.SW_HIDE);
    }

    public void StartTimer(nuint id, uint ms) => User32.SetTimer(Handle, id, ms, 0);
    public void StopTimer(nuint id) => User32.KillTimer(Handle, id);

    private static nint Route(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var self)) return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        if (msg == Msg.MOUSEACTIVATE) return User32.MA_NOACTIVATE;
        if (self._handler?.Invoke(msg, wParam, lParam) is { } handled) return handled;
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        Windows.TryRemove(Handle, out _);
        User32.DestroyWindow(Handle);
        Handle = 0;
    }
}
