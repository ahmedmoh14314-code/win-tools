using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Subtitles;

namespace WinTools.Core.Tests;

/// <summary>
/// V2 PR 7.1 (ADR-0024): subtitle parsing, writing, timing and encodings, written before the code. Malformed timing is
/// reported, never guessed; Arabic in Windows-1256 is told from Western text in Windows-1252 by its runs of high bytes.
/// </summary>
public sealed class SubtitleTests
{
    private const string Srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nمرحبا بكم 😀\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\n<i>Second</i>\r\nline two\r\n\r\n";

    [Fact]
    public void Srt_parses_into_cues_with_their_times_and_lines()
    {
        var doc = SubtitleParser.Parse(Srt, SubtitleFormat.Srt);
        Assert.Empty(doc.Problems);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), doc.Cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), doc.Cues[0].End);
        Assert.Equal(["مرحبا بكم 😀"], doc.Cues[0].Lines);
        Assert.Equal(["<i>Second</i>", "line two"], doc.Cues[1].Lines);
    }

    [Fact]
    public void Srt_to_vtt_and_back_keeps_every_cue()
    {
        var doc = SubtitleParser.Parse(Srt, SubtitleFormat.Srt);
        var vtt = SubtitleWriter.Write(doc, SubtitleFormat.Vtt);
        Assert.StartsWith("WEBVTT\n\n", vtt, StringComparison.Ordinal);
        Assert.Contains("00:00:01.000 --> 00:00:02.500\nمرحبا بكم 😀\n", vtt, StringComparison.Ordinal);
        var back = SubtitleWriter.Write(SubtitleParser.Parse(vtt, SubtitleFormat.Vtt), SubtitleFormat.Srt);
        Assert.Equal(Srt, back);
    }

    [Fact]
    public void Vtt_with_short_times_ids_settings_notes_and_voice_tags_converts_to_plain_srt()
    {
        const string vtt = "WEBVTT - a title\n\nNOTE written by hand\n\nSTYLE\n::cue { color: yellow }\n\nintro\n01:02.000 --> 01:03.500 align:start position:10%\n<v Ahmed>Hello <c.loud>there</c></v>\n\n00:01:04.000 --> 00:01:05.000\n<i>bye</i>\n";
        var doc = SubtitleParser.Parse(vtt, SubtitleFormat.Vtt);
        Assert.Empty(doc.Problems);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(62), doc.Cues[0].Start);
        var srt = SubtitleWriter.Write(doc, SubtitleFormat.Srt);
        Assert.Equal("1\r\n00:01:02,000 --> 00:01:03,500\r\nHello there\r\n\r\n2\r\n00:01:04,000 --> 00:01:05,000\r\n<i>bye</i>\r\n\r\n", srt);
    }

    [Theory]
    [InlineData("1\n00:00:01,000 -> 00:00:02,000\ntext\n")]
    [InlineData("1\n00:00:61,000 --> 00:00:62,000\ntext\n")]
    [InlineData("1\n00:00:03,000 --> 00:00:02,000\ntext\n")]
    [InlineData("1\nsoon --> later\ntext\n")]
    public void Malformed_timing_is_reported_with_its_line_and_never_guessed(string text)
    {
        var doc = SubtitleParser.Parse(text, SubtitleFormat.Srt);
        Assert.NotEmpty(doc.Problems);
        Assert.Contains("line 2", doc.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Shifting_moves_every_cue_and_clamps_at_zero()
    {
        var doc = SubtitleParser.Parse(Srt, SubtitleFormat.Srt);
        var later = SubtitleTiming.Shift(doc, TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), later.Cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), later.Cues[1].End);
        var earlier = SubtitleTiming.Shift(doc, TimeSpan.FromSeconds(-2));
        Assert.Equal(TimeSpan.Zero, earlier.Cues[0].Start); // 1.0 - 2.0 clamps at 0
        Assert.Equal(TimeSpan.FromMilliseconds(500), earlier.Cues[0].End);
        Assert.Equal(1, SubtitleTiming.ClampedBy(doc, TimeSpan.FromSeconds(-2)));
    }

    [Fact]
    public void Overlapping_cues_are_counted_for_info()
    {
        var doc = SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:05,000\na\n\n2\n00:00:04,000 --> 00:00:06,000\nb\n", SubtitleFormat.Srt);
        Assert.Equal(1, SubtitleTiming.Overlaps(doc));
    }

    [Fact]
    public void Encodings_are_told_apart_by_bom_strict_utf8_and_arabic_runs()
    {
        var arabic = "مرحبا بكم في الفيلم الجديد"; // 1256 is read when the article is as common as in prose (third look)
        var e1256 = CodePages.Get(1256).GetBytes(arabic);
        var western = "Café crème à la française, déjà vu";
        var e1252 = CodePages.Get(1252).GetBytes(western);
        Assert.Equal((arabic, "Windows-1256 (Arabic)"), Decode(e1256));
        Assert.Null(SubtitleEncoding.TryDecode(e1252, out _)); // not guessed: Western, Central European and Turkish share their bytes (second review)
        Assert.Equal((arabic, "UTF-8"), Decode(Encoding.UTF8.GetBytes(arabic)));
        Assert.Equal((arabic, "UTF-8 with BOM"), Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(arabic)]));
        Assert.Equal((arabic, "UTF-16 LE"), Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(arabic)]));
        Assert.Equal((arabic, "UTF-16 BE"), Decode([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(arabic)]));
    }

    private static (string, string) Decode(byte[] bytes)
    {
        var d = SubtitleEncoding.TryDecode(bytes, out var why) ?? throw new InvalidOperationException(why);
        return (d.Text, d.EncodingName);
    }

    [Fact]
    public void Writing_normalises_line_endings_per_format()
    {
        var doc = SubtitleParser.Parse("1\r00:00:01,000 --> 00:00:02,000\rold mac line\r\r", SubtitleFormat.Srt);
        Assert.Empty(doc.Problems);
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,000\r\nold mac line\r\n\r\n", SubtitleWriter.Write(doc, SubtitleFormat.Srt));
        Assert.DoesNotContain('\r', SubtitleWriter.Write(doc, SubtitleFormat.Vtt));
    }

    [Fact]
    public void A_huge_or_endless_input_is_bounded()
    {
        var many = new StringBuilder();
        for (var i = 1; i <= SubtitleParser.MaxCues + 1; i++) many.Append(i).Append("\n00:00:01,000 --> 00:00:02,000\nx\n\n");
        var doc = SubtitleParser.Parse(many.ToString(), SubtitleFormat.Srt);
        Assert.Contains(doc.Problems, p => p.Contains("more than", StringComparison.Ordinal));
    }

    [Fact]
    public void The_menu_offers_conversion_timing_and_tools_for_a_subtitle()
    {
        var resolver = CapabilityResolver.For(customResizeId: null);
        Assert.Same(FileKinds.Srt, FileKinds.IdentifyByName(@"C:\p\film.SRT"));
        Assert.Same(FileKinds.Vtt, FileKinds.IdentifyByName(@"C:\p\film.vtt"));
        var menu = RadialMenu.Build(resolver, [@"C:\p\film.srt"]);
        Assert.Equal(["Convert", "Tools", "Timing", "Actions"], menu.Select(g => g.Label));
        Assert.Equal(["To VTT"], menu[0].Commands.Select(c => c.Label));
        Assert.Equal(["Earlier 1 s", "Earlier 0.5 s", "Later 0.5 s", "Later 1 s"], menu[2].Commands.Select(c => c.Label));
        Assert.Contains("Subtitle info", menu[1].Commands.Select(c => c.Label));
        Assert.Contains("Clean up (UTF-8)", menu[1].Commands.Select(c => c.Label));
        Assert.Equal(["To SRT"], RadialMenu.Build(resolver, [@"C:\p\film.vtt"])[0].Commands.Select(c => c.Label));
        Assert.DoesNotContain(resolver.Resolve([@"C:\p\film.srt", @"C:\p\film.vtt"]), c => c.Id == SubtitleCapabilities.ToVttId);
        Assert.Contains(resolver.Resolve([@"C:\p\film.srt", @"C:\p\film.vtt"]), c => c.Id == SubtitleCapabilities.ShiftLater1Id);
    }

    [Fact]
    public void Outputs_are_named_after_the_source()
    {
        Assert.Equal(@"C:\p\film.vtt", SubtitleCapabilities.Candidates(SubtitleCapabilities.ToVttId, @"C:\p\film.srt", null).First());
        Assert.Equal(@"C:\p\film (1 s later).srt", SubtitleCapabilities.Candidates(SubtitleCapabilities.ShiftLater1Id, @"C:\p\film.srt", null).First());
        Assert.Equal(@"C:\p\film (0.5 s earlier).vtt", SubtitleCapabilities.Candidates(SubtitleCapabilities.ShiftEarlierHalfId, @"C:\p\film.vtt", null).First());
        Assert.Equal(@"C:\p\film (UTF-8).srt", SubtitleCapabilities.Candidates(SubtitleCapabilities.CleanUpId, @"C:\p\film.srt", null).First());
    }
}
