using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Windows.Native;

namespace WinTools.Windows.Overlay;

/// <summary>
/// Explorer drops a file whose full path is longer than MAX_PATH (260) as its 8.3 short path, so that legacy programs
/// can open it. <see cref="PathPolicy"/> refuses short names, because they are not the canonical name, so every such
/// file got no commands. This expands a dropped short path to the long path of the same file (GetLongPathNameW, asked
/// with the \\?\ prefix so the answer may exceed MAX_PATH). Only a path with an alias's shape is touched, in any case
/// (old sources send lower-case aliases), and only one PathPolicy would otherwise accept or calls a short name: a path
/// refused for another reason is never looked up. A lookup that fails or is never made (deadline, end of the drag) is
/// counted as unresolved, and the drop target then offers no commands: a lower-case alias PathPolicy cannot tell from
/// a chosen name must not run under its 8.3 name.
/// <para>
/// A lookup can block for many seconds (a symlink on a fixed drive that points to an offline share took 21 s), so this
/// never runs on the UI thread: the drop target runs it as a task and picks the result up on a later DragOver. It stops
/// at <see cref="Deadline"/> or when the drag ends (checked between lookups); what is left counts as unresolved. Windows' "no disk"
/// dialogs are suppressed on the thread doing it.
/// </para>
/// </summary>
internal static class ShortNames
{
    /// <summary>The paths after expansion, and how many alias-shaped ones could not be expanded.</summary>
    internal readonly record struct Expansion(string[] Paths, int Unresolved);

    private const string Verbatim = @"\\?\";

    /// <summary>Total time one drag's expansion may take before the rest is left refused.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    /// <summary>Whether any path needs a lookup. Never for selections the menu would refuse anyway (too many files).</summary>
    public static bool NeedsExpansion(IReadOnlyList<string> paths) =>
        paths.Count <= CapabilityResolver.MaxFiles && paths.Any(p => PathPolicy.LooksLikeShortName(p, anyCase: true));

    public static Expansion ExpandAll(string[] paths, CancellationToken cancel)
    {
        var buffer = new char[Kernel32.MaxLongPath];
        var previous = 0u;
        var errorModeSet = Kernel32.SetThreadErrorMode(Kernel32.SEM_FAILCRITICALERRORS | Kernel32.SEM_NOOPENFILEERRORBOX, out previous);
        try { return ExpandAll(paths, p => Expand(p, buffer), Stopwatch.GetTimestamp, cancel); }
        finally { if (errorModeSet) Kernel32.SetThreadErrorMode(previous, out _); }
    }

    /// <summary>The test seam: the expansion of one path (null when it fails) and the clock (Stopwatch ticks).</summary>
    internal static Expansion ExpandAll(string[] paths, Func<string, string?> expand, Func<long> now, CancellationToken cancel)
    {
        var result = (string[])paths.Clone();
        var start = now();
        var unresolved = 0;
        for (var i = 0; i < paths.Length; i++)
        {
            if (!PathPolicy.LooksLikeShortName(paths[i], anyCase: true)
                || PathPolicy.Explain(paths[i]) is not (PathVerdict.Supported or PathVerdict.ShortName)) continue;
            if (cancel.IsCancellationRequested || Stopwatch.GetElapsedTime(start, now()) >= Deadline) { unresolved++; continue; }
            if (expand(paths[i]) is { } expanded) result[i] = expanded;
            else unresolved++;
        }
        return new Expansion(result, unresolved);
    }

    private static string? Expand(string path, char[] buffer)
    {
        var asked = path.StartsWith(@"\\", StringComparison.Ordinal) ? Verbatim + @"UNC\" + path[2..] : Verbatim + path;
        var n = Kernel32.GetLongPathNameW(asked, buffer, (uint)buffer.Length);
        if (n == 0 || n >= buffer.Length) return null;
        var expanded = new string(buffer, 0, (int)n);
        if (expanded.StartsWith(Verbatim + @"UNC\", StringComparison.Ordinal)) return @"\\" + expanded[(Verbatim.Length + 4)..];
        return expanded.StartsWith(Verbatim, StringComparison.Ordinal) ? expanded[Verbatim.Length..] : expanded;
    }
}
