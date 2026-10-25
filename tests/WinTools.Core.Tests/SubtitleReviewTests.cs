using System.Text;
using WinTools.Core.Subtitles;

namespace WinTools.Core.Tests;

/// <summary>
/// The evaluator's findings on V2 PR 7.1, each as a failing input first (ADR-0024 "Evaluator review"). The rule they all
/// follow: when the input is not certain, refuse with the reason; never guess and write something else.
/// </summary>
public sealed class SubtitleReviewTests
{
    [Theory]
    [InlineData(1251, "Привет, как дела? Это тест субтитров.")]           // Cyrillic: runs of high bytes, but not Arabic
    [InlineData(1253, "Καλημέρα, τι κάνεις; Αυτό είναι δοκιμή.")]           // Greek
    public void A_legacy_code_page_that_is_neither_arabic_nor_western_is_refused(int codePage, string text)
    {
        var bytes = CodePages.Get(codePage).GetBytes(text);
        Assert.Null(SubtitleEncoding.TryDecode(bytes, out var why));
        Assert.Contains("Save it as UTF-8", why, StringComparison.Ordinal);
    }

    // Second review, PR 7.1: Western, Central European and Turkish code pages share most byte values, so no content rule
    // tells them apart; each is refused with the reason (save as UTF-8), never read as another script.
    [Theory]
    [InlineData(1252, "Ação! Atenção às ações e emoções.")]
    [InlineData(1252, "Ich weiß es nicht. Das ist schön.")]
    [InlineData(1252, "¿Dónde está? No sé, señor.")]
    [InlineData(1252, "Jag förstår inte vad du säger.")]
    [InlineData(1251, "Я не знаю, но он да. Мы с ним в доме.")]
    [InlineData(1253, "Ναι, το ξέρω. Θα σε δω.")]
    [InlineData(1254, "Teşekkürler. Şimdi gidiyorum.")]
    [InlineData(1250, "Dziękuję. Chodź tutaj, děkuji.")]
    public void A_code_page_that_content_cannot_tell_apart_is_refused_never_misread(int codePage, string text)
    {
        Assert.Null(SubtitleEncoding.TryDecode(CodePages.Get(codePage).GetBytes(text), out var why));
        Assert.Contains("UTF-8", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("مرحبا بكم في الفيلم الجديد")]
    [InlineData("- «هيا بنا»\n- «إلى أين؟» …لا… – حسنا")] // Arabic quotes and dashes are neutral
    [InlineData("الحلقة 12: ما بعد النهاية (الجزء الثاني)")]
    public void Unambiguous_arabic_in_windows_1256_is_read(string text)
    {
        var decoded = SubtitleEncoding.TryDecode(CodePages.Get(1256).GetBytes(text), out var why);
        Assert.True(decoded is not null, why);
        Assert.Equal(text, decoded.Text);
        Assert.Equal("Windows-1256 (Arabic)", decoded.EncodingName);
    }

    [Fact]
    public void Arabic_mixed_with_accented_latin_in_windows_1256_is_refused_not_half_misread() =>
        Assert.Null(SubtitleEncoding.TryDecode(CodePages.Get(1256).GetBytes("مرحبا Hélène, ça va? Très bien, café à côté."), out _));

    [Theory]
    [InlineData("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\na --&gt; b\n")]
    public void A_webvtt_line_that_srt_cannot_hold_stops_the_conversion(string vtt)
    {
        var doc = SubtitleParser.Parse(vtt, SubtitleFormat.Vtt);
        Assert.Empty(doc.Problems);
        Assert.NotNull(SubtitleWriter.CannotWrite(doc, SubtitleFormat.Srt));
    }

    [Fact]
    public void A_webvtt_cue_whose_text_becomes_empty_still_converts()
    {
        var doc = SubtitleParser.Parse("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Bob></v>\n&nbsp;\n\n00:00:03.000 --> 00:00:04.000\nnext\n", SubtitleFormat.Vtt);
        var srt = SubtitleParser.Parse(SubtitleWriter.Write(doc, SubtitleFormat.Srt), SubtitleFormat.Srt);
        Assert.Empty(srt.Problems);
        Assert.Equal(2, srt.Cues.Count);
        Assert.Contains("speaker names", SubtitleWriter.Dropped(doc, SubtitleFormat.Srt));
    }

    [Fact]
    public void Srt_settings_are_only_its_own_position_hints() =>
        Assert.NotEmpty(SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:02,000 Xjunk\ntext\n", SubtitleFormat.Srt).Problems);

    [Fact]
    public void Utf8_with_a_few_damaged_bytes_is_refused_not_read_as_a_code_page()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("مرحبا بكم في الفيلم مرحبا"), 0xE9];
        Assert.Null(SubtitleEncoding.TryDecode(bytes, out var why));
        Assert.Contains("UTF-8", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'c', (byte)'a', (byte)'f', 0xE9 })]       // UTF-8 BOM, then a 1252 byte
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'a', 0x00, (byte)'b' })]                          // UTF-16 LE, odd length
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0xD8, (byte)'a', 0x00 })]                          // a lone surrogate
    public void A_bom_followed_by_text_that_is_not_its_encoding_is_refused(byte[] bytes)
    {
        Assert.Null(SubtitleEncoding.TryDecode(bytes, out var why));
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void A_missing_blank_line_between_cues_is_a_problem_not_one_cue()
    {
        var doc = SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:02,000\nA\n2\n00:00:03,000 --> 00:00:04,000\nB\n", SubtitleFormat.Srt);
        Assert.Contains(doc.Problems, p => p.Contains("line 5", StringComparison.Ordinal));
    }

    [Fact]
    public void Digits_other_than_ascii_are_a_problem_not_a_crash()
    {
        var doc = SubtitleParser.Parse("1\n٠٠:٠٠:٠١,٠٠٠ --> ٠٠:٠٠:٠٢,٠٠٠\nنص\n", SubtitleFormat.Srt);
        Assert.Contains(doc.Problems, p => p.Contains("line 2", StringComparison.Ordinal));
    }

    [Fact]
    public void A_text_line_longer_than_the_limit_is_a_problem_not_cut()
    {
        var doc = SubtitleParser.Parse($"1\n00:00:01,000 --> 00:00:02,000\n{new string('x', SubtitleParser.MaxLineLength + 1)}\n", SubtitleFormat.Srt);
        Assert.Contains(doc.Problems, p => p.Contains("line 3", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("WEBVTT\n   \n00:00:01.000 --> 00:00:02.000\nfirst\n\n00:00:03.000 --> 00:00:04.000\nsecond\n", 2, false)]
    [InlineData("WEBVTT\n00:00:01.000 --> 00:00:02.000\nfirst\n", 0, true)] // the blank line after the header is missing
    public void The_first_webvtt_cue_is_never_swallowed_by_the_header(string vtt, int cues, bool problem)
    {
        var doc = SubtitleParser.Parse(vtt, SubtitleFormat.Vtt);
        Assert.Equal(cues, doc.Cues.Count);
        Assert.Equal(problem, doc.Problems.Count > 0);
    }

    [Fact]
    public void Webvtt_to_webvtt_keeps_the_header_blocks_ids_and_settings()
    {
        const string vtt = "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\nSTYLE\n::cue { color: yellow }\n\nNOTE kept\n\nNOTEBOOK\n00:00:01.000 --> 00:00:02.000 line:0 align:start\n<v Ahmed>top</v>\n\n";
        var doc = SubtitleParser.Parse(vtt, SubtitleFormat.Vtt);
        Assert.Empty(doc.Problems);
        Assert.Single(doc.Cues); // "NOTEBOOK" is a cue id, not a note
        var shifted = SubtitleWriter.Write(SubtitleTiming.Shift(doc, TimeSpan.FromSeconds(1)), SubtitleFormat.Vtt);
        Assert.Equal(vtt.Replace("00:00:01.000 --> 00:00:02.000", "00:00:02.000 --> 00:00:03.000", StringComparison.Ordinal), shifted);
        Assert.NotEmpty(SubtitleWriter.Dropped(doc, SubtitleFormat.Srt)); // the conversion says what SRT cannot hold
        Assert.Empty(SubtitleWriter.Dropped(doc, SubtitleFormat.Vtt));
    }

    [Theory]
    [InlineData("if a<b and c>d", "if a<b and c>d")]                                   // not tags
    [InlineData("<i.loud>x</i> &amp; y &lt;3", "<i>x</i> & y <3")]                     // classes off, entities decoded
    [InlineData("<c.red>red</c> <00:00:01.500>karaoke <lang ar>نص</lang>", "red karaoke نص")]
    public void Webvtt_text_becomes_plain_srt_text(string vttText, string srtText)
    {
        var doc = SubtitleParser.Parse($"WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n{vttText}\n", SubtitleFormat.Vtt);
        Assert.Empty(doc.Problems);
        Assert.Equal([srtText], SubtitleParser.Parse(SubtitleWriter.Write(doc, SubtitleFormat.Srt), SubtitleFormat.Srt).Cues[0].Lines);
    }

    [Fact]
    public void Srt_text_is_escaped_for_webvtt_and_comes_back_the_same()
    {
        var doc = SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:02,000\nI <3 you & <i>mean</i> it\n", SubtitleFormat.Srt);
        var vtt = SubtitleWriter.Write(doc, SubtitleFormat.Vtt);
        Assert.Contains("I &lt;3 you &amp; <i>mean</i> it", vtt, StringComparison.Ordinal);
        Assert.Equal(["I <3 you & <i>mean</i> it"], SubtitleParser.Parse(SubtitleWriter.Write(SubtitleParser.Parse(vtt, SubtitleFormat.Vtt), SubtitleFormat.Srt), SubtitleFormat.Srt).Cues[0].Lines);
    }

    [Fact]
    public void A_file_of_endless_empty_lines_is_bounded()
    {
        var doc = SubtitleParser.Parse(new string('\n', SubtitleParser.MaxLines + 10), SubtitleFormat.Srt);
        Assert.Contains(doc.Problems, p => p.Contains("lines", StringComparison.Ordinal));
    }
}
