namespace Spike.Interaction.Core.Native;

public static class WM
{
    public const uint CREATE = 0x0001, DESTROY = 0x0002, PAINT = 0x000F, CLOSE = 0x0010, QUIT = 0x0012, ERASEBKGND = 0x0014;
    public const uint SETCURSOR = 0x0020, MOUSEACTIVATE = 0x0021, NCHITTEST = 0x0084, TIMER = 0x0113;
    public const uint KEYDOWN = 0x0100, KEYUP = 0x0101, SYSKEYDOWN = 0x0104, SYSKEYUP = 0x0105;
    public const uint MOUSEMOVE = 0x0200, LBUTTONDOWN = 0x0201, LBUTTONUP = 0x0202, RBUTTONDOWN = 0x0204, RBUTTONUP = 0x0205;
    public const uint MBUTTONDOWN = 0x0207, MBUTTONUP = 0x0208, MOUSEWHEEL = 0x020A, XBUTTONDOWN = 0x020B, XBUTTONUP = 0x020C;
    public const uint DPICHANGED = 0x02E0;
    public const uint USER = 0x0400;
}

public static class WS
{
    public const uint POPUP = 0x80000000, VISIBLE = 0x10000000, CHILD = 0x40000000, OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint EX_TOPMOST = 0x00000008, EX_TOOLWINDOW = 0x00000080, EX_NOACTIVATE = 0x08000000;
    public const uint EX_LAYERED = 0x00080000, EX_TRANSPARENT = 0x00000020, EX_ACCEPTFILES = 0x00000010;
}

public static class SW
{
    public const int HIDE = 0, SHOWNOACTIVATE = 4, SHOW = 5, SHOWNA = 8;
}

public static class SWP
{
    public const uint NOSIZE = 0x0001, NOMOVE = 0x0002, NOZORDER = 0x0004, NOACTIVATE = 0x0010, SHOWWINDOW = 0x0040, HIDEWINDOW = 0x0080;
    public static readonly nint HWND_TOPMOST = new(-1);
}

public static class WH
{
    public const int KEYBOARD_LL = 13, MOUSE_LL = 14;
}

public static class VK
{
    public const int LBUTTON = 0x01, RBUTTON = 0x02, MBUTTON = 0x04, XBUTTON1 = 0x05, XBUTTON2 = 0x06;
    public const int BACK = 0x08, TAB = 0x09, RETURN = 0x0D, SHIFT = 0x10, CONTROL = 0x11, MENU = 0x12, ESCAPE = 0x1B, SPACE = 0x20;
    public const int LSHIFT = 0xA0, RSHIFT = 0xA1, LCONTROL = 0xA2, RCONTROL = 0xA3, LMENU = 0xA4, RMENU = 0xA5;
    public const int LWIN = 0x5B, RWIN = 0x5C, F1 = 0x70;
}

public static class LLKHF
{
    public const uint EXTENDED = 0x01, LOWER_IL_INJECTED = 0x02, INJECTED = 0x10, ALTDOWN = 0x20, UP = 0x80;
}

public static class LLMHF
{
    public const uint INJECTED = 0x01, LOWER_IL_INJECTED = 0x02;
}

public static class MK
{
    public const uint LBUTTON = 0x0001, RBUTTON = 0x0002, SHIFT = 0x0004, CONTROL = 0x0008, MBUTTON = 0x0010, ALT = 0x0020;
}

public static class DROPEFFECT
{
    public const uint NONE = 0, COPY = 1, MOVE = 2, LINK = 4, SCROLL = 0x80000000;
}

public static class HRESULTS
{
    public const int S_OK = 0, S_FALSE = 1, E_FAIL = unchecked((int)0x80004005), E_NOTIMPL = unchecked((int)0x80004001);
    public const int DRAGDROP_S_DROP = 0x00040100, DRAGDROP_S_CANCEL = 0x00040101, DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);
}

public static class CF
{
    public const ushort HDROP = 15;
}

public static class MONITOR
{
    public const uint DEFAULTTONULL = 0, DEFAULTTOPRIMARY = 1, DEFAULTTONEAREST = 2;
}

public static class DpiAwarenessContext
{
    public static readonly nint PER_MONITOR_AWARE_V2 = new(-4);
}

public static class INPUTTYPE
{
    public const uint MOUSE = 0, KEYBOARD = 1;
}

public static class MOUSEEVENTF
{
    public const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
    public const uint MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040, XDOWN = 0x0080, XUP = 0x0100, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;
}

public static class KEYEVENTF
{
    public const uint EXTENDEDKEY = 0x0001, KEYUP = 0x0002;
}

public static class GR
{
    public const uint GDIOBJECTS = 0, USEROBJECTS = 1;
}
