using System.Runtime.InteropServices;

namespace WinTools.EndToEnd;

/// <summary>SendInput and window queries for the harness. Absolute moves use virtual-desktop coordinates.</summary>
internal static partial class Desktop
{
    public const int VkSpace = 0x20, VkEscape = 0x1B, VkControl = 0x11;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nuint extra; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION u; }

    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint SendInput(uint n, [In] INPUT[] inputs, int size);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")] public static partial nint WindowFromPoint(POINT p);
    [LibraryImport("user32.dll")] public static partial nint GetAncestor(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] public static partial uint GetDoubleClickTime();
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);
    /// <summary>Whether a key or button is down right now (VK_LBUTTON = 1).</summary>
    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static unsafe partial int GetClassNameW(nint hwnd, char* name, int max);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint hwnd, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll")] private static partial nint FindWindowExW(nint parent, nint after, nint className, nint title);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint hwnd);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    /// <summary>Work area of the monitor that holds most of <paramref name="hwnd"/> (the area the overlay is clamped to).</summary>
    public static RECT WorkAreaOf(nint hwnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref info)) throw new InvalidOperationException("GetMonitorInfo failed.");
        return info.rcWork;
    }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")] private static partial int GetWindowLong(nint hwnd, int index);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    /// <summary>Diagnostics for a refused injection: is the window there at all, and where.</summary>
    public static string StateOf(nint hwnd)
    {
        GetWindowRect(hwnd, out var r);
        _ = DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int));
        return $"visible={IsWindowVisible(hwnd)} iconic={IsIconic(hwnd)} cloaked={cloaked} topmost={(GetWindowLong(hwnd, -20) & 0x8) != 0} rect={r.Left},{r.Top},{r.Right},{r.Bottom}";
    }

    public static readonly nint HwndTopmost = -1, HwndNoTopmost = -2;
    public const uint SwpNoMove = 0x2, SwpNoSize = 0x1, SwpNoActivate = 0x10, SwpNoZOrder = 0x4;

    public static unsafe string ClassOf(nint hwnd)
    {
        var buf = stackalloc char[256];
        var n = GetClassNameW(hwnd, buf, 256);
        return n > 0 ? new string(buf, 0, n) : "";
    }
    /// <summary>Closes the visible message boxes (dialog class #32770) owned by <paramref name="pid"/>; returns how many.</summary>
    public static int CloseDialogsOf(int pid) => CloseWindowsOf(pid, "#32770");

    /// <summary>Posts WM_CLOSE to the visible top-level windows of class <paramref name="className"/> owned by <paramref name="pid"/>; returns how many.</summary>
    public static int CloseWindowsOf(int pid, string className)
    {
        var closed = 0;
        foreach (var w in VisibleWindowsOf(pid, className))
            if (PostMessageW(w, 0x0010 /* WM_CLOSE */, 0, 0)) closed++;
        return closed;
    }

    /// <summary>The visible top-level windows of class <paramref name="className"/> owned by <paramref name="pid"/>.</summary>
    public static List<nint> VisibleWindowsOf(int pid, string className)
    {
        var found = new List<nint>();
        for (nint w = FindWindowExW(0, 0, 0, 0); w != 0; w = FindWindowExW(0, w, 0, 0))
        {
            if (!IsWindowVisible(w) || ClassOf(w) != className) continue;
            _ = GetWindowThreadProcessId(w, out var owner);
            if (owner == (uint)pid) found.Add(w);
        }
        return found;
    }

    public static nint RootAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2 /* GA_ROOT */);

    public static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        Mouse(0x0001 | 0x8000 | 0x4000, (int)Math.Round((x - vx) * 65535.0 / (vw - 1)), (int)Math.Round((y - vy) * 65535.0 / (vh - 1)));
    }

    public static void GlideTo(int x, int y, int steps = 6, int stepMs = 12)
    {
        GetCursorPos(out var p);
        for (var i = 1; i <= steps; i++) { MoveTo(p.X + (x - p.X) * i / steps, p.Y + (y - p.Y) * i / steps); Thread.Sleep(stepMs); }
    }

    public static void LeftDown() => Mouse(0x0002);
    public static void LeftUp() => Mouse(0x0004);
    public static void KeyDown(int vk) => Key(vk, 0);
    public static void KeyUp(int vk) => Key(vk, 0x0002);
    public static void Tap(int vk, int holdMs = 40) { KeyDown(vk); Thread.Sleep(holdMs); KeyUp(vk); }

    private static void Mouse(uint flags, int dx = 0, int dy = 0) =>
        Send(new INPUT { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } } });

    private static void Key(int vk, uint flags) =>
        Send(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags } } });

    private static void Send(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1) throw new InvalidOperationException($"SendInput failed ({Marshal.GetLastPInvokeError()}).");
    }
}
