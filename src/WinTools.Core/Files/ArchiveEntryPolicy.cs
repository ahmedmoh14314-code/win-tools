namespace WinTools.Core.Files;

/// <summary>What an archive entry's name becomes on disk, or why it is not extracted.</summary>
/// <param name="RelativePath">A relative Windows path inside the extraction folder, or null when refused.</param>
/// <param name="Renamed">The name had to change to be valid on Windows (a trailing dot, a reserved name, a forbidden character).</param>
/// <param name="Refusal">Why the entry is skipped, in the user's terms; null when it is extracted.</param>
public sealed record EntryVerdict(string? RelativePath, bool IsFolder, bool Renamed, string? Refusal);

/// <summary>
/// The rules every archive entry's name passes before anything is written (V2 plan §14, ADR-0023). Archive names are
/// untrusted: a name may try to leave the extraction folder ("../x", "/etc", "C:\x", "\\server\x"), reach an alternate
/// data stream ("a:b"), or a device ("NUL", "COM1.txt"). Pure and deterministic: the same name always gives the same verdict.
/// <list type="bullet">
/// <item>Refused: empty names, absolute, drive-relative and UNC paths, any ".." component, a ':' anywhere, control
/// characters, a component longer than 255 characters, and names more than 32 components deep.</item>
/// <item>Renamed (and reported): trailing dots and spaces are trimmed (Windows would drop them, merging two names);
/// a reserved device name gets a leading '_'; characters Windows forbids in names become '_'.</item>
/// </list>
/// The extractor still checks that every final path lies inside its folder; this is the first of two fences.
/// </summary>
public static class ArchiveEntryPolicy
{
    public const int MaxComponentLength = 255, MaxDepth = 32;

    public static EntryVerdict Judge(string? entryName)
    {
        if (string.IsNullOrEmpty(entryName)) return Refuse("The entry has no name.");
        if (entryName.Any(char.IsControl)) return Refuse("The name holds control characters.");
        var name = entryName.Replace('\\', '/');
        var isFolder = name.EndsWith('/');
        if (name.StartsWith('/')) return Refuse("The name is an absolute path.");
        if (name.Contains(':', StringComparison.Ordinal)) return Refuse("The name names a drive or an alternate data stream.");
        var parts = new List<string>();
        var renamed = false;
        foreach (var raw in name.Split('/'))
        {
            if (raw.Length == 0 || raw == ".") continue;
            if (raw == "..") return Refuse("The name points outside the extraction folder.");
            var part = raw.TrimEnd('.', ' ');
            if (part.Length == 0) return Refuse("A part of the name is only dots or spaces.");
            if (part.Length != raw.Length) renamed = true;
            var cleaned = string.Create(part.Length, part, static (span, p) =>
            {
                for (var i = 0; i < p.Length; i++) span[i] = p[i] is '<' or '>' or '"' or '|' or '?' or '*' ? '_' : p[i];
            });
            if (cleaned != part) renamed = true;
            var stem = cleaned.Split('.')[0].TrimEnd(' ');
            if (PathPolicy.IsDeviceName(stem)) { cleaned = "_" + cleaned; renamed = true; } // the same list PathPolicy refuses (COM0, LPT0 too)
            if (cleaned.Length > MaxComponentLength) return Refuse("A part of the name is longer than 255 characters.");
            parts.Add(cleaned);
        }
        if (parts.Count == 0) return Refuse("The entry has no name.");
        if (parts.Count > MaxDepth) return Refuse($"The name is more than {MaxDepth} folders deep.");
        return new EntryVerdict(string.Join('\\', parts), isFolder, renamed, null);
    }

    /// <summary>Whether a final path stays inside the extraction folder (the second fence, after <see cref="Judge"/>).</summary>
    public static bool IsInside(string root, string fullPath)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(fullPath).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static EntryVerdict Refuse(string why) => new(null, false, false, why);
}

/// <summary>
/// Bounds on what one archive may make the worker write or read (V2 plan D13, ADR-0023), counted as bytes are actually
/// decompressed, never from the sizes the archive declares.
/// </summary>
public static class ArchiveLimits
{
    public const int MaxEntries = 100_000;
    public const long MaxTotalBytes = 20L << 30;

    /// <summary>An entry that expands to more than this many times its compressed size, past <see cref="RatioAfterBytes"/>, is a bomb.</summary>
    public const int MaxRatio = 200;
    public const long RatioAfterBytes = 1L << 20;

    /// <summary>Free space left on the destination drive after an extraction.</summary>
    public const long FreeSpaceReserve = 2L << 30;

    /// <summary>Whether <paramref name="written"/> bytes from an entry of <paramref name="compressed"/> bytes is beyond the ratio.</summary>
    public static bool IsBomb(long written, long compressed) => written > RatioAfterBytes && written > Math.Max(compressed, 1) * MaxRatio;

    /// <summary>
    /// The same rule for the whole archive: everything written against the archive's own size. Entries may declare any
    /// compressed size, or share one stored stream (overlapping entries), so the per-entry rule alone can be cheated
    /// (evaluator, PR 5.1).
    /// </summary>
    public static bool IsArchiveBomb(long totalWritten, long archiveBytes) => totalWritten > ArchiveRatioAfterBytes && IsBomb(totalWritten, archiveBytes);

    /// <summary>
    /// The whole-archive rule starts here: many small, very compressible files (logs) are ordinary, and the 20 GB cap and
    /// the free-space reserve bound the rest (second review, PR 5.1).
    /// </summary>
    public const long ArchiveRatioAfterBytes = 256L << 20;

    /// <summary>
    /// Counts what an extraction creates, files and folders alike (implicit parent folders included), against
    /// <see cref="MaxEntries"/>: a small archive of nested folder names must not create millions of folders (evaluator,
    /// PR 5.1). Also tells a file from a folder of the same name.
    /// </summary>
    public sealed class Budget(int max = MaxEntries)
    {
        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

        public int Created => _folders.Count + _files.Count;

        /// <summary>Records a folder and its parents; false when that would pass the limit or a file has that name.</summary>
        public bool AddFolder(string relative) => AddParents(relative, includeSelf: true);

        /// <summary>Records a file and its parent folders; false past the limit, or when a folder or file has that name.</summary>
        public bool AddFile(string relative)
        {
            if (_folders.Contains(relative) || _files.Contains(relative) || !AddParents(relative, includeSelf: false)) return false;
            if (Created + 1 > max) return false;
            _files.Add(relative);
            return true;
        }

        /// <summary>Whether a name is already a file (the extractor then picks "name (1)").</summary>
        public bool IsFile(string relative) => _files.Contains(relative);

        private bool AddParents(string relative, bool includeSelf)
        {
            var parts = relative.Split('\\');
            var count = includeSelf ? parts.Length : parts.Length - 1;
            var path = "";
            for (var i = 0; i < count; i++)
            {
                path = i == 0 ? parts[0] : path + "\\" + parts[i];
                if (_files.Contains(path)) return false; // a file stands where a folder must go
                if (_folders.Contains(path)) continue;
                if (Created + 1 > max) return false;
                _folders.Add(path);
            }
            return true;
        }
    }
}
