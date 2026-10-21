namespace WinTools.Core.Files;

/// <summary>Content-based format detection from the first bytes of a file (magic numbers).</summary>
public static class FileSignature
{
    /// <summary>Bytes needed to recognise every supported format.</summary>
    public const int HeaderLength = 8;

    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> Jpeg => [0xFF, 0xD8, 0xFF];

    public static ImageFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(Png)) return ImageFormat.Png;
        if (header.StartsWith(Jpeg)) return ImageFormat.Jpeg;
        return ImageFormat.Unknown;
    }
}
