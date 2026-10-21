using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Imaging;

/// <summary>
/// Decode → orient → encode, with every format meeting at <see cref="RasterImage"/>. Policies:
/// <list type="bullet">
/// <item>Transparency is kept for PNG, WebP and TIFF outputs and composited onto white for JPEG and BMP.</item>
/// <item>EXIF orientation (JPEG, TIFF, WebP) is applied to the pixels; outputs carry no EXIF.</item>
/// <item>Only the first frame is converted. Metadata (EXIF, GPS, ICC) is not copied.</item>
/// <item>Default encoder settings: JPEG quality 0.9 (the WIC default), WebP lossy quality 90.</item>
/// </list>
/// </summary>
internal static class ImagePipeline
{
    public readonly record struct Encoded(byte[] Bytes, int Width, int Height);

    public static Encoded Convert(byte[] input, ImageFormat source, ImageFormat target)
    {
        var (image, orientation) = Decode(input, source);
        image = Orientation.Apply(image, orientation);
        return new Encoded(Encode(image, target), image.Width, image.Height);
    }

    public static (RasterImage Image, int Orientation) Decode(byte[] bytes, ImageFormat format)
    {
        if (WicCodec.Handles(format)) return WicCodec.Decode(bytes, format);
        if (format == ImageFormat.WebP) return SkiaCodec.DecodeWebP(bytes);
        throw new ImagingException(ErrorCode.UnsupportedOperation, $"{format} cannot be decoded.");
    }

    public static byte[] Encode(RasterImage image, ImageFormat format)
    {
        if (WicCodec.Handles(format)) return WicCodec.Encode(image, format);
        if (format == ImageFormat.WebP) return SkiaCodec.EncodeWebP(image, SkiaCodec.DefaultWebPQuality, lossless: false);
        throw new ImagingException(ErrorCode.UnsupportedOperation, $"{format} cannot be encoded.");
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
