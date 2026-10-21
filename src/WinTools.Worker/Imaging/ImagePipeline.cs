using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// Decode → orient → operation (convert, resize, rotate, compress) → encode, with every format meeting at
/// <see cref="RasterImage"/>. Policies:
/// <list type="bullet">
/// <item>Transparency is kept for PNG, WebP and TIFF outputs and composited onto white for JPEG and BMP.</item>
/// <item>EXIF orientation (JPEG, TIFF, WebP) is applied to the pixels; outputs carry no EXIF.</item>
/// <item>Only the first frame is converted. Metadata (EXIF, GPS, ICC) is not copied.</item>
/// <item>Conversion: JPEG quality 0.9 (WIC default), WebP lossy 90. Resize/rotate keep quality: JPEG 0.95, WebP lossy 95 or
/// lossless if the source was lossless.</item>
/// <item>Compress keeps the format and is refused (nothing to do) when the result would not be smaller.</item>
/// </list>
/// </summary>
internal static class ImagePipeline
{
    public readonly record struct Encoded(byte[] Bytes, int Width, int Height, ImageFormat Format);

    /// <summary>JPEG quality for re-encoding a JPEG whose pixels are only rotated or resized: high, to limit generation loss.</summary>
    private const float KeepJpegQuality = 0.95f;

    /// <summary>Runs one operation on the content of a file whose real format is <paramref name="source"/>.</summary>
    public static Encoded Run(byte[] input, ImageFormat source, Operation op)
    {
        var (image, orientation) = Decode(input, source);
        image = Orientation.Apply(image, orientation);
        var output = op.OutputFormat(source);
        var keepLossless = source == ImageFormat.WebP && SkiaCodec.IsLosslessWebP(input);
        byte[] bytes;
        switch (op.Kind)
        {
            case OperationKind.Convert:
                bytes = Encode(image, output);
                break;
            case OperationKind.Resize:
                var size = op.Resize!.TargetFor(image.Width, image.Height)
                    ?? throw new ImagingException(ErrorCode.NothingToDo, $"The image ({image.Width}x{image.Height}) already fits; nothing to resize.");
                image = Resampler.Resize(image, size.Width, size.Height);
                bytes = EncodeKeepingQuality(image, output, keepLossless);
                break;
            case OperationKind.Rotate:
                image = Orientation.Apply(image, op.RotateDegrees switch { 90 => 6, 270 => 8, _ => 3 });
                bytes = EncodeKeepingQuality(image, output, keepLossless);
                break;
            case OperationKind.Compress:
                bytes = Compress(image, output, op.Compress);
                if (bytes.LongLength >= input.LongLength)
                    throw new ImagingException(ErrorCode.NothingToDo, "This setting would not make the file smaller; no copy was written.");
                break;
            default:
                throw new ImagingException(ErrorCode.UnsupportedOperation, $"Operation {op.Id} is not supported.");
        }
        return new Encoded(bytes, image.Width, image.Height, output);
    }

    /// <summary>Plain conversion, kept for callers that only convert.</summary>
    public static Encoded Convert(byte[] input, ImageFormat source, ImageFormat target) => Run(input, source, OperationCatalog.Convert(target));

    public static (RasterImage Image, int Orientation) Decode(byte[] bytes, ImageFormat format)
    {
        if (WicCodec.Handles(format)) return WicCodec.Decode(bytes, format);
        if (format == ImageFormat.WebP) return SkiaCodec.DecodeWebP(bytes);
        throw new ImagingException(ErrorCode.UnsupportedOperation, $"{format} cannot be decoded.");
    }

    /// <summary>Default encoder settings for a conversion: JPEG 0.9 (the WIC default), WebP lossy 90.</summary>
    public static byte[] Encode(RasterImage image, ImageFormat format)
    {
        if (WicCodec.Handles(format)) return WicCodec.Encode(image, format);
        if (format == ImageFormat.WebP) return SkiaCodec.EncodeWebP(image, SkiaCodec.DefaultWebPQuality, lossless: false);
        throw new ImagingException(ErrorCode.UnsupportedOperation, $"{format} cannot be encoded.");
    }

    private static byte[] EncodeKeepingQuality(RasterImage image, ImageFormat format, bool losslessWebP) => format switch
    {
        ImageFormat.Jpeg => WicCodec.Encode(image, format, KeepJpegQuality),
        ImageFormat.WebP => SkiaCodec.EncodeWebP(image, losslessWebP ? 100 : 95, losslessWebP),
        _ => Encode(image, format),
    };

    /// <summary>JPEG and WebP: lossy at High 90 / Balanced 80 / Smallest 65. PNG: lossless only, verified pixel for pixel.</summary>
    private static byte[] Compress(RasterImage image, ImageFormat format, CompressLevel level)
    {
        var quality = level switch { CompressLevel.High => 90, CompressLevel.Small => 65, _ => 80 };
        switch (format)
        {
            case ImageFormat.Jpeg when level != CompressLevel.Lossless:
                return WicCodec.Encode(image, format, quality / 100f);
            case ImageFormat.WebP when level != CompressLevel.Lossless:
                return SkiaCodec.EncodeWebP(image, quality, lossless: false);
            case ImageFormat.Png when level == CompressLevel.Lossless:
                var png = SkiaCodec.EncodePngSmallest(image);
                var back = Decode(png, ImageFormat.Png).Image;
                if (!back.Bgra.AsSpan().SequenceEqual(image.Bgra) && !EqualIgnoringHiddenColour(back.Bgra, image.Bgra))
                    throw new ImagingException(ErrorCode.OutputValidationFailed, "Lossless compression changed pixels.");
                return png;
            default:
                throw new ImagingException(ErrorCode.UnsupportedOperation, $"No {level} compression for {format}.");
        }
    }

    /// <summary>Pixels equal, except colour under fully transparent pixels, which encoders may normalise.</summary>
    private static bool EqualIgnoringHiddenColour(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (a[i + 3] != b[i + 3]) return false;
            if (a[i + 3] != 0 && (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2])) return false;
        }
        return true;
    }

    /// <summary>Decodes every pixel of an encoded output and checks its format and size. Throws on any mismatch.</summary>
    public static void Validate(byte[] encoded, ImageFormat format, int expectedWidth, int expectedHeight)
    {
        if (FileSignature.Detect(encoded) != format) throw new ImagingException(ErrorCode.OutputValidationFailed, "Output signature does not match the target format.");
        RasterImage decoded;
        try { decoded = Decode(encoded, format).Image; }
        catch (ImagingException ex) { throw new ImagingException(ErrorCode.OutputValidationFailed, "Output does not decode: " + ex.Message, ex); }
        if (decoded.Width != expectedWidth || decoded.Height != expectedHeight)
            throw new ImagingException(ErrorCode.OutputValidationFailed, $"Output is {decoded.Width}x{decoded.Height}, expected {expectedWidth}x{expectedHeight}.");
    }
}
