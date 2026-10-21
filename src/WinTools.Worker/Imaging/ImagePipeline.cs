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
    public static Encoded Run(byte[] input, ImageFormat source, Operation op, int? jpegQuality = null, int? webpQuality = null)
    {
        // Everything is processed at 8 bits per channel. Converting to another format says so in its docs; operations that
        // promise to keep the file as it is (lossless compression, rotate, resize) refuse deeper images instead.
        if (op.Kind != OperationKind.Convert && BitDepth.MoreThan8(input, source))
            throw new ImagingException(ErrorCode.UnsupportedContent, "16-bit images are not supported by this operation yet (it would reduce them to 8 bits); converting is possible.");
        var (image, orientation) = Decode(input, source);
        image = Orientation.Apply(image, orientation);
        var output = op.OutputFormat(source);
        var keepLossless = source == ImageFormat.WebP && SkiaCodec.IsLosslessWebP(input);
        byte[] bytes;
        switch (op.Kind)
        {
            case OperationKind.Convert:
                bytes = output switch
                {
                    ImageFormat.Jpeg when jpegQuality is { } q => WicCodec.Encode(image, output, Math.Clamp(q, 1, 100) / 100f),
                    ImageFormat.WebP when webpQuality is { } q => SkiaCodec.EncodeWebP(image, Math.Clamp(q, 1, 100), lossless: false),
                    _ => Encode(image, output),
                };
                break;
            case OperationKind.StripMetadata:
                return StripMetadata(input, source, image, orientation);
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
                bytes = Compress(image, output, op.Compress, input);
                if (bytes.LongLength >= input.LongLength)
                    throw new ImagingException(ErrorCode.NothingToDo, "This setting would not make the file smaller; no copy was written.");
                break;
            default:
                throw new ImagingException(ErrorCode.UnsupportedOperation, $"Operation {op.Id} is not supported.");
        }
        return new Encoded(bytes, image.Width, image.Height, output);
    }

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
        ImageFormat.Jpeg => WicCodec.Encode(image, format, KeepJpegQuality, fullChroma: true),
        ImageFormat.WebP => SkiaCodec.EncodeWebP(image, losslessWebP ? 100 : 95, losslessWebP),
        _ => Encode(image, format),
    };

    /// <summary>
    /// Keeps only what the image needs and drops the rest without re-encoding: byte-level from an allow-list for JPEG,
    /// PNG and WebP (the orientation kept as a minimal EXIF tag), a pixel-identical re-encode for TIFF (orientation
    /// applied). The result is verified: the same pixels as shown before, and every byte of the file belongs to an
    /// allowed element (<see cref="Metadata.Residue"/>, a walk of the output independent of the removal).
    /// </summary>
    private static Encoded StripMetadata(byte[] input, ImageFormat source, RasterImage shown, int orientation)
    {
        if (!Metadata.Inspect(input, source).AnyRemovable)
            throw new ImagingException(ErrorCode.NothingToDo, "This file has no metadata to remove.");
        if (Metadata.HasSecondaryImage(input, source))
            throw new ImagingException(ErrorCode.UnsupportedContent, "This photo contains a second image (HDR gain map, motion photo or multi-picture); removing metadata would drop it, so no copy was written.");
        var stripped = source == ImageFormat.Tiff ? Encode(shown, source)
            : Metadata.Strip(input, source, orientation)
              ?? throw new ImagingException(ErrorCode.DecodeFailed, "The file's structure could not be followed to its end, so metadata cannot be removed safely.");
        var (decoded, storedOrientation) = Decode(stripped, source);
        var after = Orientation.Apply(decoded, storedOrientation);
        if (after.Width != shown.Width || after.Height != shown.Height || !EqualIgnoringHiddenColour(after.Bgra, shown.Bgra))
            throw new ImagingException(ErrorCode.OutputValidationFailed, "Removing metadata changed the image.");
        if (Metadata.Residue(stripped, source) is { Count: > 0 } residue)
            throw new ImagingException(ErrorCode.OutputValidationFailed, $"Metadata is still present after removal ({residue[0]}).");
        if (Metadata.Inspect(stripped, source).AnyRemovable)
            throw new ImagingException(ErrorCode.OutputValidationFailed, "Metadata is still present after removal.");
        return new Encoded(stripped, decoded.Width, decoded.Height, source);
    }

    /// <summary>Facts for "Image info": nothing is written.</summary>
    public static ImageInfo Describe(byte[] input, ImageFormat format)
    {
        var (image, orientation) = Decode(input, format);
        var shown = Orientation.Apply(image, orientation);
        var meta = Metadata.Inspect(input, format);
        return new ImageInfo(ImageFormats.DisplayName(format), shown.Width, shown.Height, input.LongLength, Math.Round(image.DpiX, 1), Math.Round(image.DpiY, 1),
            image.HasTransparency(), orientation, meta.Exif, meta.Gps, meta.Xmp, meta.Icc || image.IccProfile is not null, meta.Text, BitDepth.MoreThan8(input, format), meta.Other);
    }

    /// <summary>JPEG and WebP: lossy at High 90 / Balanced 80 / Smallest 65. PNG: lossless only, verified pixel for pixel.</summary>
    private static byte[] Compress(RasterImage image, ImageFormat format, CompressLevel level, byte[] input)
    {
        var quality = level switch { CompressLevel.High => 90, CompressLevel.Small => 65, _ => 80 };
        switch (format)
        {
            case ImageFormat.Jpeg when level != CompressLevel.Lossless:
                return WicCodec.Encode(image, format, quality / 100f);
            case ImageFormat.WebP when level != CompressLevel.Lossless:
                return SkiaCodec.EncodeWebP(image, quality, lossless: false);
            case ImageFormat.Png when level == CompressLevel.Lossless:
                var png = SkiaCodec.EncodePngSmallest(image, input);
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
