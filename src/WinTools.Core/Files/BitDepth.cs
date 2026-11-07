namespace WinTools.Core.Files;

/// <summary>Whether an image stores more than 8 bits per channel (16-bit PNG; TIFF with BitsPerSample above 8; 10- and
/// 12-bit HEIF and AVIF).</summary>
public static class BitDepth
{
    public static bool MoreThan8(ReadOnlySpan<byte> data, ImageFormat format) => format switch
    {
        ImageFormat.Png => data.Length > 24 && FileSignature.Detect(data) == ImageFormat.Png && data[24] > 8,
        ImageFormat.Tiff => TiffCheck.MaxBitsPerSample(data) > 8,
        ImageFormat.Heif or ImageFormat.Avif => Heif.Read(data, out _) is { BitDepth: > 8 },
        _ => false,
    };
}
