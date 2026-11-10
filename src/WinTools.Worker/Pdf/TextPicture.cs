using System.Runtime.InteropServices;

namespace WinTools.Worker.Pdf;

/// <summary>
/// A line of text drawn as a picture by Windows' own GDI (Watermark): GDI shapes every script Windows does, so Arabic is
/// joined and runs right to left, which PDF's standard fonts cannot do. The text is drawn white on black with grey
/// antialiasing; that coverage becomes the alpha of a grey picture at the asked opacity. No font file is read or embedded.
/// </summary>
internal static partial class TextPicture
{
    /// <summary>The height of the text in pixels: sharp when the watermark spans a page.</summary>
    public const int FontPixels = 220;

    /// <summary>The widest picture drawn.</summary>
    public const int MaxWidth = 12_000;

    /// <summary>BGRA rows, not premultiplied, and the size; the grey is <paramref name="grey"/>, the opacity 1 to 100.</summary>
    public static (byte[] Bgra, int Width, int Height) Draw(string text, int opacityPercent, byte grey = 128) =>
        Draw(text, opacityPercent, (grey, grey, grey), "Segoe UI", 700);

    /// <summary>The same in a colour, face and weight of the caller's (a typed signature: Segoe Script).</summary>
    public static (byte[] Bgra, int Width, int Height) Draw(string text, int opacityPercent, (byte R, byte G, byte B) color, string face, int weight)
    {
        var rtl = text.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿');
        var flags = DtSingleLine | DtNoPrefix | DtCenter | DtVCenter | (rtl ? DtRtlReading : 0u);
        var dc = CreateCompatibleDC(0);
        if (dc == 0) throw new InvalidOperationException("GDI could not start.");
        var font = CreateFontW(-FontPixels, 0, 0, 0, weight, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, face);
        nint bitmap = 0, oldFont = 0, oldBitmap = 0;
        try
        {
            if (font == 0) throw new InvalidOperationException("The font could not be made.");
            oldFont = SelectObject(dc, font);
            var measure = new Rect();
            if (DrawTextW(dc, text, text.Length, ref measure, flags | DtCalcRect) == 0) throw new InvalidOperationException("The text could not be measured.");
            var pad = FontPixels / 5;
            var width = Math.Clamp(measure.Right - measure.Left + 2 * pad, 1, MaxWidth);
            var height = measure.Bottom - measure.Top + 2 * pad;
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new InvalidOperationException("The picture could not be made.");
            Marshal.Copy(new byte[width * height * 4], 0, bits, width * height * 4); // black, whatever the section held
            oldBitmap = SelectObject(dc, bitmap);
            _ = SetBkMode(dc, 1); // TRANSPARENT: the section starts black
            _ = SetTextColor(dc, 0x00FFFFFF);
            var area = new Rect { Left = 0, Top = 0, Right = width, Bottom = height };
            if (DrawTextW(dc, text, text.Length, ref area, flags) == 0) throw new InvalidOperationException("The text could not be drawn.");
            GdiFlush();
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var opacity = Math.Clamp(opacityPercent, 1, 100);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var coverage = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                (pixels[i], pixels[i + 1], pixels[i + 2]) = (color.B, color.G, color.R);
                pixels[i + 3] = (byte)(coverage * opacity / 100);
            }
            return (pixels, width, height);
        }
        finally
        {
            if (oldBitmap != 0) SelectObject(dc, oldBitmap);
            if (oldFont != 0) SelectObject(dc, oldFont);
            if (bitmap != 0) DeleteObject(bitmap);
            if (font != 0) DeleteObject(font);
            DeleteDC(dc);
        }
    }

    private const uint DtCenter = 0x1, DtVCenter = 0x4, DtSingleLine = 0x20, DtCalcRect = 0x400, DtNoPrefix = 0x800, DtRtlReading = 0x20000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GdiFlush();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int DrawTextW(nint dc, string text, int count, ref Rect rect, uint format);
}
