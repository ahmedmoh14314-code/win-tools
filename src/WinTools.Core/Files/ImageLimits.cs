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
