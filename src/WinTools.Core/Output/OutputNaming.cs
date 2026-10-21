using WinTools.Core.Files;

namespace WinTools.Core.Output;

/// <summary>
/// Pure naming policy for conversion output. The output goes next to the source: "photo.png" becomes "photo.jpg",
/// then "photo (1).jpg", "photo (2).jpg"... The caller claims a name by an exclusive create/move and moves on to the
/// next candidate when it is taken, so the policy itself never checks the file system.
/// </summary>
public static class OutputNaming
{
    public const int MaxCollisionIndex = 999;

    public static string Extension(ImageFormat format) => ImageFormats.Extension(format);

    /// <summary>
    /// Candidate output paths in the order they must be tried. <paramref name="suffix"/> names the operation for outputs
    /// that keep the format ("photo (resized 50%).jpg"); a plain conversion has none ("photo.png").
    /// </summary>
    public static IEnumerable<string> Candidates(string sourcePath, ImageFormat target, string? suffix = null)
    {
        var (dir, stem) = Split(sourcePath);
        if (suffix is not null) stem = $"{stem} ({suffix})";
        var ext = Extension(target);
        yield return Path.Combine(dir, stem + ext);
        for (var i = 1; i <= MaxCollisionIndex; i++)
            yield return Path.Combine(dir, $"{stem} ({i}){ext}");
    }

    /// <summary>
    /// The temporary file for one request, in the destination folder so the final step is a same-volume rename.
    /// Deterministic so the host can delete it if the worker dies mid-job. The name is obviously not a result.
    /// </summary>
    public static string TempPath(string sourcePath, string requestId)
    {
        if (requestId.Length == 0 || requestId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Request id must be non-empty ASCII letters/digits.", nameof(requestId));
        var (dir, _) = Split(sourcePath);
        return Path.Combine(dir, $"~wintools-{requestId}.tmp");
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
