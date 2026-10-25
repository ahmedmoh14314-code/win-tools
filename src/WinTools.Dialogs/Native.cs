using System.Runtime.InteropServices;

namespace WinTools.Dialogs;

/// <summary>The few Win32 calls the pick list needs to place itself on the drop point's monitor.</summary>
internal static partial class Native
{
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)] public readonly record struct POINT(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [LibraryImport("user32.dll")] public static partial nint MonitorFromPoint(POINT pt, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
