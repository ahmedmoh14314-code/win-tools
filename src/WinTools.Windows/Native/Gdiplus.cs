using System.Runtime.InteropServices;

namespace WinTools.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PointF { public float X, Y; public PointF(float x, float y) { X = x; Y = y; } }

[StructLayout(LayoutKind.Sequential)]
internal struct GdiplusStartupInput { public uint GdiplusVersion; public nint DebugEventCallback; public int SuppressBackgroundThread, SuppressExternalCodecs; }

/// <summary>
/// The GDI+ flat API (gdiplus.dll, part of Windows), used for anti-aliased shapes on the overlay's 32-bit premultiplied
/// bitmaps. Colours are 0xAARRGGBB, not premultiplied: GDI+ premultiplies when it draws into a PARGB bitmap.
/// </summary>
internal static partial class Gdip
{
    private const string Lib = "gdiplus.dll";
    public const int PixelFormat32bppPARGB = 0x000E200B;
    public const int SmoothingModeAntiAlias = 4, PixelOffsetModeHighQuality = 4, CompositingQualityHighQuality = 2;
    public const int UnitPixel = 2, LineCapRound = 2, LineJoinRound = 2, FillModeAlternate = 0, WrapModeTileFlipXY = 3, FlushIntentionSync = 1;

    [LibraryImport(Lib)] public static partial int GdiplusStartup(out nuint token, in GdiplusStartupInput input, nint output);
    [LibraryImport(Lib)] public static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan0, out nint bitmap);
    [LibraryImport(Lib)] public static partial int GdipGetImageGraphicsContext(nint image, out nint graphics);
    [LibraryImport(Lib)] public static partial int GdipDeleteGraphics(nint graphics);
    [LibraryImport(Lib)] public static partial int GdipTranslateWorldTransform(nint graphics, float dx, float dy, int order);
    [LibraryImport(Lib)] public static partial int GdipScaleWorldTransform(nint graphics, float sx, float sy, int order);
    [LibraryImport(Lib)] public static partial int GdipDisposeImage(nint image);
    [LibraryImport(Lib)] public static partial int GdipSetSmoothingMode(nint graphics, int mode);
    [LibraryImport(Lib)] public static partial int GdipSetPixelOffsetMode(nint graphics, int mode);
    [LibraryImport(Lib)] public static partial int GdipSetCompositingQuality(nint graphics, int quality);
    [LibraryImport(Lib)] public static partial int GdipGraphicsClear(nint graphics, uint argb);
    [LibraryImport(Lib)] public static partial int GdipFlush(nint graphics, int intention);
    [LibraryImport(Lib)] public static partial int GdipCreateSolidFill(uint argb, out nint brush);
    [LibraryImport(Lib)] public static partial int GdipCreateLineBrush(in PointF p1, in PointF p2, uint c1, uint c2, int wrapMode, out nint brush);
    [LibraryImport(Lib)] public static partial int GdipDeleteBrush(nint brush);
    [LibraryImport(Lib)] public static partial int GdipCreatePen1(uint argb, float width, int unit, out nint pen);
    [LibraryImport(Lib)] public static partial int GdipSetPenStartCap(nint pen, int cap);
    [LibraryImport(Lib)] public static partial int GdipSetPenEndCap(nint pen, int cap);
    [LibraryImport(Lib)] public static partial int GdipSetPenLineJoin(nint pen, int join);
    [LibraryImport(Lib)] public static partial int GdipDeletePen(nint pen);
    [LibraryImport(Lib)] public static partial int GdipCreatePath(int fillMode, out nint path);
    [LibraryImport(Lib)] public static partial int GdipDeletePath(nint path);
    [LibraryImport(Lib)] public static partial int GdipAddPathArc(nint path, float x, float y, float w, float h, float start, float sweep);
    [LibraryImport(Lib)] public static partial int GdipAddPathLine(nint path, float x1, float y1, float x2, float y2);
    [LibraryImport(Lib)] public static partial int GdipClosePathFigure(nint path);
    [LibraryImport(Lib)] public static partial int GdipStartPathFigure(nint path);
    [LibraryImport(Lib)] public static partial int GdipFillPath(nint graphics, nint brush, nint path);
    [LibraryImport(Lib)] public static partial int GdipDrawPath(nint graphics, nint pen, nint path);
    [LibraryImport(Lib)] public static partial int GdipFillEllipse(nint graphics, nint brush, float x, float y, float w, float h);
    [LibraryImport(Lib)] public static partial int GdipDrawEllipse(nint graphics, nint pen, float x, float y, float w, float h);
    [LibraryImport(Lib)] public static partial int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);
    [LibraryImport(Lib)] public static partial int GdipDrawArc(nint graphics, nint pen, float x, float y, float w, float h, float start, float sweep);
    [LibraryImport(Lib)] public static unsafe partial int GdipDrawLines(nint graphics, nint pen, PointF* points, int count);
    [LibraryImport(Lib)] public static unsafe partial int GdipFillPolygon(nint graphics, nint brush, PointF* points, int count, int fillMode);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] public static partial int GdipSaveImageToFile(nint image, string file, in Guid encoder, nint parameters);

    /// <summary>The PNG encoder's CLSID (built into GDI+).</summary>
    public static readonly Guid PngEncoder = new("557CF406-1A04-11D3-9A73-0000F81EF32E");

    private static readonly Lazy<bool> Started = new(() =>
    {
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        return GdiplusStartup(out _, input, 0) == 0;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Starts GDI+ once for the process (it stays up until exit).</summary>
    public static void EnsureStarted()
    {
        if (!Started.Value) throw new InvalidOperationException("GdiplusStartup failed.");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE { public int Cx, Cy; }

[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth, biHeight;
    public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage;
    public int biXPelsPerMeter, biYPelsPerMeter;
    public uint biClrUsed, biClrImportant;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID, uFlags, uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState, dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

/// <summary>Layered windows, DIB sections, timers, menus and the notification area.</summary>
internal static partial class Ui
{
    public const uint WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020;
    public const uint ULW_ALPHA = 0x2;
    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4;
    public const int SW_SHOWNOACTIVATE = 4;
    public const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MBUTTONUP = 0x0208, WM_SETCURSOR = 0x0020, WM_NULL = 0, WM_COMMAND = 0x0111, WM_CONTEXTMENU = 0x007B;
    public const uint WM_LBUTTONDBLCLK = 0x0203, WM_DISPLAYCHANGE = 0x007E;
    public const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_GRAYED = 1;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80, TPM_BOTTOMALIGN = 0x20;
    public const uint IMAGE_ICON = 1, LR_DEFAULTCOLOR = 0;
    public const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    public const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateLayeredWindow(nint hwnd, nint hdcDst, in POINT dst, in SIZE size, nint hdcSrc, in POINT src, uint key, in BLENDFUNCTION blend, uint flags);
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint cursor);
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT pt);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessageW(string name);
    [LibraryImport("user32.dll")] public static partial nint LoadImageW(nint instance, nint name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] public static partial nint LoadIconW(nint instance, nint name);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(nint icon);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SystemParametersInfoW(uint action, uint param, out int value, uint winIni);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Shell_NotifyIconW(uint message, in NOTIFYICONDATAW data);
    [LibraryImport("gdi32.dll")] public static partial nint CreateDIBSection(nint hdc, in BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextExtentPoint32W(nint hdc, string text, int length, out SIZE size);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GdiFlush();
    [LibraryImport("winmm.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PlaySoundW(nint sound, nint module, uint flags);
}
