namespace WinTools.Core.Files;

/// <summary>
/// Extension-based guess used only to decide which commands to offer while the user is still dragging.
/// It is never trusted for conversion: the worker checks the content signature and decodes the file (FileSignature).
/// </summary>
public static class FormatHint
{
    public static ImageFormat FromPath(string path) => Of(FileKinds.IdentifyByName(path));

    /// <summary>The image module's format for a file kind; Unknown for a kind that is not an image it reads.</summary>
    public static ImageFormat Of(FileKind? kind) => kind?.Id switch
    {
        "png" => ImageFormat.Png,
        "jpeg" => ImageFormat.Jpeg,
        "webp" => ImageFormat.WebP,
        "bmp" => ImageFormat.Bmp,
        "tiff" => ImageFormat.Tiff,
        _ => ImageFormat.Unknown,
    };
}
