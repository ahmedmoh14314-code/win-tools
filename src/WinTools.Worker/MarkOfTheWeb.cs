using System.Globalization;

namespace WinTools.Worker;

/// <summary>
/// The Mark of the Web (ADR-0018): Windows records where a downloaded file came from in its Zone.Identifier stream, and
/// SmartScreen and Office's Protected View act on it. An output made from such a file must carry the mark too, or a
/// conversion would launder an internet file into a local one. Only the zone is written; the source's URLs are its own
/// data, not the output's. Zones 3 (internet) and 4 (restricted) are carried; 0–2 (local, intranet, trusted) are not.
/// A mark that is present but unreadable counts as the internet zone.
/// </summary>
internal static class MarkOfTheWeb
{
    private const string Stream = ":Zone.Identifier";
    private const int MaxStreamBytes = 64 * 1024;

    /// <summary>The zone an output of this source must carry, or null for none.</summary>
    public static int? ZoneToCarry(string source)
    {
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
            return zone >= 3 ? zone : null;
        }
        return 3;
    }

    /// <summary>
    /// Marks a file with the zone, before it gets its final name, so no output is ever visible unmarked. A volume without
    /// alternate streams (FAT, exFAT) cannot hold the mark; Windows itself drops it there, and so does this.
    /// </summary>
    public static void Apply(string path, int zone)
    {
        try { File.WriteAllText(path + Stream, string.Create(CultureInfo.InvariantCulture, $"[ZoneTransfer]\r\nZoneId={zone}\r\n")); }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { }
    }
}
