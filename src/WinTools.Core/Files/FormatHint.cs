namespace WinTools.Core.Files;

/// <summary>
/// Extension-based guess used only to decide which commands to offer while the user is still dragging.
/// It is never trusted for conversion: the worker checks the content signature and decodes the file (FileSignature).
/// </summary>
public static class FormatHint
{
    public static ImageFormat FromPath(string path)
    {
        var ext = Path.GetExtension(path.AsSpan());
        static bool Is(ReadOnlySpan<char> e, string candidate) => e.Equals(candidate, StringComparison.OrdinalIgnoreCase);
        if (Is(ext, ".png")) return ImageFormat.Png;
        if (Is(ext, ".jpg") || Is(ext, ".jpeg") || Is(ext, ".jpe") || Is(ext, ".jfif")) return ImageFormat.Jpeg;
        if (Is(ext, ".webp")) return ImageFormat.WebP;
        if (Is(ext, ".bmp") || Is(ext, ".dib")) return ImageFormat.Bmp;
        if (Is(ext, ".tif") || Is(ext, ".tiff")) return ImageFormat.Tiff;
        return ImageFormat.Unknown;
    }
}
