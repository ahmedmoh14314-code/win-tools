using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Windows.Native;

namespace WinTools.Windows.Overlay;

/// <summary>
/// Marks dropped folders with a trailing separator, the Windows convention for a directory path, so that from there on
/// a folder is a kind like any other (UX-009, ADR-0019). Only names without an extension are asked about: a folder named
/// like a file ("holiday.png") is caught by the worker instead. The lookups run on the drop target's inspection task,
/// with the same deadline as short-name expansion; a path that cannot be asked about stays as it was.
/// </summary>
internal static class FolderMarks
{
    private const string Verbatim = @"\\?\";

    public static bool MayHide(IReadOnlyList<string> paths) => paths.Count <= CapabilityResolver.MaxFiles && paths.Any(Candidate);

    private static bool Candidate(string path) =>
        !Path.EndsInDirectorySeparator(path) && Path.GetExtension(path.AsSpan()).Length == 0 && PathPolicy.Explain(path) == PathVerdict.Supported;

    public static string[] Mark(string[] paths, CancellationToken cancel) => Mark(paths, IsFolder, Stopwatch.GetTimestamp, cancel);

    /// <summary>The test seam: the question asked of the file system, and the clock.</summary>
    internal static string[] Mark(string[] paths, Func<string, bool> isFolder, Func<long> now, CancellationToken cancel)
    {
        var result = (string[])paths.Clone();
        var start = now();
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
