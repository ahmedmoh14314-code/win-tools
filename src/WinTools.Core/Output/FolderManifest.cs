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

    /// <summary>
    /// The shape of a folder tree (V2 PR 5.1): one SHA-256 over every entry's relative path, and each file's size, in
    /// ordinal order. Extraction can write 20 GB; the host checks the tree it finds is the one the worker reported without
    /// reading it all again. Null when the tree holds a link or junction, or more than <paramref name="maxEntries"/> entries.
    /// Only a real link counts: OneDrive marks its files with a reparse point too, and they are ordinary data (evaluator,
    /// PR 5.1).
    /// </summary>
    public static string? ShapeOf(string folder, int maxEntries = 200_000)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists || root.LinkTarget is not null) return null;
        var lines = new List<string>();
        var pending = new Stack<DirectoryInfo>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var e in dir.EnumerateFileSystemInfos())
            {
                if (e.LinkTarget is not null) return null;
                if (lines.Count >= maxEntries) return null;
                var relative = Path.GetRelativePath(root.FullName, e.FullName).Replace('\\', '/');
                if (e is DirectoryInfo d) { lines.Add(relative + "/"); pending.Push(d); }
                else lines.Add(relative + "\t" + ((FileInfo)e).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
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
