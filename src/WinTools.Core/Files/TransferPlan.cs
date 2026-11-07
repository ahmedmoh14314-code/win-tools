namespace WinTools.Core.Files;

/// <summary>
/// The pure rules of Copy to and Move to (ADR-0032): which destination is acceptable, what an item is named there, and
/// which moves are refused. String checks only; the host does the file-system work (<c>FileTransfer</c>) and claims a
/// name by a move that fails when the name is taken, so nothing is ever overwritten.
/// </summary>
public static class TransferPlan
{
    /// <summary>
    /// The folder the dialog answered, as the host uses it: fully qualified, canonical, without a trailing separator
    /// (except a drive or share root), passing <see cref="PathPolicy"/> as a folder. Null for anything else.
    /// </summary>
    public static string? Destination(string? picked)
    {
        if (string.IsNullOrEmpty(picked) || !Path.IsPathFullyQualified(picked)) return null;
        string root;
        try { root = Path.GetPathRoot(picked) ?? ""; }
        catch (ArgumentException) { return null; }
        if (root.Length == 0) return null;
        var isRoot = string.Equals(Path.TrimEndingDirectorySeparator(picked), Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal);
        if (isRoot)
        {
            // "D:\" or "\\server\share\": a root has no name of its own, which PathPolicy refuses for a folder.
            var withSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return PathPolicy.Explain(withSeparator) is PathVerdict.Supported or PathVerdict.NoStem && Path.IsPathFullyQualified(withSeparator)
                ? withSeparator : null;
        }
        var folder = Path.TrimEndingDirectorySeparator(picked);
        return PathPolicy.IsSupported(folder + Path.DirectorySeparatorChar) ? folder : null;
    }

    /// <summary>The item's own path without the drop target's folder mark.</summary>
    public static string Plain(string item) => Path.TrimEndingDirectorySeparator(item);

    /// <summary>Whether a dropped item is a folder (the drop target marks folders with a trailing separator, ADR-0019).</summary>
    public static bool IsFolder(string item) => Path.EndsInDirectorySeparator(item);

    /// <summary>
    /// Names to try in the destination, in order: the item's own name, then "report (1).pdf", "report (2).pdf"... as
    /// every other command names its outputs (<see cref="Output.OutputNaming"/>). A folder keeps its whole name
    /// ("v1.2 (1)"); a file keeps its extension.
    /// </summary>
    public static IEnumerable<string> Candidates(string item, string destination)
    {
        var name = Path.GetFileName(Plain(item));
        if (name.Length == 0) throw new ArgumentException("The item has no name.", nameof(item));
        var (stem, ext) = IsFolder(item) ? (name, "") : (Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
        if (stem.Length == 0) (stem, ext) = (name, "");
        yield return Path.Combine(destination, name);
        for (var i = 1; i <= Output.OutputNaming.MaxCollisionIndex; i++)
            yield return Path.Combine(destination, $"{stem} ({i}){ext}");
    }

    /// <summary>
    /// Whether a folder would go into itself or one of its own subfolders: refused for Copy (it would copy itself without
    /// end) and Move (impossible). A file never is. Compared as Windows compares names, without case.
    /// </summary>
    public static bool IntoItself(string item, string destination)
    {
        if (!IsFolder(item)) return false;
        var folder = Plain(item) + Path.DirectorySeparatorChar;
        var target = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
        return target.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the item is already in the destination folder (Move has nothing to do; Copy makes a numbered copy).</summary>
    public static bool AlreadyThere(string item, string destination) =>
        string.Equals(Path.GetDirectoryName(Plain(item)), Path.TrimEndingDirectorySeparator(destination), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A name as a toast may show it: control and bidirectional-formatting characters, which could hide or reorder the
    /// text (JobFacts), become "?", and a very long name is cut with an ellipsis.
    /// </summary>
    public static string ShowableName(string name, int max = 80)
    {
        var chars = name.Select(c => char.IsControl(c) || c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\u200E' or '\u200F' or '\u061C' ? '?' : c).ToArray();
        var text = new string(chars);
        if (text.Length <= max) return text;
        var cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--; // never split a surrogate pair
        return text[..cut] + "…";
    }
}
