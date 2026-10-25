using WinTools.Core.Files;

namespace WinTools.Core.Output;

/// <summary>
/// Pure naming policy for conversion output. By default the output goes next to the source ("photo.png" becomes
/// "photo.jpg", then "photo (1).jpg", "photo (2).jpg"...); with an output folder from the settings it goes there under
/// the same names. The caller claims a name by an exclusive create/move and moves on to the next candidate when it is
/// taken, so the policy itself never checks the file system.
/// </summary>
public static class OutputNaming
{
    public const int MaxCollisionIndex = 999;

    public static string Extension(ImageFormat format) => ImageFormats.Extension(format);

    /// <summary>
    /// Candidate output paths in the order they must be tried. <paramref name="suffix"/> names the operation for outputs
    /// that keep the format ("photo (resized 50%).jpg"); a plain conversion has none ("photo.png").
    /// </summary>
    public static IEnumerable<string> Candidates(string sourcePath, ImageFormat target, string? suffix = null, string? outputDirectory = null) =>
        CandidatesWithExtension(sourcePath, Extension(target), suffix, outputDirectory);

    /// <summary>
    /// The same names for any module's file output, with the extension given (".pdf"): "report (merged).pdf", then
    /// "report (merged) (1).pdf"...
    /// </summary>
    public static IEnumerable<string> CandidatesWithExtension(string sourcePath, string extension, string? suffix = null, string? outputDirectory = null)
    {
        if (extension is not ['.', _, ..] || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.'))
            throw new ArgumentException("The extension must be a dot and ASCII letters or digits.", nameof(extension));
        var (dir, stem) = Split(sourcePath);
        dir = outputDirectory ?? dir;
        if (suffix is not null) stem = $"{stem} ({suffix})";
        yield return Path.Combine(dir, stem + extension);
        for (var i = 1; i <= MaxCollisionIndex; i++)
            yield return Path.Combine(dir, $"{stem} ({i}){extension}");
    }

    /// <summary>
    /// Candidate names of a folder that holds several outputs of one source ("report (pages)", "report (pages) (1)"...),
    /// claimed by renaming the staging folder, which fails when the name is taken.
    /// </summary>
    public static IEnumerable<string> FolderCandidates(string sourcePath, string suffix, string? outputDirectory = null)
    {
        var (dir, stem) = Split(sourcePath);
        dir = outputDirectory ?? dir;
        yield return Path.Combine(dir, $"{stem} ({suffix})");
        for (var i = 1; i <= MaxCollisionIndex; i++)
            yield return Path.Combine(dir, $"{stem} ({suffix}) ({i})");
    }

    /// <summary>
    /// The temporary file for one request, in the destination folder so the final step is a same-volume rename.
    /// Deterministic so the host can delete it if the worker dies mid-job. The name is obviously not a result.
    /// </summary>
    public static string TempPath(string sourcePath, string requestId, string? outputDirectory = null)
    {
        if (requestId.Length == 0 || requestId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Request id must be non-empty ASCII letters/digits.", nameof(requestId));
        var (dir, _) = Split(sourcePath);
        return Path.Combine(outputDirectory ?? dir, $"~wintools-{requestId}.tmp");
    }

    private static (string Dir, string Stem) Split(string sourcePath)
    {
        if (!Path.IsPathFullyQualified(sourcePath)) throw new ArgumentException("Source path must be fully qualified.", nameof(sourcePath));
        var dir = Path.GetDirectoryName(sourcePath) ?? throw new ArgumentException("Source path has no directory.", nameof(sourcePath));
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        if (stem.Length == 0) throw new ArgumentException("Source file name is empty.", nameof(sourcePath));
        return (dir, stem);
    }
}
