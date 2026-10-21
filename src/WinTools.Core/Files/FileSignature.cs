namespace WinTools.Core.Files;

/// <summary>Content-based format detection from the first bytes of a file (magic numbers).</summary>
public static class FileSignature
{
    /// <summary>Bytes needed to recognise every supported format.</summary>
    public const int HeaderLength = 16;

    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> Jpeg => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> TiffLittle => [0x49, 0x49, 0x2A, 0x00];
    private static ReadOnlySpan<byte> TiffBig => [0x4D, 0x4D, 0x00, 0x2A];

    public static ImageFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(Png)) return ImageFormat.Png;
        if (header.StartsWith(Jpeg)) return ImageFormat.Jpeg;
        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)) return ImageFormat.WebP;
        if (header.StartsWith(TiffLittle) || header.StartsWith(TiffBig)) return ImageFormat.Tiff;
        if (header.Length >= 18 && header.StartsWith("BM"u8))
        {
            // BITMAPFILEHEADER is 14 bytes; the DIB header that follows starts with its own size.
            var dib = header[14] | header[15] << 8 | header[16] << 16 | header[17] << 24;
            if (dib is 12 or 40 or 52 or 56 or 64 or 108 or 124) return ImageFormat.Bmp;
        }
        return ImageFormat.Unknown;
    }
}
