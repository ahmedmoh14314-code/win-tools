using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinTools.Core.Subtitles;

public enum SubtitleFormat { Srt, Vtt }

/// <summary>
/// One cue: when it shows, when it goes, and its text lines as the source format wrote them (WebVTT tags and entities
/// kept raw), with a WebVTT cue id and the settings after the end time (WebVTT cue settings, SRT position hints).
/// </summary>
public sealed record Cue(TimeSpan Start, TimeSpan End, IReadOnlyList<string> Lines, string? Id = null, string? Settings = null);

/// <summary>A block a WebVTT file holds besides its cues (NOTE, STYLE, REGION), kept verbatim before cue <see cref="BeforeCue"/>.</summary>
public sealed record SubtitleBlock(int BeforeCue, IReadOnlyList<string> Lines);

/// <summary>
/// A parsed subtitle file: its cues, the problems found (line-numbered, never guessed around), and, for WebVTT, the
/// header lines and the other blocks, so a WebVTT-to-WebVTT job loses nothing (evaluator, PR 7.1).
/// </summary>
public sealed record SubtitleDocument(IReadOnlyList<Cue> Cues, IReadOnlyList<string> Problems, SubtitleFormat Format = SubtitleFormat.Srt,
    IReadOnlyList<string>? Header = null, IReadOnlyList<SubtitleBlock>? Blocks = null)
{
    public IReadOnlyList<string> HeaderLines => Header ?? [];
    public IReadOnlyList<SubtitleBlock> OtherBlocks => Blocks ?? [];
}

/// <summary>
/// SRT and WebVTT parsing (V2 PR 7.1, ADR-0024), in house. A cue is an optional index (SRT) or id (WebVTT), a timing
/// line ("00:00:01,000 --> 00:00:02,500"; WebVTT also "01:02.000", cue settings after the end time) and its text lines
/// up to a blank line. Anything uncertain is reported with its line number and nothing is guessed: a timing that does not
/// match exactly (ASCII digits only), an end before its start, a timing line among a cue's text (a missing blank line),
/// a line longer than <see cref="MaxLineLength"/>, a WebVTT header that runs into a cue. Bounded in lines, cues and line
/// length, and linear, so a hostile file cannot make it loop or allocate without limit.
/// </summary>
public static partial class SubtitleParser
{
    public const int MaxCues = 100_000, MaxLineLength = 4096, MaxProblems = 20, MaxLines = 1_000_000;

    [GeneratedRegex(@"^(?:([0-9]{1,3}):)?([0-9]{2}):([0-9]{2})[,.]([0-9]{3})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Time();

    [GeneratedRegex(@"^NOTE(?:[ \t]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NoteLine();

    public static SubtitleDocument Parse(string text, SubtitleFormat format)
    {
        var problems = new List<string>();
        void Problem(string p) { if (problems.Count < MaxProblems) problems.Add(p); }
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length > MaxLines) return new([], [$"more than {MaxLines:N0} lines."], format);
        static bool Blank(string s) => s.Trim().Length == 0;
        var cues = new List<Cue>();
        var header = new List<string>();
        var blocks = new List<SubtitleBlock>();
        var i = 0;
        if (format == SubtitleFormat.Vtt)
        {
            var first = lines.Length > 0 ? lines[0].TrimStart('﻿') : "";
            if (!(first == "WEBVTT" || first.StartsWith("WEBVTT ", StringComparison.Ordinal) || first.StartsWith("WEBVTT\t", StringComparison.Ordinal)))
            {
                Problem("line 1: a WebVTT file starts with \"WEBVTT\".");
                return new(cues, problems, format);
            }
            header.Add(first);
            i = 1;
            for (; i < lines.Length && !Blank(lines[i]); i++)
            {
                if (lines[i].Contains("-->", StringComparison.Ordinal))
                {
                    Problem($"line {i + 1}: a timing in the header (the blank line after WEBVTT is missing).");
                    return new(cues, problems, format, header);
                }
                header.Add(lines[i]);
            }
        }
        while (i < lines.Length)
        {
            if (Blank(lines[i])) { i++; continue; }
            if (lines[i].Length > MaxLineLength) { Problem($"line {i + 1}: longer than {MaxLineLength:N0} characters."); i++; continue; }
            if (format == SubtitleFormat.Vtt && !lines[i].Contains("-->", StringComparison.Ordinal)
                && (NoteLine().IsMatch(lines[i]) || lines[i].Trim() is "STYLE" or "REGION")
                && !(i + 1 < lines.Length && lines[i + 1].Contains("-->", StringComparison.Ordinal)))
            {
                var block = new List<string>();
                while (i < lines.Length && !Blank(lines[i])) block.Add(lines[i++]);
                blocks.Add(new SubtitleBlock(cues.Count, block));
                continue;
            }
            // The timing line is the block's first line, or its second after an index (SRT) or a cue id (WebVTT).
            var timing = lines[i].Contains("-->", StringComparison.Ordinal) ? i : i + 1 < lines.Length && lines[i + 1].Contains("-->", StringComparison.Ordinal) ? i + 1 : -1;
            if (timing < 0)
            {
                var at = lines[i].Trim().All(char.IsAsciiDigit) && i + 1 < lines.Length ? i + 2 : i + 1;
                Problem($"line {at}: a cue without a timing line (\"start --> end\").");
                while (i < lines.Length && !Blank(lines[i])) i++;
                continue;
            }
            var id = timing > i && format == SubtitleFormat.Vtt ? lines[i] : null;
            var parsed = ParseTiming(lines[timing], format);
            i = timing + 1;
            var cueText = new List<string>();
            var broken = false;
            while (i < lines.Length && !Blank(lines[i]))
            {
                if (lines[i].Contains("-->", StringComparison.Ordinal)) { Problem($"line {i + 1}: a timing line inside a cue (a blank line is missing before it)."); broken = true; }
                else if (lines[i].Length > MaxLineLength) { Problem($"line {i + 1}: longer than {MaxLineLength:N0} characters."); broken = true; }
                cueText.Add(lines[i]);
                i++;
            }
            if (parsed is not { } t) { Problem($"line {timing + 1}: the timing \"{Short(lines[timing])}\" is not understood."); continue; }
            if (t.End < t.Start) { Problem($"line {timing + 1}: the cue ends before it starts."); continue; }
            if (broken) continue;
            if (cues.Count >= MaxCues) { Problem($"more than {MaxCues:N0} cues."); break; }
            cues.Add(new Cue(t.Start, t.End, cueText, id, t.Settings));
        }
        return new SubtitleDocument(cues, problems, format, header, blocks);
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..39] + "…";

    private static (TimeSpan Start, TimeSpan End, string? Settings)? ParseTiming(string line, SubtitleFormat format)
    {
        var parts = line.Split("-->", 2, StringSplitOptions.None);
        if (parts.Length != 2) return null;
        var start = parts[0].Trim();
        var rest = parts[1].Trim();
        var end = rest.Split([' ', '\t'], 2)[0]; // settings follow the end time
        var settings = end.Length < rest.Length ? rest[end.Length..].Trim() : null;
        if (format == SubtitleFormat.Srt && settings is not null && !settings.StartsWith('X')) return null; // SRT has only "X1:..." position hints
        return ParseTime(start) is { } s && ParseTime(end) is { } e ? (s, e, settings) : null;
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

/// <summary>
/// Writes a document as SRT (UTF-8 with a BOM, CRLF, as Windows players expect) or WebVTT (UTF-8, LF). The same format
/// is written back verbatim apart from the times (a WebVTT header, NOTE, STYLE and REGION blocks, cue ids and settings
/// are kept). A conversion turns the text over: WebVTT to SRT keeps &lt;i&gt;, &lt;b&gt; and &lt;u&gt;, drops the other
/// tags and decodes the entities; SRT to WebVTT escapes '&amp;', '&lt;' and '&gt;' outside those tags. What a conversion
/// cannot carry is listed by <see cref="Dropped"/>, so the user is told.
/// </summary>
public static partial class SubtitleWriter
{
    /// <summary>The longest time either format writes: three hour digits.</summary>
    public static readonly TimeSpan MaxTime = new(0, 999, 59, 59, 999);

    [GeneratedRegex(@"</?(?:c|v|lang|ruby|rt)(?:[.][^\s>]{0,100})?(?:[ \t][^>]{0,200})?>|<[0-9]{2}:[0-9]{2}(?::[0-9]{2})?[.][0-9]{3}>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex VttOnlyTag();

    [GeneratedRegex(@"<(/?)([ibu])[.][^\s>]{0,100}>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex StyledBasicTag();

    [GeneratedRegex(@"</?font(?:[ \t][^>]{0,200})?>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex FontTag();

    [GeneratedRegex(@"</?[ibu]>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex BasicTag();

    public static string Write(SubtitleDocument doc, SubtitleFormat format)
    {
        var nl = format == SubtitleFormat.Srt ? "\r\n" : "\n";
        var same = doc.Format == format;
        var text = new StringBuilder();
        if (format == SubtitleFormat.Vtt)
        {
            if (same && doc.HeaderLines.Count > 0) foreach (var h in doc.HeaderLines) text.Append(h).Append(nl);
            else text.Append("WEBVTT").Append(nl);
            text.Append(nl);
        }
        var blocks = same ? doc.OtherBlocks : [];
        var nextBlock = 0;
        for (var i = 0; i < doc.Cues.Count; i++)
        {
            for (; nextBlock < blocks.Count && blocks[nextBlock].BeforeCue <= i; nextBlock++)
            {
                foreach (var l in blocks[nextBlock].Lines) text.Append(l).Append(nl);
                text.Append(nl);
            }
            var c = doc.Cues[i];
            if (format == SubtitleFormat.Srt) text.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(nl);
            else if (same && c.Id is not null) text.Append(c.Id).Append(nl);
            text.Append(Time(c.Start, format)).Append(" --> ").Append(Time(c.End, format));
            if (same && c.Settings is not null) text.Append(' ').Append(c.Settings);
            text.Append(nl);
            foreach (var line in c.Lines) text.Append(same ? line : Convert(line, doc.Format)).Append(nl);
            text.Append(nl);
        }
        for (; nextBlock < blocks.Count; nextBlock++)
        {
            foreach (var l in blocks[nextBlock].Lines) text.Append(l).Append(nl);
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

    /// <summary>What writing <paramref name="doc"/> as <paramref name="format"/> leaves out, in the user's words; empty when nothing.</summary>
    public static IReadOnlyList<string> Dropped(SubtitleDocument doc, SubtitleFormat format)
    {
        if (doc.Format == format) return [];
        var dropped = new List<string>();
        if (doc.Format == SubtitleFormat.Vtt)
        {
            if (doc.HeaderLines.Count > 1) dropped.Add("header settings");
            if (doc.OtherBlocks.Count > 0) dropped.Add("styles and notes");
            if (doc.Cues.Any(c => c.Settings is not null)) dropped.Add("cue positions");
            if (doc.Cues.Any(c => c.Lines.Any(l => VttOnlyTag().IsMatch(l) || StyledBasicTag().IsMatch(l)))) dropped.Add("WebVTT styling");
        }
        else
        {
            if (doc.Cues.Any(c => c.Settings is not null)) dropped.Add("SRT position hints");
            if (doc.Cues.Any(c => c.Lines.Any(l => FontTag().IsMatch(l)))) dropped.Add("font colours");
        }
        return dropped;
    }

    private static string Convert(string line, SubtitleFormat from) => from == SubtitleFormat.Vtt ? VttToSrt(line) : SrtToVtt(line);

    private static string VttToSrt(string line)
    {
        var s = VttOnlyTag().Replace(line, "");
        s = StyledBasicTag().Replace(s, "<$1$2>");
        // The entities WebVTT defines that a cue holds; any other '&' is text.
        return s.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("&lrm;", "‎", StringComparison.Ordinal).Replace("&rlm;", "‏", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
    }

    private static string SrtToVtt(string line)
    {
        var s = FontTag().Replace(line, "");
        var text = new StringBuilder();
        var at = 0;
        foreach (Match tag in BasicTag().Matches(s))
        {
            text.Append(Escape(s[at..tag.Index])).Append(tag.Value);
            at = tag.Index + tag.Length;
        }
        return text.Append(Escape(s[at..])).ToString();
    }

    private static string Escape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

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
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}

/// <summary>What a subtitle file's bytes decoded to, and how.</summary>
public sealed record DecodedText(string Text, string EncodingName);

/// <summary>
/// Decodes a subtitle file whose encoding is not stated (V2 plan §7), only when it is certain (evaluator, PR 7.1):
/// <list type="number">
/// <item>A BOM (UTF-8, UTF-16 LE or BE), with a strict decoder: text that is not that encoding is refused.</item>
/// <item>Strict UTF-8. A file that is mostly valid UTF-8 with a few bad bytes is refused (damaged, not a code page).</item>
/// <item>Windows-1256, when at least 80 % of what its high bytes decode to is Arabic script.</item>
/// <item>Windows-1252, when what its high bytes decode to is Latin and they stand alone or in pairs inside words (accented
/// letters), with fewer than half of them in runs of three or more. Cyrillic and Greek in their own code pages put over 90 % of
/// their high bytes in such runs (whole words); Western text stays far below.</item>
/// </list>
/// Anything else is refused with the reason: a wrong guess would be written out as garbage and called a success.
/// </summary>
public static class SubtitleEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, throwOnInvalidBytes: true), StrictUtf16Be = new(true, false, throwOnInvalidBytes: true);
    private const string Unknown = "The text encoding is not recognised (it is not UTF-8, Arabic Windows-1256 or Western Windows-1252). Save the file as UTF-8 and try again.";

    public static DecodedText? TryDecode(ReadOnlySpan<byte> bytes, out string why)
    {
        why = "";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Strict(bytes[3..], StrictUtf8, "UTF-8 with BOM", out why);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Strict(bytes[2..], StrictUtf16Le, "UTF-16 LE", out why);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Strict(bytes[2..], StrictUtf16Be, "UTF-16 BE", out why);
        try { return new(StrictUtf8.GetString(bytes), "UTF-8"); }
        catch (DecoderFallbackException) { }
        if (MostlyUtf8(bytes))
        {
            why = "The file is UTF-8 with a few damaged bytes (it may be cut short or joined from two files). It was not changed.";
            return null;
        }
        if (Try(bytes, 1256) is { } arabic && Share(arabic, IsArabic) >= 0.8) return new(arabic, "Windows-1256 (Arabic)");
        if (Try(bytes, 1252) is { } western && Share(western, IsWestern) >= 0.95 && RunShare(bytes) < 0.5) return new(western, "Windows-1252 (Western)");
        why = Unknown;
        return null;
    }

    /// <summary>For text the product wrote itself (read-backs): decoded, or an exception.</summary>
    public static DecodedText Decode(ReadOnlySpan<byte> bytes) =>
        TryDecode(bytes, out var why) ?? throw new InvalidDataException(why);

    private static DecodedText? Strict(ReadOnlySpan<byte> body, Encoding encoding, string name, out string why)
    {
        why = "";
        try { return new(encoding.GetString(body), name); }
        catch (DecoderFallbackException)
        {
            why = $"The file says it is {name} but holds bytes that are not; it was not changed.";
            return null;
        }
    }

    private static string? Try(ReadOnlySpan<byte> bytes, int codePage)
    {
        try { return CodePages.Get(codePage).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>The share of non-ASCII characters that pass <paramref name="test"/>.</summary>
    private static double Share(string text, Func<char, bool> test)
    {
        int all = 0, pass = 0;
        foreach (var c in text)
        {
            if (c < 0x80) continue;
            all++;
            if (test(c)) pass++;
        }
        return all == 0 ? 0 : (double)pass / all;
    }

    private static bool IsArabic(char c) => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿'
        or '‌' or '‍' or '‎' or '‏' or ' ';

    private static bool IsWestern(char c) => c is >= ' ' and <= 'ÿ'
        || "ŒœŠšŽžŸƒ‘’‚“”„†‡•…‰‹›€™ˆ˜–—".Contains(c, StringComparison.Ordinal);

    /// <summary>The share of high bytes that stand in runs of three or more: words of a non-Latin script.</summary>
    private static double RunShare(ReadOnlySpan<byte> b)
    {
        int high = 0, inRuns = 0;
        for (var i = 0; i < b.Length;)
        {
            if (b[i] < 0x80) { i++; continue; }
            var start = i;
            while (i < b.Length && b[i] >= 0x80) i++;
            var run = i - start;
            high += run;
            if (run >= 3) inRuns += run;
        }
        return high == 0 ? 0 : (double)inRuns / high;
    }

    /// <summary>Whether most non-ASCII bytes form valid UTF-8 sequences: a damaged UTF-8 file, not a legacy code page.</summary>
    private static bool MostlyUtf8(ReadOnlySpan<byte> b)
    {
        int valid = 0, invalid = 0;
        for (var i = 0; i < b.Length;)
        {
            if (b[i] < 0x80) { i++; continue; }
            var need = b[i] >= 0xF0 && b[i] <= 0xF4 ? 3 : b[i] >= 0xE0 ? 2 : b[i] >= 0xC2 && b[i] <= 0xDF ? 1 : -1;
            var ok = need > 0;
            for (var k = 1; ok && k <= need; k++) ok = i + k < b.Length && (b[i + k] & 0xC0) == 0x80;
            if (ok) { valid += need + 1; i += need + 1; }
            else { invalid++; i++; }
        }
        return valid >= 4 && valid >= 4 * invalid;
    }
}
