using System.Runtime.InteropServices;
using WinTools.Windows.Native;

namespace WinTools.Windows.Shell;

/// <summary>One entry of the tray menu; null text is a separator.</summary>
public sealed record TrayCommand(string? Text, Action? Run, bool IsDefault = false)
{
    public static TrayCommand Separator { get; } = new(null, null);
}

/// <summary>
/// The notification-area icon (Shell_NotifyIcon, version 4) and its menu. It lives on a hidden tool window that is
/// never shown, so it also receives the "TaskbarCreated" broadcast and puts the icon back after Explorer restarts.
/// A left click or the context-menu key opens the menu; a double click runs the default command. Opening the menu
/// makes this process the foreground one for the menu's lifetime (TrackPopupMenu requires it); that is the user's own
/// click on the icon. Use on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const string ClassName = "WinTools.Tray";
    private const uint CallbackMessage = Msg.APP + 20;
    private static readonly WndProc Proc = Route; // kept alive for the process
    private static TrayIcon? _instance;

    private readonly IReadOnlyList<TrayCommand> _commands;
    private readonly uint _taskbarCreated;
    private readonly nint _hwnd;
    private nint _icon;
    private bool _ownsIcon;
    private string _tooltip;
    private bool _added;

    public TrayIcon(string tooltip, IReadOnlyList<TrayCommand> commands)
    {
        _tooltip = tooltip;
        _commands = commands;
        _taskbarCreated = Ui.RegisterWindowMessageW("TaskbarCreated");
        var instance = Kernel32.GetModuleHandleW(0);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = instance,
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };
        _ = User32.RegisterClassExW(wc); // fails harmlessly when already registered
        _instance = this;
        _hwnd = User32.CreateWindowExW(User32.WS_EX_TOOLWINDOW, ClassName, "WinTools", User32.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        (_icon, _ownsIcon) = LoadAppIcon(Ui.GetDpiForSystem());
        Add();
    }

    /// <summary>Whether the shell accepted the icon (false when there is no notification area, e.g. in some sessions).</summary>
    public bool Visible => _added;

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        if (_added) _ = Ui.Shell_NotifyIconW(Ui.NIM_MODIFY, Data(Ui.NIF_TIP | Ui.NIF_SHOWTIP));
    }

    /// <summary>The executable's own icon (resource 32512, set by ApplicationIcon) at the small-icon size, or the system default.</summary>
    private static (nint Icon, bool Owned) LoadAppIcon(uint dpi)
    {
        var cx = Ui.GetSystemMetricsForDpi(Ui.SM_CXSMICON, dpi);
        var cy = Ui.GetSystemMetricsForDpi(Ui.SM_CYSMICON, dpi);
        var icon = Ui.LoadImageW(Kernel32.GetModuleHandleW(0), 32512, Ui.IMAGE_ICON, cx, cy, Ui.LR_DEFAULTCOLOR);
        return icon != 0 ? (icon, true) : (Ui.LoadIconW(0, 32512 /* IDI_APPLICATION, shared: never destroyed */), false);
    }

    private unsafe NOTIFYICONDATAW Data(uint flags)
    {
        var d = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var tip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip;
        for (var i = 0; i < tip.Length; i++) d.szTip[i] = tip[i];
        return d;
    }

    private void Add()
    {
        _added = Ui.Shell_NotifyIconW(Ui.NIM_ADD, Data(Ui.NIF_MESSAGE | Ui.NIF_ICON | Ui.NIF_TIP | Ui.NIF_SHOWTIP));
        if (!_added) return;
        var v = Data(0);
        v.uVersion = Ui.NOTIFYICON_VERSION_4;
        _ = Ui.Shell_NotifyIconW(Ui.NIM_SETVERSION, v);
    }

    private static nint Route(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var self = _instance;
        if (self is null || hwnd != self._hwnd) return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        if (msg == self._taskbarCreated) { self.Add(); return 0; } // Explorer restarted: the icon is gone, add it again
        if (msg == CallbackMessage)
        {
            // Version 4: LOWORD(lParam) is the event, wParam carries the anchor point in screen coordinates.
            var ev = (uint)(lParam & 0xFFFF);
            var x = (short)(wParam & 0xFFFF);
            var y = (short)((wParam >> 16) & 0xFFFF);
            switch (ev)
            {
                case Ui.WM_CONTEXTMENU or Ui.NIN_SELECT or Ui.NIN_KEYSELECT: self.ShowMenu(x, y); break;
                case Ui.WM_LBUTTONDBLCLK: self._commands.FirstOrDefault(c => c.IsDefault)?.Run?.Invoke(); break;
            }
            return 0;
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = Ui.CreatePopupMenu();
        try
        {
            for (var i = 0; i < _commands.Count; i++)
            {
                var c = _commands[i];
                if (c.Text is null) Ui.AppendMenuW(menu, Ui.MF_SEPARATOR, 0, null);
                else Ui.AppendMenuW(menu, Ui.MF_STRING, (nuint)(i + 1), c.Text);
            }
            _ = Ui.SetForegroundWindow(_hwnd); // otherwise the menu does not close when the user clicks elsewhere
            var chosen = Ui.TrackPopupMenuEx(menu, Ui.TPM_RETURNCMD | Ui.TPM_RIGHTBUTTON | Ui.TPM_NONOTIFY | Ui.TPM_BOTTOMALIGN, x, y, _hwnd, 0);
            _ = User32.PostMessageW(_hwnd, Ui.WM_NULL, 0, 0);
            if (chosen > 0 && chosen <= _commands.Count) _commands[chosen - 1].Run?.Invoke();
        }
        finally
        {
            Ui.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added) { _ = Ui.Shell_NotifyIconW(Ui.NIM_DELETE, Data(0)); _added = false; }
        if (_icon != 0 && _ownsIcon) Ui.DestroyIcon(_icon);
        _icon = 0;
        User32.DestroyWindow(_hwnd);
        if (_instance == this) _instance = null;
    }
}
