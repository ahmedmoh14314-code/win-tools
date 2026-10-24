namespace WinTools.Core.Files;

/// <summary>The broad family a kind of file belongs to; a module usually serves one family.</summary>
public enum FileFamily { Image, Folder }

/// <summary>
/// One kind of file the product recognises: a stable id (used in capability rules, settings and logs), its family, a
/// display name and the extensions that name it. At menu time a file's kind comes from its name only
/// (<see cref="FileKinds.IdentifyByName"/>); the worker that opens it checks the content (<see cref="FileSignature"/>).
/// </summary>
public sealed record FileKind(string Id, FileFamily Family, string DisplayName, IReadOnlyList<string> Extensions);

/// <summary>The registry of file kinds. Today: the image formats the image module reads.</summary>
public static class FileKinds
{
    public static FileKind Png { get; } = new("png", FileFamily.Image, "PNG", [".png"]);
    public static FileKind Jpeg { get; } = new("jpeg", FileFamily.Image, "JPEG", [".jpg", ".jpeg", ".jpe", ".jfif"]);
    public static FileKind WebP { get; } = new("webp", FileFamily.Image, "WebP", [".webp"]);
    public static FileKind Bmp { get; } = new("bmp", FileFamily.Image, "BMP", [".bmp", ".dib"]);
    public static FileKind Tiff { get; } = new("tiff", FileFamily.Image, "TIFF", [".tif", ".tiff"]);

    public static IReadOnlyList<FileKind> All { get; } = [Png, Jpeg, WebP, Bmp, Tiff];

    /// <summary>A folder. The drop target marks dropped folders with a trailing separator (UX-009, ADR-0019).</summary>
    public static FileKind Folder { get; } = new("folder", FileFamily.Folder, "Folder", []);

    private static readonly Dictionary<string, FileKind> ByExtension =
        All.SelectMany(k => k.Extensions.Select(e => (e, k))).ToDictionary(p => p.e, p => p.k, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, FileKind>.AlternateLookup<ReadOnlySpan<char>> BySpan = ByExtension.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The kind a path's extension names, or null when no module knows it. A hint only: never trusted for work.</summary>
    public static FileKind? IdentifyByName(string? path)
    {
        if (path is not null && Path.EndsInDirectorySeparator(path)) return Folder;
        var ext = Path.GetExtension(path.AsSpan());
        return ext.Length > 0 && BySpan.TryGetValue(ext, out var kind) ? kind : null;
    }

    /// <summary>A kind by its id, or null.</summary>
    public static FileKind? Find(string id) => All.FirstOrDefault(k => k.Id == id);
}
