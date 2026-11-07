namespace WinTools.Core.Files;

/// <summary>
/// The largest image the product decodes or produces (ADR-0015). A worker's peak memory grows with the pixel count, up
/// to about 21 MB per megapixel (conversion to BMP, measured), so 50 MP stays under the worker memory cap with room to
/// spare. That covers 48 and 50 MP phone photos and most cameras; larger images are refused with ImageTooLarge before
/// any pixel is allocated.
/// </summary>
public static class ImageLimits
{
    public const long MaxPixels = 50_000_000;
    public const int MaxDimension = 65_535;
}

/// <summary>
/// How long an image job may take (ADR-0031). Ordinary work stays at the host's 60 s. Work that goes through the engine, a
/// HEIC or AVIF source or an AVIF result, and a Collage, which decodes up to 16 images in one request, gets twice that.
/// Host and worker use this one rule, on the file's real format: the worker answers <see cref="WorkerMargin"/> before the
/// host's limit, so a late job ends as a plain failure with its temporary files gone, not as a killed process.
/// </summary>
public static class ImageTimeLimits
{
    public static readonly TimeSpan Ordinary = TimeSpan.FromSeconds(60), Engine = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan WorkerMargin = TimeSpan.FromSeconds(15);

    public static TimeSpan For(bool collage, ImageFormat target, IEnumerable<ImageFormat> sources) =>
        collage || target == ImageFormat.Avif || sources.Any(s => s is ImageFormat.Heif or ImageFormat.Avif) ? Engine : Ordinary;
}
