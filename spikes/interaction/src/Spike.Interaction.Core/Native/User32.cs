using System.Runtime.InteropServices;

namespace Spike.Interaction.Core.Native;

public delegate nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam);
public delegate nint HookProc(int nCode, nuint wParam, nint lParam);

public static partial class User32
{
    private const string Lib = "user32.dll";

    [LibraryImport(Lib, SetLastError = true)] public static partial nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);
    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnhookWindowsHookEx(nint hhk);
    [LibraryImport(Lib)] public static partial nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [LibraryImport(Lib, SetLastError = true)] public static partial ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle, int x, int y, int w, int h, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);
    [LibraryImport(Lib)] public static partial nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint hWnd);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint hWnd, int nCmdShow);
    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(nint hWnd);
    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(nint hWnd, nint lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UpdateWindow(nint hWnd);
    [LibraryImport(Lib)] public static partial int SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);
    [LibraryImport(Lib, SetLastError = true)] public static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);
    [LibraryImport(Lib, SetLastError = true)] public static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);
    public const int GWL_EXSTYLE = -20, GWL_STYLE = -16;

    [LibraryImport(Lib)] public static partial nint BeginPaint(nint hWnd, out PAINTSTRUCT lpPaint);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(nint hWnd, out RECT lpRect);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(ref MSG lpMsg);
    [LibraryImport(Lib)] public static partial nint DispatchMessageW(ref MSG lpMsg);
    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport(Lib)] public static partial void PostQuitMessage(int nExitCode);
    [LibraryImport(Lib, SetLastError = true)] public static partial nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint hWnd, nuint uIDEvent);

    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT lpPoint);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetCursorPos(int x, int y);
    [LibraryImport(Lib)] public static partial short GetAsyncKeyState(int vKey);
    [LibraryImport(Lib)] public static partial short GetKeyState(int vKey);
    [LibraryImport(Lib, SetLastError = true)] public static partial uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);
    [LibraryImport(Lib)] public static partial nint GetMessageExtraInfo();

    [LibraryImport(Lib)] public static partial nint MonitorFromPoint(POINT pt, uint dwFlags);
    [LibraryImport(Lib)] public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);
    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);
    [LibraryImport(Lib)] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport(Lib)] public static partial uint GetDpiForSystem();
    [LibraryImport(Lib, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetProcessDpiAwarenessContext(nint value);
    [LibraryImport(Lib)] public static partial int GetSystemMetrics(int nIndex);
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79, SM_CMONITORS = 80;

    [LibraryImport(Lib)] public static partial nint GetForegroundWindow();
    [LibraryImport(Lib)] public static partial nint GetActiveWindow();
    [LibraryImport(Lib)] public static partial nint GetFocus();
    [LibraryImport(Lib)] public static partial nint WindowFromPoint(POINT pt);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] public static partial int GetClassNameW(nint hWnd, Span<char> lpClassName, int nMaxCount);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] public static partial int GetWindowTextW(nint hWnd, Span<char> lpString, int nMaxCount);
    [LibraryImport(Lib)] public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [LibraryImport(Lib)] public static partial uint GetGuiResources(nint hProcess, uint uiFlags);
    [LibraryImport(Lib)] public static partial nint LoadCursorW(nint hInstance, nint lpCursorName);
    public static readonly nint IDC_ARROW = new(32512);
    [LibraryImport(Lib)] public static partial nint SetCursor(nint hCursor);
    [LibraryImport(Lib)] public static partial int FillRect(nint hDC, ref RECT lprc, nint hbr);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] public static partial int DrawTextW(nint hdc, string lpchText, int cchText, ref RECT lprc, uint format);
    public const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20;

    public static string ClassNameOf(nint hwnd)
    {
        Span<char> buf = stackalloc char[256];
        var n = GetClassNameW(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf[..n]) : string.Empty;
    }

    public static string WindowTextOf(nint hwnd)
    {
        Span<char> buf = stackalloc char[512];
        var n = GetWindowTextW(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf[..n]) : string.Empty;
    }
}
