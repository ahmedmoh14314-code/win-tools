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
/// (old sources send lower-case aliases); a path that cannot be expanded (gone, no access, offline) is returned as it
/// was, and the result is judged by PathPolicy like any other dropped path.
/// <para>
/// A lookup can block for many seconds (a symlink on a fixed drive that points to an offline share took 21 s), so this
/// never runs on the UI thread: the drop target runs it as a task and picks the result up on a later DragOver. It stops
/// at <see cref="Deadline"/> or when the drag ends; what is left stays as dropped, and so stays refused. Windows' "no disk"
/// dialogs are suppressed on the thread doing it.
/// </para>
/// </summary>
internal static class ShortNames
{
    private const string Verbatim = @"\\?\";

    /// <summary>Total time one drag's expansion may take before the rest is left refused.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    /// <summary>Whether any path needs a lookup. Never for selections the menu would refuse anyway (too many files).</summary>
    public static bool NeedsExpansion(IReadOnlyList<string> paths) =>
        paths.Count <= CapabilityResolver.MaxFiles && paths.Any(p => PathPolicy.LooksLikeShortName(p, anyCase: true));

    public static string[] ExpandAll(string[] paths, CancellationToken cancel)
    {
        var buffer = new char[Kernel32.MaxLongPath];
        var previous = 0u;
        var errorModeSet = Kernel32.SetThreadErrorMode(Kernel32.SEM_FAILCRITICALERRORS | Kernel32.SEM_NOOPENFILEERRORBOX, out previous);
        try { return ExpandAll(paths, p => Expand(p, buffer), Stopwatch.GetTimestamp, cancel); }
        finally { if (errorModeSet) Kernel32.SetThreadErrorMode(previous, out _); }
    }

    /// <summary>The test seam: the expansion of one path and the clock (Stopwatch ticks).</summary>
    internal static string[] ExpandAll(string[] paths, Func<string, string> expand, Func<long> now, CancellationToken cancel)
    {
        var result = (string[])paths.Clone();
        var start = now();
        for (var i = 0; i < paths.Length; i++)
        {
            if (!PathPolicy.LooksLikeShortName(paths[i], anyCase: true)) continue;
            if (cancel.IsCancellationRequested || Stopwatch.GetElapsedTime(start, now()) >= Deadline) break;
            result[i] = expand(paths[i]);
        }
        return result;
    }

    private static string Expand(string path, char[] buffer)
    {
        var asked = path.StartsWith(@"\\", StringComparison.Ordinal) ? Verbatim + @"UNC\" + path[2..] : Verbatim + path;
        var n = Kernel32.GetLongPathNameW(asked, buffer, (uint)buffer.Length);
        if (n == 0 || n >= buffer.Length) return path;
        var expanded = new string(buffer, 0, (int)n);
        if (expanded.StartsWith(Verbatim + @"UNC\", StringComparison.Ordinal)) return @"\\" + expanded[(Verbatim.Length + 4)..];
        return expanded.StartsWith(Verbatim, StringComparison.Ordinal) ? expanded[Verbatim.Length..] : expanded;
    }
}
