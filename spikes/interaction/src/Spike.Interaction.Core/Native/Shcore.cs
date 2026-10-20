using System.Runtime.InteropServices;

namespace Spike.Interaction.Core.Native;

public static partial class Shcore
{
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}

public static class MonitorInfo
{
    /// <summary>Work area (physical pixels) and DPI of the monitor containing a point.</summary>
    public static (RECT Work, RECT Bounds, uint Dpi, string Device) ForPoint(POINT pt)
    {
        var hmon = User32.MonitorFromPoint(pt, MONITOR.DEFAULTTONEAREST);
        var mi = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
        User32.GetMonitorInfoW(hmon, ref mi);
        uint dpi = 96;
        if (Shcore.GetDpiForMonitor(hmon, 0, out var dx, out _) == 0) dpi = dx;
        return (mi.rcWork, mi.rcMonitor, dpi, mi.Device);
    }
}
