using SkiaSharp;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// The edits of ADR-0031 on the pipeline's straight-alpha BGRA pixels: flips, centred crops and the one-click
/// adjustments. Each keeps the image's resolution and colour profile (the pixels stay in the source's colour space, as
/// for resize and rotate). Tone and colour adjustments leave alpha as it is; blur and sharpen take transparency into
/// account (premultiplied), so no dark fringe appears at transparent edges.
/// </summary>
internal static class Edits
{
    public static RasterImage Flip(RasterImage image, FlipAxis axis) => Orientation.Apply(image, axis == FlipAxis.Horizontal ? 2 : 4);

    /// <summary>The largest centred rectangle of the ratio, or null when the image already has it (to the pixel).</summary>
    public static (int X, int Y, int Width, int Height)? CropRect(int width, int height, AspectRatio aspect)
    {
        long a = aspect.Width, b = aspect.Height;
        int w = width, h = height;
        if (width * b > height * a) w = (int)Math.Max(1, Math.Round(height * a / (double)b, MidpointRounding.AwayFromZero));
        else h = (int)Math.Max(1, Math.Round(width * b / (double)a, MidpointRounding.AwayFromZero));
        if (Math.Abs(w - width) <= 1 && Math.Abs(h - height) <= 1) return null;
        return ((width - w) / 2, (height - h) / 2, w, h);
    }

    public static RasterImage Crop(RasterImage image, AspectRatio aspect)
    {
        var (x, y, w, h) = CropRect(image.Width, image.Height, aspect)
            ?? throw new ImagingException(ErrorCode.NothingToDo, $"The image is already {aspect.Width}:{aspect.Height}; nothing to crop.");
        var pixels = RasterImage.AllocateFor(w, h);
        for (var row = 0; row < h; row++)
            image.Bgra.AsSpan(((y + row) * image.Width + x) * 4, w * 4).CopyTo(pixels.AsSpan(row * w * 4));
        return image.WithPixels(w, h, pixels);
    }

    public static RasterImage Adjust(RasterImage image, Adjustment adjustment) => adjustment switch
    {
        Adjustment.BlackAndWhite => Grey(image),
        Adjustment.Brighter => Curve(image, x => Math.Pow(x, 1 / 1.25)),  // lifts the mid-tones; black and white stay
        Adjustment.Darker => Curve(image, x => Math.Pow(x, 1.25)),
        Adjustment.MoreContrast => Curve(image, x => x + 0.7 * (0.5 - 0.5 * Math.Cos(Math.PI * x) - x)), // an S-curve: shadows down, highlights up
        Adjustment.MoreVivid => Saturate(image, 1.3),
        Adjustment.Sharpen => Sharpen(image),
        _ => Blur(image, BlurSigma(image)),
    };

    /// <summary>"Blur": clearly soft, the same on a photo of any size (0.6 % of the shorter side).</summary>
    internal static float BlurSigma(RasterImage image) => (float)Math.Max(2.0, Math.Min(image.Width, image.Height) * 0.006);

    private static RasterImage Curve(RasterImage image, Func<double, double> f)
    {
        var lut = new byte[256];
        for (var i = 0; i < 256; i++) lut[i] = (byte)Math.Clamp(Math.Round(255 * f(i / 255.0)), 0, 255);
        var output = (byte[])image.Bgra.Clone();
        for (var i = 0; i < output.Length; i += 4)
        {
            output[i] = lut[output[i]];
            output[i + 1] = lut[output[i + 1]];
            output[i + 2] = lut[output[i + 2]];
        }
        return image.WithPixels(image.Width, image.Height, output);
    }

    /// <summary>Luminance (Rec. 709 weights) computed in linear light, so the grey keeps each colour's brightness.</summary>
    private static RasterImage Grey(RasterImage image)
    {
        var toLinear = new double[256];
        for (var i = 0; i < 256; i++) { var c = i / 255.0; toLinear[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        const int steps = 4096;
        var toEncoded = new byte[steps + 1];
        for (var i = 0; i <= steps; i++) { var l = i / (double)steps; toEncoded[i] = (byte)Math.Round(255 * (l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055)); }
        var output = (byte[])image.Bgra.Clone();
        for (var i = 0; i < output.Length; i += 4)
        {
            var y = 0.0722 * toLinear[output[i]] + 0.7152 * toLinear[output[i + 1]] + 0.2126 * toLinear[output[i + 2]];
            var g = toEncoded[(int)Math.Round(Math.Clamp(y, 0, 1) * steps)];
            output[i] = output[i + 1] = output[i + 2] = g;
        }
        return image.WithPixels(image.Width, image.Height, output);
    }

    /// <summary>Colour pushed away from its grey (Rec. 709 luma) by <paramref name="factor"/>, clipped to the gamut.</summary>
    private static RasterImage Saturate(RasterImage image, double factor)
    {
        var output = (byte[])image.Bgra.Clone();
        for (var i = 0; i < output.Length; i += 4)
        {
            double b = output[i], g = output[i + 1], r = output[i + 2];
            var y = 0.0722 * b + 0.7152 * g + 0.2126 * r;
            output[i] = (byte)Math.Clamp(Math.Round(y + (b - y) * factor), 0, 255);
            output[i + 1] = (byte)Math.Clamp(Math.Round(y + (g - y) * factor), 0, 255);
            output[i + 2] = (byte)Math.Clamp(Math.Round(y + (r - y) * factor), 0, 255);
        }
        return image.WithPixels(image.Width, image.Height, output);
    }

    /// <summary>An unsharp mask: the difference from a slightly blurred copy, added back at 80 %. Alpha is kept.</summary>
    private static RasterImage Sharpen(RasterImage image)
    {
        var sigma = (float)Math.Clamp(Math.Min(image.Width, image.Height) / 1500.0, 1.0, 4.0);
        var soft = Blur(image, sigma).Bgra;
        var output = (byte[])image.Bgra.Clone();
        const double amount = 0.8;
        for (var i = 0; i < output.Length; i += 4)
            for (var c = 0; c < 3; c++)
                output[i + c] = (byte)Math.Clamp(Math.Round(output[i + c] + amount * (output[i + c] - soft[i + c])), 0, 255);
        return image.WithPixels(image.Width, image.Height, output);
    }

    /// <summary>A Gaussian blur through Skia, premultiplied, the edges extended (not faded into transparency).</summary>
    internal static unsafe RasterImage Blur(RasterImage image, float sigma)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var output = RasterImage.AllocateFor(image.Width, image.Height);
        fixed (byte* src = image.Bgra)
        fixed (byte* dst = output)
        {
            using var pixmap = new SKPixmap(info, (nint)src, image.Width * 4);
            using var source = SKImage.FromPixels(pixmap) ?? throw new ImagingException(ErrorCode.EncodeFailed, "The image could not be blurred.");
            using var surface = SKSurface.Create(info.WithAlphaType(SKAlphaType.Premul))
                ?? throw new ImagingException(ErrorCode.ImageTooLarge, "Not enough memory to blur this image.");
            using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src };
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawImage(source, 0, 0, SKSamplingOptions.Default, paint);
            surface.Canvas.Flush();
            if (!surface.ReadPixels(info, (nint)dst, image.Width * 4, 0, 0))
                throw new ImagingException(ErrorCode.EncodeFailed, "The image could not be blurred.");
        }
        return image.WithPixels(image.Width, image.Height, output);
    }
}
