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
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) return ImageFormat.Png;
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return ImageFormat.Jpeg;
        return ImageFormat.Unknown;
    }
}
