using System.Security.Cryptography;
using System.Text;

namespace WinTools.Core.Output;

/// <summary>
/// The hash a worker reports for an output folder (V2 PR 2.1): one SHA-256 over the folder's file names and their own
/// SHA-256s, sorted by name. A folder of 2,000 pages fits one protocol line this way, and the host recomputes it from
/// the disk, so a folder that differs in any file, name or count is not believed.
/// </summary>
public static class FolderManifest
{
    /// <summary>The manifest hash of (file name, lowercase hex SHA-256) pairs, in ordinal name order.</summary>
    public static string Hash(IEnumerable<(string Name, string Sha256)> files)
    {
        var text = new StringBuilder();
        foreach (var (name, sha) in files.OrderBy(f => f.Name, StringComparer.Ordinal))
            text.Append(name).Append('\t').Append(sha.ToLowerInvariant()).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The manifest hash of a folder's files as they are on disk (no subfolders are expected or allowed).</summary>
    public static string? HashOf(string folder)
    {
        var dir = new DirectoryInfo(folder);
        if (!dir.Exists || dir.LinkTarget is not null || dir.EnumerateDirectories().Any()) return null;
        var files = new List<(string, string)>();
        foreach (var f in dir.EnumerateFiles())
        {
            if (f.LinkTarget is not null) return null;
            using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add((f.Name, Convert.ToHexStringLower(SHA256.HashData(s))));
        }
        return Hash(files);
    }
}
