namespace WinTools.Core.Files;

/// <summary>
/// Which dropped paths the product acts on. A path must already be in canonical form: Win32 path normalisation would
/// strip trailing dots and spaces from a folder or file name, so a request for "dir.\photo.png" would silently read and
/// write "dir\photo.png", a different file. A path must also be well-formed UTF-16: a lone surrogate cannot cross the
/// worker's JSON pipe intact and would name a different file on the other side. Device and verbatim (\\?\, \\.\)
/// paths, alternate data streams ("a.png:s") and names without a stem (".png") are refused too. 8.3 short names are
/// refused as well (GetFullPath expands them); Explorer drops long names. Pure string checks; no file-system access.
/// </summary>
public static class PathPolicy
{
    public static bool IsSupported(string? path) => Explain(path) == PathVerdict.Supported;

    /// <summary>Why a path is or is not acted on. For diagnostics (the reason, never the path, may be logged).</summary>
    public static PathVerdict Explain(string? path)
    {
        if (string.IsNullOrEmpty(path)) return PathVerdict.Empty;
        if (!Path.IsPathFullyQualified(path)) return PathVerdict.Relative;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return PathVerdict.Verbatim;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) return PathVerdict.Device;
        if (path[0] == '\\' && path[1] != '\\') return PathVerdict.Device; // "\??\..." (NT device namespace): only drive and UNC paths
        if (path.IndexOf(':', 2) >= 0) return PathVerdict.Stream; // "file.png:stream" names an alternate data stream
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= path.Length || !char.IsLowSurrogate(path[i + 1])) return PathVerdict.BadText;
                i++;
            }
            else if (char.IsLowSurrogate(c) || c < 0x20) return PathVerdict.BadText;
        }
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return PathVerdict.NotCanonical; }
        if (!string.Equals(full, path, StringComparison.Ordinal)) return path.Contains('~') ? PathVerdict.ShortName : PathVerdict.NotCanonical;
        return Path.GetFileNameWithoutExtension(path).Length > 0 ? PathVerdict.Supported : PathVerdict.NoStem;
    }
}

/// <summary>The outcome of <see cref="PathPolicy.Explain"/>.</summary>
public enum PathVerdict { Supported, Empty, Relative, Verbatim, Device, Stream, BadText, NotCanonical, ShortName, NoStem }
