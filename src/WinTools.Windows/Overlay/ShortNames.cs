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
/// </summary>
internal static class ShortNames
{
    private const string Verbatim = @"\\?\", VerbatimUnc = @"\\?\UNC\";

    public static string Expand(string path)
    {
        if (PathPolicy.Explain(path) != PathVerdict.ShortName) return path;
        var asked = path.StartsWith(@"\\", StringComparison.Ordinal) ? VerbatimUnc + path[2..] : Verbatim + path;
        var buffer = new char[Kernel32.MaxLongPath];
        var n = Kernel32.GetLongPathNameW(asked, buffer, (uint)buffer.Length);
        if (n == 0 || n >= buffer.Length) return path;
        var expanded = new string(buffer, 0, (int)n);
        if (expanded.StartsWith(VerbatimUnc, StringComparison.Ordinal)) return @"\\" + expanded[VerbatimUnc.Length..];
        return expanded.StartsWith(Verbatim, StringComparison.Ordinal) ? expanded[Verbatim.Length..] : expanded;
    }
}
