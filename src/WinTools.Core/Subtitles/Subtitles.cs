using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinTools.Core.Subtitles;

public enum SubtitleFormat { Srt, Vtt }

/// <summary>One cue: when it shows, when it goes, and its text lines (tags kept as SRT understands them).</summary>
public sealed record Cue(TimeSpan Start, TimeSpan End, IReadOnlyList<string> Lines);

/// <summary>A parsed subtitle file: its cues, and the problems found (line-numbered, never guessed around).</summary>
public sealed record SubtitleDocument(IReadOnlyList<Cue> Cues, IReadOnlyList<string> Problems);

/// <summary>
/// SRT and WebVTT parsing (V2 PR 7.1, ADR-0024), in house: a cue is a timing line ("00:00:01,000 --> 00:00:02,500";
/// WebVTT also "01:02.000" and cue settings after the times) and its text lines up to a blank line. A timing line that
/// does not match exactly, a field out of range or an end before the start is reported with its line number; the cue is
/// not guessed at. Linear in the input and bounded (<see cref="MaxCues"/>, <see cref="MaxLineLength"/>), so a hostile
/// file cannot make it loop or allocate without limit. WebVTT NOTE, STYLE and REGION blocks are skipped; voice and class
/// tags are removed, keeping only &lt;i&gt;, &lt;b&gt; and &lt;u&gt;, which both formats understand.
/// </summary>
public static partial class SubtitleParser
{
    public const int MaxCues = 100_000, MaxLineLength = 4096, MaxProblems = 20;

    [GeneratedRegex(@"^(?:(\d{1,3}):)?(\d{2}):(\d{2})[,.](\d{3})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Time();

    [GeneratedRegex(@"</?(?!(?:i|b|u)>)[a-zA-Z][^>]{0,200}>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ForeignTag();

    public static SubtitleDocument Parse(string text, SubtitleFormat format)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var cues = new List<Cue>();
        var problems = new List<string>();
        void Problem(string p) { if (problems.Count < MaxProblems) problems.Add(p); }
        var i = 0;
        if (format == SubtitleFormat.Vtt)
        {
            if (lines.Length == 0 || !lines[0].TrimStart('﻿').StartsWith("WEBVTT", StringComparison.Ordinal)) Problem("line 1: a WebVTT file starts with \"WEBVTT\".");
            while (i < lines.Length && lines[i].Length > 0) i++; // the header block
        }
        while (i < lines.Length)
        {
            if (lines[i].Length > MaxLineLength) { Problem($"line {i + 1}: longer than {MaxLineLength} characters."); i++; continue; }
            if (lines[i].Trim().Length == 0) { i++; continue; }
            if (format == SubtitleFormat.Vtt && (lines[i].StartsWith("NOTE", StringComparison.Ordinal) || lines[i] == "STYLE" || lines[i] == "REGION"))
            {
                while (i < lines.Length && lines[i].Trim().Length > 0) i++;
                continue;
            }
            // The timing line is the block's first line, or its second after an index (SRT) or a cue id (WebVTT).
            var timing = lines[i].Contains("-->", StringComparison.Ordinal) ? i : i + 1 < lines.Length && lines[i + 1].Contains("-->", StringComparison.Ordinal) ? i + 1 : -1;
            if (timing < 0)
            {
                // After an index ("1") the next line should have been the timing: name that line.
                var at = lines[i].Trim().All(char.IsAsciiDigit) && i + 1 < lines.Length ? i + 2 : i + 1;
                Problem($"line {at}: a cue without a timing line (\"start --> end\").");
                while (i < lines.Length && lines[i].Trim().Length > 0) i++;
                continue;
            }
            var parsed = ParseTiming(lines[timing], format);
            i = timing + 1;
            var text2 = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                var line = lines[i].Length > MaxLineLength ? lines[i][..MaxLineLength] : lines[i];
                text2.Add(format == SubtitleFormat.Vtt ? ForeignTag().Replace(line, "") : line);
                i++;
            }
            if (parsed is not { } t) { Problem($"line {timing + 1}: the timing \"{Short(lines[timing])}\" is not understood."); continue; }
            if (t.End < t.Start) { Problem($"line {timing + 1}: the cue ends before it starts."); continue; }
            if (cues.Count >= MaxCues) { Problem($"more than {MaxCues:N0} cues."); break; }
            cues.Add(new Cue(t.Start, t.End, text2));
        }
        return new SubtitleDocument(cues, problems);
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..39] + "…";

    private static (TimeSpan Start, TimeSpan End)? ParseTiming(string line, SubtitleFormat format)
    {
        var parts = line.Split("-->", 2, StringSplitOptions.None);
        if (parts.Length != 2) return null;
        var start = parts[0].Trim();
        var rest = parts[1].Trim();
        var end = rest.Split([' ', '\t'], 2)[0]; // WebVTT cue settings follow the end time
        if (format == SubtitleFormat.Srt && end.Length != rest.Length && !rest[end.Length..].TrimStart().StartsWith('X')) return null; // SRT allows only "X1:..." position hints
        return ParseTime(start) is { } s && ParseTime(end) is { } e ? (s, e) : null;
    }

    internal static TimeSpan? ParseTime(string s)
    {
        Match m;
        try { m = Time().Match(s); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;
        var h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var sec = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var ms = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        if (min > 59 || sec > 59) return null;
        return new TimeSpan(0, h, min, sec, ms);
    }
}

/// <summary>Writes a document as SRT (UTF-8 with a BOM, CRLF, as Windows players expect) or WebVTT (UTF-8, LF).</summary>
public static class SubtitleWriter
{
    public static string Write(SubtitleDocument doc, SubtitleFormat format)
    {
        var nl = format == SubtitleFormat.Srt ? "\r\n" : "\n";
        var text = new StringBuilder();
        if (format == SubtitleFormat.Vtt) text.Append("WEBVTT").Append(nl).Append(nl);
        for (var i = 0; i < doc.Cues.Count; i++)
        {
            var c = doc.Cues[i];
            if (format == SubtitleFormat.Srt) text.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(nl);
            text.Append(Time(c.Start, format)).Append(" --> ").Append(Time(c.End, format)).Append(nl);
            foreach (var line in c.Lines) text.Append(line).Append(nl);
            text.Append(nl);
        }
        return text.ToString();
    }

    /// <summary>The bytes of a written document: SRT with a UTF-8 BOM (older players need it for Arabic), WebVTT without.</summary>
    public static byte[] Bytes(SubtitleDocument doc, SubtitleFormat format)
    {
        var body = Encoding.UTF8.GetBytes(Write(doc, format));
        return format == SubtitleFormat.Srt ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    private static string Time(TimeSpan t, SubtitleFormat format)
    {
        var sep = format == SubtitleFormat.Srt ? ',' : '.';
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{sep}{t.Milliseconds:000}");
    }
}

/// <summary>Timing changes and checks.</summary>
public static class SubtitleTiming
{
    /// <summary>Every cue moved by <paramref name="by"/>; a time that would fall before zero stays at zero.</summary>
    public static SubtitleDocument Shift(SubtitleDocument doc, TimeSpan by) =>
        doc with { Cues = [.. doc.Cues.Select(c => c with { Start = Clamp(c.Start + by), End = Clamp(c.End + by) })] };

    /// <summary>How many cues a shift would clamp at zero.</summary>
    public static int ClampedBy(SubtitleDocument doc, TimeSpan by) => doc.Cues.Count(c => c.Start + by < TimeSpan.Zero);

    /// <summary>How many cues start before the previous one has ended.</summary>
    public static int Overlaps(SubtitleDocument doc)
    {
        var n = 0;
        for (var i = 1; i < doc.Cues.Count; i++) if (doc.Cues[i].Start < doc.Cues[i - 1].End) n++;
        return n;
    }

    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}

/// <summary>The legacy code pages subtitles come in (the provider is part of .NET; it is registered once).</summary>
public static class CodePages
{
    private static readonly Lazy<bool> Registered = new(() => { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return true; });

    public static Encoding Get(int codePage)
    {
        _ = Registered.Value;
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }
}

/// <summary>What a subtitle file's bytes decoded to, and how.</summary>
public sealed record DecodedText(string Text, string EncodingName);

/// <summary>
/// Decodes a subtitle file whose encoding is not stated (V2 plan §7): a BOM first (UTF-8, UTF-16 LE or BE); then strict
/// UTF-8; then Windows-1256 (Arabic) or Windows-1252 (Western), told apart by how the bytes above 0x7F sit. Arabic words
/// are runs of such bytes, while Western text has single accented letters between ASCII letters.
/// </summary>
public static class SubtitleEncoding
{
    public static DecodedText Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return new(Encoding.UTF8.GetString(bytes[3..]), "UTF-8 with BOM");
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return new(Encoding.Unicode.GetString(bytes[2..]), "UTF-16 LE");
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return new(Encoding.BigEndianUnicode.GetString(bytes[2..]), "UTF-16 BE");
        try { return new(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), "UTF-8"); }
        catch (DecoderFallbackException) { }
        return LooksArabic(bytes) ? new(CodePages.Get(1256).GetString(bytes), "Windows-1256 (Arabic)") : new(CodePages.Get(1252).GetString(bytes), "Windows-1252 (Western)");
    }

    /// <summary>Most high bytes have a high-byte neighbour: words written in a non-Latin script.</summary>
    private static bool LooksArabic(ReadOnlySpan<byte> b)
    {
        int high = 0, inRuns = 0;
        for (var i = 0; i < b.Length; i++)
        {
            if (b[i] < 0x80) continue;
            high++;
            if ((i > 0 && b[i - 1] >= 0x80) || (i + 1 < b.Length && b[i + 1] >= 0x80)) inRuns++;
        }
        return high > 0 && inRuns * 2 > high;
    }
}
