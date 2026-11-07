using SkiaSharp;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// A collage (ADR-0031): 2 to 16 images in a grid, in the order they are given (the caller sorts them by name), each
/// fitted whole into a cell of one size, centred, on white, with a small even gap. The grid is as square as it can be
/// (2 → 2×1, 3–4 → 2×2, 5–6 → 3×2, 7–9 → 3×3, 10–12 → 4×3, 13–16 → 4×4); a last row that is not full is centred.
/// Cells take the typical (median) shape and width of the images, and the whole collage is at most
/// <see cref="MaxSide"/> pixels on its longer side, so 16 large photos make a sensible file, not a giant one.
/// The images are decoded one at a time and reduced at once, so memory stays that of one full image plus the collage.
/// </summary>
internal static class Collage
{
    public const int MaxSide = 6000;

    public static (int Columns, int Rows) Grid(int count)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        return (columns, (int)Math.Ceiling(count / (double)columns));
    }

    /// <summary>The cell size and gap for images of these sizes.</summary>
    public static (int CellWidth, int CellHeight, int Gap) Layout(IReadOnlyList<(int Width, int Height)> sizes)
    {
        var (columns, rows) = Grid(sizes.Count);
        var aspect = Median(sizes.Select(s => s.Width / (double)s.Height));
        double cellW = Median(sizes.Select(s => (double)s.Width));
        var cellH = cellW / aspect;
        // At most MaxSide on the longer side, gaps included (2 % of a cell).
        var scale = Math.Min(1.0, Math.Min(MaxSide / (columns * cellW * 1.02), MaxSide / (rows * cellH * 1.02)));
        cellW *= scale;
        cellH *= scale;
        var gap = (int)Math.Max(2, Math.Round(Math.Min(cellW, cellH) * 0.02));
        return ((int)Math.Max(1, Math.Round(cellW)), (int)Math.Max(1, Math.Round(cellH)), gap);
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    /// <summary>
    /// The largest a cell can be for this many images: each image is reduced to fit it as soon as it is decoded, so only
    /// one full-size image is in memory at a time.
    /// </summary>
    public static (int Width, int Height) CellBound(int count)
    {
        var (columns, rows) = Grid(count);
        return (MaxSide / columns, MaxSide / rows);
    }

    /// <summary>
    /// The image's pixels converted from its ICC profile to sRGB (a colour-managed copy through Skia, profile dropped), or
    /// null when the profile cannot be applied: Skia reads matrix-and-curve profiles, not table-based ones. An image with
    /// no profile is taken as sRGB already.
    /// </summary>
    public static unsafe RasterImage? ToSrgb(RasterImage image)
    {
        var plain = new RasterImage(image.Width, image.Height, image.Bgra, image.DpiX, image.DpiY);
        if (image.IccProfile is not { } icc) return plain;
        using var space = ReadProfile(icc);
        if (space is null) return null;
        if (space.IsSrgb) return plain;
        using var srgb = SKColorSpace.CreateSrgb();
        var source = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, space);
        var target = source.WithColorSpace(srgb);
        var output = new byte[image.Bgra.Length];
        fixed (byte* s = image.Bgra)
        fixed (byte* d = output)
        {
            using var pixmap = new SKPixmap(source, (nint)s, image.Width * 4);
            if (!pixmap.ReadPixels(target, (nint)d, image.Width * 4)) return null;
        }
        return new RasterImage(image.Width, image.Height, output, image.DpiX, image.DpiY);
    }

    /// <summary>Skia's reading of a profile, or null when it has none (it throws for bytes it cannot parse).</summary>
    private static SKColorSpace? ReadProfile(byte[] icc)
    {
        try { return SKColorSpace.CreateIcc(icc); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The image, reduced to fit within a box when it is larger (never enlarged).</summary>
    public static RasterImage FitWithin(RasterImage image, int width, int height)
    {
        var s = Math.Min(1.0, Math.Min(width / (double)image.Width, height / (double)image.Height));
        return s >= 1 ? image : Resampler.Resize(image, Math.Max(1, (int)Math.Round(image.Width * s)), Math.Max(1, (int)Math.Round(image.Height * s)));
    }

    /// <summary>
    /// Draws the images (orientation applied, already within <see cref="CellBound"/>); <paramref name="sizes"/> are their
    /// sizes as decoded, which set the layout.
    /// </summary>
    public static RasterImage Compose(IReadOnlyList<RasterImage> images, IReadOnlyList<(int Width, int Height)> sizes, byte[]? iccProfile)
    {
        var count = images.Count;
        var (columns, rows) = Grid(count);
        var (cellW, cellH, gap) = Layout(sizes);
        long width = columns * (long)cellW + (columns + 1L) * gap, height = rows * (long)cellH + (rows + 1L) * gap;
        var canvas = RasterImage.AllocateFor(width, height);
        canvas.AsSpan().Fill(0xFF); // opaque white
        int w = (int)width;
        for (var i = 0; i < count; i++)
        {
            var image = images[i];
            var (row, column) = (i / columns, i % columns);
            var inRow = row == rows - 1 ? count - row * columns : columns;
            var offset = (columns - inRow) * (cellW + gap) / 2; // a short last row is centred
            // The whole image in its cell, as large as fits, centred.
            var s = Math.Min(cellW / (double)image.Width, cellH / (double)image.Height);
            int fw = Math.Max(1, (int)Math.Round(image.Width * s)), fh = Math.Max(1, (int)Math.Round(image.Height * s));
            var fitted = fw == image.Width && fh == image.Height ? image : Resampler.Resize(image, fw, fh);
            var x0 = gap + column * (cellW + gap) + offset + (cellW - fw) / 2;
            var y0 = gap + row * (cellH + gap) + (cellH - fh) / 2;
            Blend(fitted, canvas, w, x0, y0);
        }
        return new RasterImage(w, (int)height, canvas, iccProfile: iccProfile);
    }

    /// <summary>Straight-alpha source over the opaque canvas.</summary>
    private static void Blend(RasterImage source, byte[] canvas, int canvasWidth, int x0, int y0)
    {
        var s = source.Bgra;
        for (var y = 0; y < source.Height; y++)
        {
            var from = y * source.Width * 4;
            var to = ((y0 + y) * canvasWidth + x0) * 4;
            for (var x = 0; x < source.Width * 4; x += 4)
            {
                int a = s[from + x + 3];
                if (a == 255) { s.AsSpan(from + x, 3).CopyTo(canvas.AsSpan(to + x)); continue; }
                for (var c = 0; c < 3; c++)
                    canvas[to + x + c] = (byte)((s[from + x + c] * a + canvas[to + x + c] * (255 - a) + 127) / 255);
            }
        }
    }

    /// <summary>Thrown for a collage that cannot be made, with the reason for the user.</summary>
    public static ImagingException Refused(string message) => new(ErrorCode.UnsupportedContent, message);
}
