using System.Runtime.InteropServices;

namespace Spike.Interaction.Core.Native;

public static partial class Gdi32
{
    private const string Lib = "gdi32.dll";

    [LibraryImport(Lib)] public static partial nint CreateEllipticRgn(int x1, int y1, int x2, int y2);
    [LibraryImport(Lib)] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport(Lib)] public static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint ho);
    [LibraryImport(Lib)] public static partial nint SelectObject(nint hdc, nint h);
    [LibraryImport(Lib)] public static partial nint GetStockObject(int i);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Ellipse(nint hdc, int left, int top, int right, int bottom);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Pie(nint hdc, int left, int top, int right, int bottom, int xr1, int yr1, int xr2, int yr2);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool MoveToEx(nint hdc, int x, int y, nint lppt);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool LineTo(nint hdc, int x, int y);
    [LibraryImport(Lib)] public static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport(Lib)] public static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport(Lib)] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport(Lib)] public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint hdc);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);
    public const uint SRCCOPY = 0x00CC0020;
    public const int TRANSPARENT = 1, PS_SOLID = 0, NULL_PEN = 8, NULL_BRUSH = 5, DC_BRUSH = 18;

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
