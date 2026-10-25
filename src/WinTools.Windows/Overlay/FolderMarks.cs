using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Windows.Native;

namespace WinTools.Windows.Overlay;

/// <summary>
/// Marks dropped folders with a trailing separator, the Windows convention for a directory path, so that from there on
/// a folder is a kind like any other (UX-009, ADR-0019). Only names whose extension no module's kind claims are asked
/// about (none, or an unknown one such as "Photos 2024.10"): a folder named like a file ("holiday.png") is caught by the
/// worker instead. The lookups run on the drop target's inspection task,
/// under one deadline shared with short-name expansion and with Windows' error boxes suppressed; a path that cannot be
/// asked about stays as it was.
/// </summary>
internal static class FolderMarks
{
    private const string Verbatim = @"\\?\";

    public static bool MayHide(IReadOnlyList<string> paths) => paths.Count <= CapabilityResolver.MaxFiles && paths.Any(Candidate);

    /// <summary>A name no module's kind claims: no extension, or one nobody knows ("Photos 2024.10", "v1.2").</summary>
    private static bool Candidate(string path) =>
        !Path.EndsInDirectorySeparator(path) && FileKinds.IdentifyByName(path) is null && PathPolicy.Explain(path) == PathVerdict.Supported;

    /// <summary>Marks on the calling thread, which must run under <see cref="ShortNames.WithoutErrorBoxes"/>; the
    /// deadline counts from <paramref name="start"/>, the start of the whole inspection (Stopwatch ticks).</summary>
    public static string[] Mark(string[] paths, long start, CancellationToken cancel) => Mark(paths, IsFolder, Stopwatch.GetTimestamp, start, cancel);

    /// <summary>The test seam: the question asked of the file system, and the clock.</summary>
    internal static string[] Mark(string[] paths, Func<string, bool> isFolder, Func<long> now, CancellationToken cancel) =>
        Mark(paths, isFolder, now, now(), cancel);

    internal static string[] Mark(string[] paths, Func<string, bool> isFolder, Func<long> now, long start, CancellationToken cancel)
    {
        var result = (string[])paths.Clone();
        for (var i = 0; i < paths.Length; i++)
        {
            if (!Candidate(paths[i])) continue;
            if (cancel.IsCancellationRequested || Stopwatch.GetElapsedTime(start, now()) >= ShortNames.Deadline) break;
            if (isFolder(paths[i])) result[i] = paths[i] + Path.DirectorySeparatorChar;
        }
        return result;
    }

    private static bool IsFolder(string path)
    {
        var asked = path.StartsWith(@"\\", StringComparison.Ordinal) ? Verbatim + @"UNC\" + path[2..] : Verbatim + path;
        var attributes = Kernel32.GetFileAttributesW(asked);
        return attributes != Kernel32.INVALID_FILE_ATTRIBUTES && (attributes & Kernel32.FILE_ATTRIBUTE_DIRECTORY) != 0;
    }
}
