namespace WinTools.Core.Files;

/// <summary>The broad family a kind of file belongs to; a module usually serves one family.</summary>
public enum FileFamily { Image, Pdf, Archive, Subtitle, Folder, Other }

/// <summary>
/// One kind of file the product recognises: a stable id (used in capability rules, settings and logs), its family, a
/// display name and the extensions that name it. At menu time a file's kind comes from its name only
/// (<see cref="FileKinds.IdentifyByName"/>); the worker that opens it checks the content (<see cref="FileSignature"/>).
/// </summary>
public sealed record FileKind(string Id, FileFamily Family, string DisplayName, IReadOnlyList<string> Extensions);

/// <summary>
/// The registry of file kinds: the image formats the image module reads (<see cref="All"/>), the PDF module's documents
/// (V2 PR 2.1), both recognised by extension, a folder, and any other file (<see cref="AnyFile"/>), which the General
/// Files module serves (V2 PR 1.4).
/// </summary>
public static class FileKinds
{
    public static FileKind Png { get; } = new("png", FileFamily.Image, "PNG", [".png"]);
    public static FileKind Jpeg { get; } = new("jpeg", FileFamily.Image, "JPEG", [".jpg", ".jpeg", ".jpe", ".jfif"]);
    public static FileKind WebP { get; } = new("webp", FileFamily.Image, "WebP", [".webp"]);
    public static FileKind Bmp { get; } = new("bmp", FileFamily.Image, "BMP", [".bmp", ".dib"]);
    public static FileKind Tiff { get; } = new("tiff", FileFamily.Image, "TIFF", [".tif", ".tiff"]);

    /// <summary>The image kinds (the image module's inputs).</summary>
    public static IReadOnlyList<FileKind> All { get; } = [Png, Jpeg, WebP, Bmp, Tiff];

    /// <summary>A PDF document (V2 PR 2.1). Its worker checks the "%PDF-" header before any engine sees the file.</summary>
    public static FileKind Pdf { get; } = new("pdf", FileFamily.Pdf, "PDF", [".pdf"]);

    /// <summary>A ZIP archive (V2 PR 5.1). Its worker checks the "PK" signature before reading it.</summary>
    public static FileKind Zip { get; } = new("zip", FileFamily.Archive, "ZIP", [".zip"]);

    /// <summary>TAR, TAR.GZ and single-file GZIP (V2 PR 5.2). ".tar.gz" is recognised before ".gz" (<see cref="ByExtensionOf"/>).</summary>
    public static FileKind Tar { get; } = new("tar", FileFamily.Archive, "TAR", [".tar"]);
    public static FileKind TarGz { get; } = new("tgz", FileFamily.Archive, "TAR.GZ", [".tgz"]);
    public static FileKind Gzip { get; } = new("gz", FileFamily.Archive, "GZIP", [".gz"]);

    /// <summary>SubRip and WebVTT subtitles (V2 PR 7.1). Text formats: the worker decodes and parses them, bounded.</summary>
    public static FileKind Srt { get; } = new("srt", FileFamily.Subtitle, "SRT", [".srt"]);
    public static FileKind Vtt { get; } = new("vtt", FileFamily.Subtitle, "WebVTT", [".vtt"]);

    /// <summary>A folder. The drop target marks dropped folders with a trailing separator (UX-009, ADR-0019).</summary>
    public static FileKind Folder { get; } = new("folder", FileFamily.Folder, "Folder", []);

    /// <summary>Any file whose extension no kind claims, or that has none: still a file, which general commands accept.</summary>
    public static FileKind AnyFile { get; } = new("file", FileFamily.Other, "File", []);

    /// <summary>Every kind a capability's input rule may name.</summary>
    public static IReadOnlyList<FileKind> Declared { get; } = [.. All, Pdf, Zip, Tar, TarGz, Gzip, Srt, Vtt, Folder, AnyFile];

    private static readonly Dictionary<string, FileKind> ByExtension =
        ((IEnumerable<FileKind>)[.. All, Pdf, Zip, Tar, TarGz, Gzip, Srt, Vtt]).SelectMany(k => k.Extensions.Select(e => (e, k))).ToDictionary(p => p.e, p => p.k, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, FileKind>.AlternateLookup<ReadOnlySpan<char>> BySpan = ByExtension.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// The kind a path's name says: a folder (trailing separator), the kind its extension names, or any file. Null only
    /// for no path. A hint only: never trusted for work.
    /// </summary>
    public static FileKind? IdentifyByName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Path.EndsInDirectorySeparator(path)) return Folder;
        return ByExtensionOf(path) ?? AnyFile;
    }

    /// <summary>The kind the extension names, or null when no kind claims it (none, or an unknown one).</summary>
    public static FileKind? ByExtensionOf(string path)
    {
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return TarGz; // the double extension names the archive, not the GZIP
        var ext = Path.GetExtension(path.AsSpan());
        return ext.Length > 0 && BySpan.TryGetValue(ext, out var kind) ? kind : null;
    }

    /// <summary>A kind by its id, or null.</summary>
    public static FileKind? Find(string id) => Declared.FirstOrDefault(k => k.Id == id);
}
