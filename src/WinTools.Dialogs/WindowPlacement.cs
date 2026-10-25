using System.Windows;

namespace WinTools.Dialogs;

/// <summary>Where a dialog opens: near the drop point, inside the work area of the monitor that holds it.</summary>
internal static class WindowPlacement
{
    /// <summary>
    /// Near the drop point, kept inside the work area of the monitor that holds it. Everything is in physical pixels
    /// (the host's coordinates), so the monitor's own DPI and position need no conversion: WPF rescales the window when
    /// it lands on a monitor of another DPI, and the second move clamps its final size (evaluator, PR #17: it used the
    /// primary monitor's work area and the DPI of wherever the window first appeared).
    /// </summary>
    public static void NearPoint(Window window, int x, int y)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var monitor = Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (hwnd == 0 || !Native.GetMonitorInfoW(monitor, ref info)) return;
        Native.SetWindowPos(hwnd, 0, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        var work = info.rcWork;
        var cx = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - (r.Right - r.Left)));
        var cy = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - (r.Bottom - r.Top)));
        Native.SetWindowPos(hwnd, 0, cx, cy, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }
}
