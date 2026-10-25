using System.Globalization;

namespace WinTools.Worker;

/// <summary>
/// The Mark of the Web (ADR-0018): Windows records where a downloaded file came from in its Zone.Identifier stream, and
/// SmartScreen and Office's Protected View act on it. An output made from such a file must carry the mark too, or a
/// conversion would launder an internet file into a local one. Only the zone is written; the source's URLs are its own
/// data, not the output's. Zones 3 (internet) and 4 (restricted) are carried; 0–2 (local, intranet, trusted) are not,
/// and a custom zone above 4 is carried as restricted. A mark that is present but unreadable counts as the internet zone;
/// a source on a volume without alternate streams (FAT, exFAT, some shares) cannot have one, and has none.
/// </summary>
public static partial class MarkOfTheWeb
{
    private const string Stream = ":Zone.Identifier";
    private const int MaxStreamBytes = 64 * 1024;

    /// <summary>The zone an output of this source must carry, or null for none.</summary>
    public static int? ZoneToCarry(string source)
    {
        // Opening "file:Zone.Identifier" there fails with a generic I/O error, which must not read as an unreadable mark
        // (evaluator, PR #16: every SD-card photo would have been marked).
        if (HasNamedStreams(source) == false) return null;
        string text;
        try
        {
            using var s = new FileStream(source + Stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (s.Length > MaxStreamBytes) return 3;
            using var reader = new StreamReader(s);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; } // no mark
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return 3; } // a mark we cannot read

        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line.Equals("[ZoneTransfer]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection || !line.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line["ZoneId=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var zone)) return 3;
            return zone >= 3 ? Math.Min(zone, 4) : null;
        }
        return 3;
    }

    /// <summary>
    /// Marks a file with the zone, before it gets its final name, so no output is ever visible unmarked. A volume without
    /// alternate streams (FAT, exFAT) cannot hold the mark; Windows itself drops it there, and so does this. On a volume
    /// that has them, a mark that cannot be written is an error: the job fails and its temp is removed (evaluator, PR #16).
    /// </summary>
    public static void Apply(string path, int zone)
    {
        try { File.WriteAllText(path + Stream, string.Create(CultureInfo.InvariantCulture, $"[ZoneTransfer]\r\nZoneId={zone}\r\n")); }
        catch (Exception ex) when (ex is IOException or NotSupportedException && HasNamedStreams(path) == false) { }
    }

    private const uint FileNamedStreams = 0x00040000; // FILE_NAMED_STREAMS

    /// <summary>Whether the path's volume supports alternate data streams; null when that cannot be asked.</summary>
    internal static bool? HasNamedStreams(string path)
    {
        string? root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (string.IsNullOrEmpty(root)) return null;
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        return GetVolumeInformationW(root, null, 0, out _, out _, out var flags, null, 0) ? (flags & FileNamedStreams) != 0 : null;
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetVolumeInformationW(string root, char[]? volumeName, uint volumeNameSize, out uint serial, out uint maxComponent,
        out uint flags, char[]? fileSystemName, uint fileSystemNameSize);
}
