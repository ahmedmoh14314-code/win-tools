using System.Runtime.InteropServices;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>A failure with the code to report across the process boundary.</summary>
internal sealed class ImagingException(ErrorCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public ErrorCode Code { get; } = code;
}

/// <summary>
/// The neutral in-memory image every codec decodes to and encodes from: 32-bit BGRA with straight (not premultiplied)
/// alpha, rows packed (stride = width × 4), plus the resolution to carry into the output.
/// </summary>
internal sealed class RasterImage
{
    public const long MaxPixels = 100_000_000;
    public const int MaxDimension = 65_535;

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public double DpiX { get; }
    public double DpiY { get; }

    /// <summary>The source's embedded ICC colour profile, carried to every output so colours render the same; null if none.</summary>
    public byte[]? IccProfile { get; }

    public RasterImage(int width, int height, byte[] bgra, double dpiX = 96, double dpiY = 96, byte[]? iccProfile = null)
    {
        if (width <= 0 || height <= 0 || bgra.Length != (long)width * height * 4) throw new ArgumentException("Pixel buffer does not match the size.");
        // Only a well-formed RGB profile can describe these (always RGB) pixels; anything else is dropped here, at the source.
        var icc = iccProfile is not null && WinTools.Core.Files.IccProfile.IsUsableRgb(iccProfile) ? iccProfile : null;
        (Width, Height, Bgra, DpiX, DpiY, IccProfile) = (width, height, bgra, Sane(dpiX), Sane(dpiY), icc);
    }

    /// <summary>Same metadata (resolution, ICC), new pixels.</summary>
    public RasterImage WithPixels(int width, int height, byte[] bgra, bool swapDpi = false) =>
        new(width, height, bgra, swapDpi ? DpiY : DpiX, swapDpi ? DpiX : DpiY, IccProfile);

    /// <summary>Allocates the pixel buffer for a decode after checking the limits, so a hostile header cannot ask for gigabytes.</summary>
    public static byte[] AllocateFor(long width, long height)
    {
        if (width <= 0 || height <= 0) throw new ImagingException(ErrorCode.DecodeFailed, "Image has zero size.");
        if (width > MaxDimension || height > MaxDimension || width * height > MaxPixels)
            throw new ImagingException(ErrorCode.ImageTooLarge, $"Image is {width}x{height}; the limit is {MaxPixels:N0} pixels and {MaxDimension} per side.");
        try { return new byte[width * height * 4]; }
        catch (OutOfMemoryException ex) { throw new ImagingException(ErrorCode.ImageTooLarge, "Not enough memory for this image.", ex); }
    }

    /// <summary>True if any pixel is not fully opaque (decides whether an alpha channel is written at all).</summary>
    public bool HasTransparency()
    {
        var px = MemoryMarshal.Cast<byte, uint>(Bgra);
        foreach (var p in px) if (p < 0xFF00_0000u) return true;
        return false;
    }

    private static double Sane(double dpi) => double.IsFinite(dpi) && dpi is >= 1 and <= 10_000 ? dpi : 96;

    /// <summary>Straight-alpha BGRA over opaque white, rounded: c' = (c·a + 255·(255 − a)) / 255.</summary>
    internal static void CompositeOnWhite(ReadOnlySpan<byte> bgra, Span<byte> bgr)
    {
        for (int i = 0, o = 0; i + 3 < bgra.Length; i += 4, o += 3)
        {
            int a = bgra[i + 3], inv = 255 - a;
            bgr[o] = (byte)((bgra[i] * a + 255 * inv + 127) / 255);
            bgr[o + 1] = (byte)((bgra[i + 1] * a + 255 * inv + 127) / 255);
            bgr[o + 2] = (byte)((bgra[i + 2] * a + 255 * inv + 127) / 255);
        }
    }
}

/// <summary>EXIF orientation (1–8) applied to pixels, so outputs without EXIF display the same way.</summary>
internal static class Orientation
{
    public static RasterImage Apply(RasterImage src, int orientation)
    {
        if (orientation is < 2 or > 8) return src;
        int w = src.Width, h = src.Height;
        var swap = orientation >= 5;
        int dw = swap ? h : w, dh = swap ? w : h;
        var input = MemoryMarshal.Cast<byte, uint>(src.Bgra);
        var bytes = new byte[src.Bgra.Length];
        var output = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                // Displayed pixel (x, y) comes from stored pixel (sx, sy).
                var (sx, sy) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),                 // transpose
                    6 => (y, h - 1 - x),         // rotate 90° clockwise
                    7 => (w - 1 - y, h - 1 - x), // transverse
                    _ => (w - 1 - y, x),         // 8: rotate 270° clockwise
                };
                output[y * dw + x] = input[sy * w + sx];
            }
        }
        return src.WithPixels(dw, dh, bytes, swapDpi: swap);
    }
}
