using System.Diagnostics;
using WinTools.Core.Files;
using WinTools.Windows.Native;

namespace WinTools.Windows.Overlay;

/// <summary>
/// Explorer drops a file whose full path is longer than MAX_PATH (260) as its 8.3 short path, so that legacy programs
/// can open it. <see cref="PathPolicy"/> refuses short names, because they are not the canonical name, so every such
/// file got no commands. This expands a dropped short path to the long path of the same file (GetLongPathNameW, asked
/// with the \\?\ prefix so the answer may exceed MAX_PATH). Only a path PathPolicy calls a short name is touched; a long
/// name that merely contains '~' is left alone; a path that cannot be expanded (gone, no access) is returned unchanged,
/// so it stays refused. The result is then judged by PathPolicy like any other dropped path.
/// <para>
/// This asks the file system on the UI thread (the drop target reads in DragEnter, DragOver and Drop), so it is bounded:
/// only paths on a local fixed, removable or RAM drive are expanded (a UNC or mapped network path could block on the
/// network for seconds), and expansion stops once <see cref="Budget"/> is spent; what is left stays refused.
/// </para>
/// </summary>
internal static class ShortNames
{
    private const string Verbatim = @"\\?\";

    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

    public static string[] ExpandAll(string[] paths) => ExpandAll(paths, IsLocalDrive, Expand, Stopwatch.GetTimestamp);

    /// <summary>The test seam: which paths may be asked about, the expansion itself, and the clock (Stopwatch ticks).</summary>
    internal static string[] ExpandAll(string[] paths, Func<string, bool> isLocal, Func<string, string> expand, Func<long> now)
    {
        string[]? result = null;
        var start = now();
        for (var i = 0; i < paths.Length; i++)
        {
            if (PathPolicy.Explain(paths[i]) != PathVerdict.ShortName || !isLocal(paths[i])) continue;
            if (Stopwatch.GetElapsedTime(start, now()) >= Budget) break;
            (result ??= (string[])paths.Clone())[i] = expand(paths[i]);
        }
        return result ?? paths;
    }

    /// <summary>A drive-letter path on a fixed, removable or RAM drive. GetDriveTypeW answers from the mount table.</summary>
    private static bool IsLocalDrive(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && Kernel32.GetDriveTypeW(path[..3]) is Kernel32.DRIVE_REMOVABLE or Kernel32.DRIVE_FIXED or Kernel32.DRIVE_RAMDISK;

    private static string Expand(string path)
    {
        var buffer = new char[Kernel32.MaxLongPath];
        var n = Kernel32.GetLongPathNameW(Verbatim + path, buffer, (uint)buffer.Length);
        if (n == 0 || n >= buffer.Length) return path;
        var expanded = new string(buffer, 0, (int)n);
        return expanded.StartsWith(Verbatim, StringComparison.Ordinal) ? expanded[Verbatim.Length..] : expanded;
    }
}
