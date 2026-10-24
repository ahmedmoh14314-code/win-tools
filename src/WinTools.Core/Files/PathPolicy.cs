namespace WinTools.Core.Files;

/// <summary>
/// Which dropped paths the product acts on. A path must already be in canonical form: Win32 path normalisation would
/// strip trailing dots and spaces from a folder or file name, so a request for "dir.\photo.png" would silently read and
/// write "dir\photo.png", a different file. A path must also be well-formed UTF-16: a lone surrogate cannot cross the
/// worker's JSON pipe intact and would name a different file on the other side. Device and verbatim (\\?\, \\.\)
/// paths, alternate data streams ("a.png:s") and names without a stem (".png") are refused too. 8.3 short names are
/// refused as well, recognised by their shape (the drop target expands the ones Explorer sends for paths beyond
/// MAX_PATH, on local drives, before they get here). Pure string checks; no file-system access.
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
        // GetFullPath expands a name containing '~' by asking the file system (GetLongPathName), which would put disk and
        // network I/O on the UI thread. '~' is swapped for a private-use character that triggers nothing and normalises
        // the same way; positions are kept, so the comparison still finds every change normalisation would make.
        var probe = path.Replace('~', (char)0xE000);
        string full;
        try { full = Path.GetFullPath(probe); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return PathVerdict.NotCanonical; }
        if (!string.Equals(full, probe, StringComparison.Ordinal)) return PathVerdict.NotCanonical;
        if (HasShortAlias(path)) return PathVerdict.ShortName;
        return Path.GetFileNameWithoutExtension(path).Length > 0 ? PathVerdict.Supported : PathVerdict.NoStem;
    }

    /// <summary>
    /// Whether a component has the shape of an 8.3 alias as NTFS and FAT generate them: upper case, at most 8 characters
    /// before an extension of at most 3, ending in '~' and digits, with no spaces or lower-case letters. Judged by shape
    /// alone, so no lookup is made; a file a person really named like an alias (ABC~1.PNG) is refused too.
    /// </summary>
    private static bool HasShortAlias(string path)
    {
        if (!path.Contains('~')) return false;
        foreach (var part in path.Split('\\'))
        {
            var dot = part.LastIndexOf('.');
            var (stem, ext) = dot < 0 ? (part, "") : (part[..dot], part[(dot + 1)..]);
            var tilde = stem.LastIndexOf('~');
            if (tilde < 1 || stem.Length > 8 || tilde == stem.Length - 1 || ext.Length > 3) continue;
            if (!stem[(tilde + 1)..].All(char.IsAsciiDigit)) continue;
            if ((stem[..tilde] + ext).All(c => c > ' ' && !char.IsLower(c) && c != '.')) return true;
        }
        return false;
    }
}

/// <summary>The outcome of <see cref="PathPolicy.Explain"/>.</summary>
public enum PathVerdict { Supported, Empty, Relative, Verbatim, Device, Stream, BadText, NotCanonical, ShortName, NoStem }
