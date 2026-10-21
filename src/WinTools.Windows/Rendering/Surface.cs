using System.Runtime.InteropServices;
using WinTools.Windows.Native;

namespace WinTools.Windows.Rendering;

/// <summary>
/// A 32-bit premultiplied BGRA bitmap (a top-down DIB section) selected into a memory DC, with a GDI+ bitmap over the
/// same pixels. GDI+ draws the anti-aliased shapes, GDI draws ClearType text on top, and the result is handed to
/// UpdateLayeredWindow or saved as a PNG. Reused across frames; reallocated only when the size changes. One thread.
/// </summary>
internal sealed class Surface : IDisposable
{
    private nint _dc, _dib, _oldDib, _bitmap, _bits;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public nint Dc => _dc;

    public Surface() => Gdip.EnsureStarted();

    /// <summary>Makes the surface <paramref name="width"/>×<paramref name="height"/>, clears it to transparent and returns a painter for one frame.</summary>
    public Painter Begin(int width, int height, uint dpi)
    {
        if (width != Width || height != Height || _dc == 0) Allocate(Math.Max(1, width), Math.Max(1, height));
        Check(Gdip.GdipGetImageGraphicsContext(_bitmap, out var g));
        _ = Gdip.GdipSetSmoothingMode(g, Gdip.SmoothingModeAntiAlias);
        _ = Gdip.GdipSetPixelOffsetMode(g, Gdip.PixelOffsetModeHighQuality);
        // Default compositing quality: the gamma-corrected mode cost about 85 % of a frame (measured 4.2 ms against 0.6 ms at 120 DPI).
        _ = Gdip.GdipGraphicsClear(g, 0);
        return new Painter(this, g, dpi);
    }

    /// <summary>Sets alpha to opaque inside a rectangle: GDI text writes alpha 0 into the pixels it touches.</summary>
    public unsafe void MakeOpaque(int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, Width); right = Math.Clamp(right, 0, Width);
        top = Math.Clamp(top, 0, Height); bottom = Math.Clamp(bottom, 0, Height);
        var p = (uint*)_bits;
        for (var y = top; y < bottom; y++)
        {
            var row = p + (long)y * Width;
            for (var x = left; x < right; x++) row[x] |= 0xFF000000;
        }
    }

    /// <summary>Alpha of one pixel (tests and checks).</summary>
    public unsafe byte AlphaAt(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? (byte)0 : (byte)(((uint*)_bits)[(long)y * Width + x] >> 24);

    /// <summary>Puts the surface on screen as the content of a layered window at (<paramref name="left"/>, <paramref name="top"/>).</summary>
    public bool Present(nint hwnd, int left, int top, byte opacity = 255)
    {
        var dst = new POINT { X = left, Y = top };
        var size = new SIZE { Cx = Width, Cy = Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = Ui.AC_SRC_OVER, SourceConstantAlpha = opacity, AlphaFormat = Ui.AC_SRC_ALPHA };
        return Ui.UpdateLayeredWindow(hwnd, 0, dst, size, _dc, src, 0, blend, Ui.ULW_ALPHA);
    }

    public void SavePng(string path)
    {
        var encoder = Gdip.PngEncoder;
        Check(Gdip.GdipSaveImageToFile(_bitmap, path, encoder, 0));
    }

    private void Allocate(int width, int height)
    {
        Release();
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, // negative: top-down
            biPlanes = 1, biBitCount = 32, biCompression = 0, // BI_RGB
        };
        _dc = Gdi32.CreateCompatibleDC(0);
        _dib = Ui.CreateDIBSection(_dc, header, 0, out _bits, 0, 0);
        if (_dc == 0 || _dib == 0 || _bits == 0) { Release(); throw new InvalidOperationException("CreateDIBSection failed."); }
        _oldDib = Gdi32.SelectObject(_dc, _dib);
        Check(Gdip.GdipCreateBitmapFromScan0(width, height, width * 4, Gdip.PixelFormat32bppPARGB, _bits, out _bitmap));
        Width = width;
        Height = height;
    }

    private void Release()
    {
        if (_bitmap != 0) { _ = Gdip.GdipDisposeImage(_bitmap); _bitmap = 0; }
        if (_dc != 0 && _oldDib != 0) Gdi32.SelectObject(_dc, _oldDib);
        if (_dib != 0) { Gdi32.DeleteObject(_dib); _dib = 0; }
        if (_dc != 0) { Gdi32.DeleteDC(_dc); _dc = 0; }
        _oldDib = 0;
        _bits = 0;
        Width = Height = 0;
    }

    internal static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"GDI+ call failed (status {status}).");
    }

    public void Dispose() => Release();
}
