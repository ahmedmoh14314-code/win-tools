using System.Runtime.InteropServices;

namespace WinTools.Windows.Native;

#pragma warning disable CA1069 // several message constants share values by design (WM_USER-based)

internal delegate nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam);
internal delegate nint HookProc(int nCode, nuint wParam, nint lParam);

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }

[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEXW
{
    public uint cbSize, style;
    public nint lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nuint dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nuint dwExtraInfo; }

internal static class Msg
{
    public const uint CLOSE = 0x0010, PAINT = 0x000F, ERASEBKGND = 0x0014, QUIT = 0x0012, TIMER = 0x0113;
    public const uint MOUSEACTIVATE = 0x0021;
    public const uint KEYDOWN = 0x0100, KEYUP = 0x0101, SYSKEYDOWN = 0x0104, SYSKEYUP = 0x0105;
    public const uint MOUSEMOVE = 0x0200, LBUTTONDOWN = 0x0201, LBUTTONUP = 0x0202;
    public const uint APP = 0x8000;
}

internal static partial class User32
{
    /// <summary>Lets the given process (a dialog the host just started) bring its window to the foreground.</summary>
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AllowSetForegroundWindow(uint processId);
    private const string Lib = "user32.dll";

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    public static readonly nint HWND_MESSAGE = -3;
    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    public const int SW_HIDE = 0;
    public const int MA_NOACTIVATE = 3;
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800;
    public const uint MB_ICONHAND = 0x10;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport(Lib, SetLastError = true)] public static partial ushort RegisterClassExW(in WNDCLASSEXW wc);
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string? windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport(Lib)] public static partial nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint hwnd);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport(Lib)] public static partial int SetWindowRgn(nint hwnd, nint rgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UpdateWindow(nint hwnd);
    [LibraryImport(Lib)] public static partial nint BeginPaint(nint hwnd, out PAINTSTRUCT ps);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EndPaint(nint hwnd, in PAINTSTRUCT ps);
    [LibraryImport(Lib)] public static partial int FillRect(nint hdc, in RECT rect, nint brush);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] public static partial int DrawTextW(nint hdc, string text, int count, ref RECT rect, uint format);
    [LibraryImport(Lib)] public static partial nint LoadCursorW(nint instance, nint name);
    [LibraryImport(Lib)] public static partial nint MonitorFromPoint(POINT pt, uint flags);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [LibraryImport(Lib)] public static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(in MSG msg);
    [LibraryImport(Lib)] public static partial nint DispatchMessageW(in MSG msg);
    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostThreadMessageW(uint threadId, uint msg, nuint wParam, nint lParam);
    [LibraryImport(Lib)] public static partial void PostQuitMessage(int exitCode);
    [LibraryImport(Lib, SetLastError = true)] public static partial nint SetWindowsHookExW(int idHook, nint proc, nint module, uint threadId);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnhookWindowsHookEx(nint hook);
    [LibraryImport(Lib)] public static partial nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [LibraryImport(Lib)] public static partial nuint SetTimer(nint hwnd, nuint id, uint elapseMs, nint proc);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint hwnd, nuint id);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool MessageBeep(uint type);
    [LibraryImport(Lib)] public static partial short GetAsyncKeyState(int vk);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] public static partial int MessageBoxW(nint owner, string text, string caption, uint type);
    [LibraryImport(Lib)] public static partial int GetSystemMetrics(int index);
    public const int SM_SWAPBUTTON = 23;
}

internal static partial class Gdi32
{
    private const string Lib = "gdi32.dll";
    public const int TRANSPARENT = 1, PS_SOLID = 0;
    public const uint SRCCOPY = 0x00CC0020;

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | g << 8 | b << 16);

    [LibraryImport(Lib)] public static partial nint CreateEllipticRgn(int l, int t, int r, int b);
    [LibraryImport(Lib)] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport(Lib)] public static partial nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [LibraryImport(Lib)] public static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint hdc);
    [LibraryImport(Lib)] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport(Lib)] public static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Pie(nint hdc, int l, int t, int r, int b, int x1, int y1, int x2, int y2);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Ellipse(nint hdc, int l, int t, int r, int b);
    [LibraryImport(Lib)] public static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport(Lib)] public static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
}

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll")] public static partial nint GetModuleHandleW(nint name);
    [LibraryImport("kernel32.dll")] public static partial uint GetCurrentThreadId();

    // CF_HDROP memory (DROPFILES): its real size, and a lock for reading it in place.
    public const uint CP_ACP = 0;
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial nuint GlobalSize(nint hMem);
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial nint GlobalLock(nint hMem);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GlobalUnlock(nint hMem);
    // Explorer drops a file beyond MAX_PATH as its 8.3 short path; this gives back the long one (up to 32,767 chars).
    public const int MaxLongPath = 32_768;
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint length);
    public const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOOPENFILEERRORBOX = 0x8000;
    public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF, FILE_ATTRIBUTE_DIRECTORY = 0x10;
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] public static partial uint GetFileAttributesW(string fileName);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetThreadErrorMode(uint newMode, out uint oldMode);
    [LibraryImport("kernel32.dll")] public static unsafe partial int MultiByteToWideChar(uint codePage, uint flags, byte* multiByte, int multiByteLength, char* wide, int wideLength);
}

/// <summary>Unicode normalization from Windows itself (Normaliz.dll, Windows Vista and later), which needs no ICU.</summary>
internal static partial class Normaliz
{
    public const int NormalizationKC = 5;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [LibraryImport("normaliz.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int NormalizeString(int normForm, string source, int sourceLength, [Out] char[]? destination, int destinationLength);
}

internal static partial class Shcore
{
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
