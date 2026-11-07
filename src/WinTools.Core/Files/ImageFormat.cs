namespace WinTools.Core.Files;

/// <summary>Image container formats the product knows about. Unknown means "not something we handle".</summary>
public enum ImageFormat { Unknown, Png, Jpeg, WebP, Bmp, Tiff, Heif, Avif }

/// <summary>
/// Per-format facts the rest of the product derives from: which formats can be read and written, the output
/// extension, the display name, and whether the format keeps transparency.
/// </summary>
public static class ImageFormats
{
    /// <summary>Formats the worker can decode (input side of the capability matrix). HEIF (HEIC) and AVIF through the bundled
    /// FFmpeg (ADR-0031).</summary>
    public static IReadOnlyList<ImageFormat> Decodable { get; } = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP, ImageFormat.Bmp, ImageFormat.Tiff, ImageFormat.Heif, ImageFormat.Avif];

    /// <summary>Formats the worker can encode (output side), in the order the menu lists them. HEIF is read only: its
    /// encoder (HEVC) is not in the product.</summary>
    public static IReadOnlyList<ImageFormat> Encodable { get; } = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP, ImageFormat.Bmp, ImageFormat.Tiff, ImageFormat.Avif];

    public static bool CanDecode(ImageFormat format) => Decodable.Contains(format);
    public static bool CanEncode(ImageFormat format) => Encodable.Contains(format);

    /// <summary>Transparency survives in these outputs; JPEG and BMP outputs are composited onto white.</summary>
    public static bool KeepsAlpha(ImageFormat format) => format is ImageFormat.Png or ImageFormat.WebP or ImageFormat.Tiff or ImageFormat.Avif;

    /// <summary>
    /// The format an edit of a file of this format writes (resize, rotate, crop...): its own, or JPEG for HEIF, which the
    /// product reads but cannot write (the photo stays a photo every program opens).
    /// </summary>
    public static ImageFormat EditedAs(ImageFormat source) => source == ImageFormat.Heif ? ImageFormat.Jpeg : source;

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.WebP => ".webp",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Tiff => ".tif",
        ImageFormat.Avif => ".avif",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No output extension for this format."),
    };

    public static string DisplayName(ImageFormat format) => format switch
    {
        ImageFormat.Png => "PNG",
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.WebP => "WebP",
        ImageFormat.Bmp => "BMP",
        ImageFormat.Tiff => "TIFF",
        ImageFormat.Heif => "HEIC",
        ImageFormat.Avif => "AVIF",
        _ => format.ToString(),
    };
}
