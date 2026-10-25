using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Subtitles;
using WinTools.Host.Jobs;
using WinTools.Worker.Text;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 7.1 (ADR-0024): subtitle jobs in the text worker on real files, including Arabic saved as Windows-1256 by an
/// old editor. The source is never changed; a file with errors is not converted; what the worker writes, the host believes.
/// </summary>
public sealed class SubtitleWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static JobRequest Request(string capability, string path) => new(Guid.NewGuid().ToString("N"), capability, [path]);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private const string ArabicSrt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nمرحبا بكم في الفيلم\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nإلى اللقاء\r\n\r\n";

    [Fact]
    public void An_arabic_srt_in_windows_1256_becomes_a_utf8_vtt()
    {
        var source = _dir.Write("film.srt", CodePages.Get(1256).GetBytes(ArabicSrt));
        var before = Hash(source);
        var request = Request(SubtitleCapabilities.ToVttId, source);
        var r = SubtitleJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal("film.vtt", Path.GetFileName(r.OutputPath));
        var vtt = File.ReadAllText(r.OutputPath!, Encoding.UTF8);
        Assert.StartsWith("WEBVTT\n\n", vtt, StringComparison.Ordinal);
        Assert.Contains("مرحبا بكم في الفيلم", vtt, StringComparison.Ordinal);
        Assert.Contains(r.Facts, f => f is { Label: "Read as", Notice: true } && f.Value.Contains("1256", StringComparison.Ordinal)); // said in the toast
        Assert.Equal(before, Hash(source));
        Assert.True(SubtitleJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void Info_names_the_encoding_and_the_problems()
    {
        var good = _dir.Write("good.srt", CodePages.Get(1256).GetBytes(ArabicSrt));
        var facts = SubtitleJobs.Run(Request(SubtitleCapabilities.InfoId, good)).Facts.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("Windows-1256 (Arabic)", facts["Encoding"]);
        Assert.Equal("2", facts["Cues"]);
        Assert.Equal("0:00:01 to 0:00:04", facts["Runs"]);
        var bad = _dir.Write("bad.srt", Encoding.UTF8.GetBytes("1\n00:00:05,000 --> 00:00:01,000\nbackwards\n\n"));
        var info = SubtitleJobs.Run(Request(SubtitleCapabilities.InfoId, bad));
        Assert.True(info.Success);
        Assert.Contains(info.Facts, f => f is { Label: "Problems", Notice: true });
        Assert.True(JobFacts.AreShowable(info.Facts));
    }

    [Fact]
    public void A_file_with_malformed_timing_is_not_changed_or_converted()
    {
        var bad = _dir.Write("bad.srt", Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nok\n\n2\n00:00:99,000 --> 00:01:00,000\nbroken\n\n"));
        var r = SubtitleJobs.Run(Request(SubtitleCapabilities.ShiftLater1Id, bad));
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Contains("line 6", r.Message, StringComparison.Ordinal);
        Assert.Equal(["bad.srt"], _dir.Names());
    }

    [Fact]
    public void A_shift_writes_a_new_file_in_the_same_format()
    {
        var source = _dir.Write("film.vtt", Encoding.UTF8.GetBytes("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nhello\n\n"));
        var r = SubtitleJobs.Run(Request(SubtitleCapabilities.ShiftEarlierHalfId, source));
        Assert.True(r.Success, r.Message);
        Assert.Equal("film (0.5 s earlier).vtt", Path.GetFileName(r.OutputPath));
        Assert.Contains("00:00:00.500 --> 00:00:01.500", File.ReadAllText(r.OutputPath!), StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_up_rewrites_as_utf8_once_and_then_has_nothing_to_do()
    {
        var source = _dir.Write("old.srt", Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nCafé crème\n")); // UTF-8 without a BOM, LF
        var r = SubtitleJobs.Run(Request(SubtitleCapabilities.CleanUpId, source));
        Assert.True(r.Success, r.Message);
        var bytes = File.ReadAllBytes(r.OutputPath!);
        Assert.True(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,000\r\nCafé crème\r\n\r\n", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal(ErrorCode.NothingToDo, SubtitleJobs.Run(Request(SubtitleCapabilities.CleanUpId, r.OutputPath!)).Error);
    }

    [Fact]
    public void A_taken_name_is_kept_and_an_internet_file_marks_its_output()
    {
        var source = _dir.Write("film.srt", Encoding.UTF8.GetBytes(ArabicSrt));
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var taken = _dir.Write("film.vtt", "mine"u8.ToArray());
        var r = SubtitleJobs.Run(Request(SubtitleCapabilities.ToVttId, source));
        Assert.Equal("film (1).vtt", Path.GetFileName(r.OutputPath));
        Assert.Equal("mine", File.ReadAllText(taken));
        Assert.Contains("ZoneId=3", File.ReadAllText(r.OutputPath + ":Zone.Identifier"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_does_not_believe_a_file_that_is_not_what_the_worker_claimed()
    {
        var source = _dir.Write("film.srt", Encoding.UTF8.GetBytes(ArabicSrt));
        var request = Request(SubtitleCapabilities.ToVttId, source);
        var r = SubtitleJobs.Run(request);
        Assert.True(SubtitleJobModule.Instance.IsBelievable(request, r));
        File.WriteAllText(r.OutputPath!, "WEBVTT\n\nnot what was written\n");
        Assert.False(SubtitleJobModule.Instance.IsBelievable(request, r));
        Assert.False(SubtitleJobModule.Instance.IsBelievable(request, r with { Outputs = [r.Outputs[0] with { Path = source }] }));
    }

    [Fact]
    public void An_encoding_that_is_not_certain_is_refused_and_nothing_is_written()
    {
        // Evaluator, PR 7.1 (blocker, reproduced): Cyrillic was read as Arabic and written out as "Saved as UTF-8".
        var russian = _dir.Write("film.srt", CodePages.Get(1251).GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nПривет, как дела?\r\n\r\n"));
        var r = SubtitleJobs.Run(Request(SubtitleCapabilities.CleanUpId, russian));
        Assert.Equal(ErrorCode.UnsupportedContent, r.Error);
        Assert.Equal(["film.srt"], _dir.Names());
        var info = SubtitleJobs.Run(Request(SubtitleCapabilities.InfoId, russian));
        Assert.Contains(info.Facts, f => f is { Label: "Encoding", Value: "Not recognised", Notice: true });
    }

    [Fact]
    public void A_conversion_says_what_the_target_format_cannot_keep()
    {
        var source = _dir.Write("film.vtt", Encoding.UTF8.GetBytes("WEBVTT\n\nSTYLE\n::cue { color: yellow }\n\n00:00:01.000 --> 00:00:02.000 line:0\n<c.red>top</c> &amp; more\n\n"));
        var request = Request(SubtitleCapabilities.ToSrtId, source);
        var r = SubtitleJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Contains(r.Facts, f => f is { Label: "Dropped", Notice: true } && f.Value.Contains("cue positions", StringComparison.Ordinal));
        Assert.Contains("top & more", File.ReadAllText(r.OutputPath!), StringComparison.Ordinal);
        Assert.True(SubtitleJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public async Task The_real_text_worker_runs_subtitle_and_file_jobs_side_by_side()
    {
        var source = _dir.Write("film.srt", Encoding.UTF8.GetBytes(ArabicSrt));
        var client = new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Text.exe"), TimeSpan.FromMinutes(1), SubtitleJobModule.Instance, parallelism: 1);
        var results = await client.RunAsync([Request(SubtitleCapabilities.ToVttId, source), Request(SubtitleCapabilities.InfoId, source)], CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        var files = await new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Text.exe"), TimeSpan.FromMinutes(1), FilesJobModule.Instance, parallelism: 1)
            .RunAsync([Request(FilesCapabilities.Sha256Id, source)], CancellationToken.None);
        Assert.True(files.Single().Success, files.Single().Message);
    }
}
