using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Media;

namespace WinTools.Worker.Tests;

/// <summary>
/// The Media module (ADR-0028) on the bundled FFmpeg: short clips and tones made here by the same engine (a test
/// pattern and a sine), so no sample is committed. Every result opens with the right streams, carries a new name, and
/// is believed by the host; the source is never changed; nothing is left behind; files that are not media, or that
/// name other files or the network, are refused before any work.
/// </summary>
public sealed class MediaWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static readonly string Engine = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    /// <summary>A clip made by the engine: a test pattern, with a tone unless <paramref name="sound"/> is false.</summary>
    private string Clip(string name, double seconds = 3, bool sound = true, int width = 320, int height = 240, int channels = 2, string sar = "1")
    {
        var path = Path.Combine(_dir.Path, name);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var ext = Path.GetExtension(name);
        var audioOnly = ext is ".mp3" or ".m4a" or ".wav" or ".flac" or ".ogg" or ".oga" or ".opus" or ".aiff" or ".aif" or ".wma";
        if (!audioOnly) args.AddRange(["-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=25:duration={seconds}:sar={sar}"]);
        if (sound || audioOnly) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}:sample_rate=44100"]);
        if (audioOnly || sound) args.AddRange(["-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        args.AddRange(ext switch
        {
            ".mp4" or ".m4v" or ".mov" or ".mkv" => ["-c:v", "libopenh264", "-b:v", "800k", "-pix_fmt", "yuv420p", "-c:a", "aac"],
            ".webm" => ["-c:v", "libvpx-vp9", "-b:v", "500k", "-c:a", "libopus"],
            ".avi" => ["-c:v", "mpeg4", "-c:a", "libmp3lame"],
            ".wmv" => ["-c:v", "wmv2", "-c:a", "wmav2"],
            ".mp3" => ["-c:a", "libmp3lame", "-b:a", "192k"],
            ".m4a" => ["-c:a", "aac", "-b:a", "192k"],
            ".wav" => ["-c:a", "pcm_s16le"],
            ".flac" => ["-c:a", "flac"],
            ".aif" => ["-c:a", "pcm_s16be", "-f", "aiff"],
            ".oga" => ["-c:a", "libopus", "-f", "ogg"],
            _ => [],
        });
        args.Add(path);
        var psi = new ProcessStartInfo(Engine) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        return path;
    }

    private static JobRequest Request(string capability, params string[] inputs) => new(Guid.NewGuid().ToString("N"), capability, inputs);

    private string[] Names() => [.. Directory.EnumerateFileSystemEntries(_dir.Path).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    private static JobResult RunBelieved(JobRequest request)
    {
        var r = MediaJobs.Run(request);
        Assert.True(r.Success, $"{request.CapabilityId}: {r.Error} {r.Message}");
        Assert.True(MediaJobModule.Instance.IsBelievable(request, r), $"{request.CapabilityId}: the host does not believe it");
        return r;
    }

    [Theory]
    [InlineData("media.video-to-webm", ".webm")]
    [InlineData("media.video-to-mkv", ".mkv")]
    [InlineData("media.video-to-mov", ".mov")]
    [InlineData("media.video-to-gif", ".gif")]
    [InlineData("media.video-to-avi", ".avi")]
    [InlineData("media.video-to-wmv", ".wmv")]
    public void A_video_converts_to_every_format_and_the_source_is_unchanged(string capability, string extension)
    {
        var clip = Clip("clip.mp4");
        var before = Hash.Of(clip);
        var r = RunBelieved(Request(capability, clip));
        var output = r.Outputs.Single().Path;
        Assert.Equal("clip" + extension, Path.GetFileName(output));
        var probe = MediaJobs.Probe(output)!;
        Assert.NotNull(probe.Video);
        if (extension != ".gif") Assert.NotNull(probe.Audio);
        Assert.Equal(before, Hash.Of(clip));
        Assert.Equal(new[] { "clip" + extension, "clip.mp4" }.Order(StringComparer.Ordinal), Names()); // nothing else left behind
    }

    [Fact]
    public void Other_containers_convert_to_MP4_with_H264()
    {
        foreach (var name in new[] { "a.webm", "b.avi", "c.wmv", "d.mkv" })
        {
            var r = RunBelieved(Request("media.video-to-mp4", Clip(name)));
            var probe = MediaJobs.Probe(r.Outputs.Single().Path)!;
            Assert.Equal("h264", probe.Video!.Codec);
            Assert.Equal("aac", probe.Audio!.Codec);
        }
    }

    [Theory]
    [InlineData("media.audio-to-mp3", ".mp3", "mp3")]
    [InlineData("media.audio-to-m4a", ".m4a", "aac")]
    [InlineData("media.audio-to-flac", ".flac", "flac")]
    [InlineData("media.audio-to-ogg", ".ogg", "vorbis")]
    [InlineData("media.audio-to-opus", ".opus", "opus")]
    [InlineData("media.audio-to-aiff", ".aiff", "pcm_s16be")]
    [InlineData("media.audio-to-wma", ".wma", "wmav2")]
    public void Audio_converts_to_every_format(string capability, string extension, string codec)
    {
        var song = Clip("song.wav");
        var r = RunBelieved(Request(capability, song));
        Assert.Equal("song" + extension, Path.GetFileName(r.Outputs.Single().Path));
        Assert.Equal(codec, MediaJobs.Probe(r.Outputs.Single().Path)!.Audio!.Codec);
        Assert.Equal(new[] { "song" + extension, "song.wav" }.Order(StringComparer.Ordinal), Names());
    }

    [Fact]
    public void The_wheel_offers_video_commands_for_a_video_and_audio_commands_for_audio_and_no_conversion_to_itself()
    {
        var resolver = CapabilityResolver.For(null);
        var video = resolver.Resolve([@"C:\p\clip.mp4"]).Select(c => c.Id).ToArray();
        Assert.Contains("media.video-to-webm", video);
        Assert.DoesNotContain("media.video-to-mp4", video);
        Assert.Contains(MediaCapabilities.ExtractAudioId, video);
        Assert.DoesNotContain("media.audio-to-mp3", video);
        var audio = resolver.Resolve([@"C:\p\song.flac"]).Select(c => c.Id).ToArray();
        Assert.Contains("media.audio-to-mp3", audio);
        Assert.DoesNotContain("media.audio-to-flac", audio);
        Assert.Contains(MediaCapabilities.NormalizeId, audio);
        Assert.DoesNotContain("media.video-to-mp4", audio);
        Assert.Contains(MediaCapabilities.JoinId, resolver.Resolve([@"C:\p\a.mp4", @"C:\p\b.mov"]).Select(c => c.Id));
    }

    [Fact]
    public void Speed_halves_or_doubles_the_length()
    {
        var clip = Clip("clip.mp4", seconds: 4);
        var fast = RunBelieved(Request(MediaCapabilities.Speed2xId, clip));
        Assert.InRange(MediaJobs.Probe(fast.Outputs.Single().Path)!.Duration, 1.7, 2.4);
        var slow = RunBelieved(Request(MediaCapabilities.SpeedHalfId, clip));
        Assert.InRange(MediaJobs.Probe(slow.Outputs.Single().Path)!.Duration, 7.5, 8.6);
        Assert.Equal("clip (2x).mp4", Path.GetFileName(fast.Outputs.Single().Path));
    }

    [Fact]
    public void Audio_can_be_extracted_or_removed()
    {
        var clip = Clip("clip.mp4");
        var audio = RunBelieved(Request(MediaCapabilities.ExtractAudioId, clip));
        var a = MediaJobs.Probe(audio.Outputs.Single().Path)!;
        Assert.Equal(("clip (audio).m4a", (string?)null), (Path.GetFileName(audio.Outputs.Single().Path), a.Video?.Codec));
        Assert.NotNull(a.Audio);
        var mute = RunBelieved(Request(MediaCapabilities.MuteId, clip));
        var m = MediaJobs.Probe(mute.Outputs.Single().Path)!;
        Assert.Null(m.Audio);
        Assert.NotNull(m.Video);
        var silent = Clip("silent.mp4", sound: false);
        Assert.Equal(ErrorCode.NothingToDo, MediaJobs.Run(Request(MediaCapabilities.MuteId, silent)).Error);
        Assert.Equal(ErrorCode.UnsupportedContent, MediaJobs.Run(Request(MediaCapabilities.ExtractAudioId, silent)).Error);
    }

    [Fact]
    public void Volume_and_channels()
    {
        var stereo = Clip("stereo.mp3");
        Assert.Equal(1, MediaJobs.Probe(RunBelieved(Request(MediaCapabilities.MonoId, stereo)).Outputs.Single().Path)!.Audio!.Channels);
        Assert.Equal(ErrorCode.NothingToDo, MediaJobs.Run(Request(MediaCapabilities.StereoId, stereo)).Error);
        var mono = Clip("mono.wav", channels: 1);
        Assert.Equal(2, MediaJobs.Probe(RunBelieved(Request(MediaCapabilities.StereoId, mono)).Outputs.Single().Path)!.Audio!.Channels);
        var normalized = RunBelieved(Request(MediaCapabilities.NormalizeId, stereo));
        Assert.Equal("stereo (normalized).mp3", Path.GetFileName(normalized.Outputs.Single().Path));
    }

    [Fact]
    public void Compress_writes_a_smaller_copy_or_says_it_would_not_save_enough()
    {
        var song = Clip("song.wav", seconds: 6);
        var r = RunBelieved(Request(MediaCapabilities.CompressAudioId, song));
        Assert.Equal("song (compressed).m4a", Path.GetFileName(r.Outputs.Single().Path));
        Assert.True(new FileInfo(r.Outputs.Single().Path).Length < new FileInfo(song).Length / 2);
        var small = Clip("small.mp4", width: 160, height: 120);
        var v = MediaJobs.Run(Request(MediaCapabilities.CompressVideoId, small));
        Assert.True(v.Success || v.Error == ErrorCode.NothingToDo, $"{v.Error} {v.Message}");
        Assert.DoesNotContain(Names(), n => n.StartsWith("~wintools", StringComparison.Ordinal));
    }

    [Fact]
    public void Join_puts_clips_end_to_end_in_name_order_even_of_different_sizes()
    {
        var a = Clip("part 1.mp4", seconds: 2);
        var b = Clip("part 2.webm", seconds: 3, width: 640, height: 360);
        var r = RunBelieved(Request(MediaCapabilities.JoinId, b, a));
        Assert.Equal("part 1 (joined).mp4", Path.GetFileName(r.Outputs.Single().Path));
        var p = MediaJobs.Probe(r.Outputs.Single().Path)!;
        Assert.InRange(p.Duration, 4.6, 5.5);
        Assert.Equal((320, 240), (p.Video!.Width, p.Video.Height));
        Assert.NotNull(p.Audio);
    }

    [Fact]
    public void Save_frames_writes_one_picture_a_second_into_a_new_folder()
    {
        var clip = Clip("clip.mp4", seconds: 4);
        var r = RunBelieved(Request(MediaCapabilities.FramesId, clip));
        var folder = r.Outputs.Single().Path;
        Assert.Equal("clip (frames)", Path.GetFileName(folder));
        Assert.InRange(Directory.GetFiles(folder, "*.png").Length, 4, 5);
    }

    [Fact]
    public void Info_reads_the_streams()
    {
        var r = RunBelieved(Request(MediaCapabilities.VideoInfoId, Clip("clip.mkv", seconds: 3)));
        Assert.Contains(r.Facts, f => f.Label == "Picture" && f.Value.StartsWith("320 × 240", StringComparison.Ordinal));
        Assert.Contains(r.Facts, f => f.Label == "Duration" && f.Value == "0:03");
        var a = RunBelieved(Request(MediaCapabilities.AudioInfoId, Clip("song.flac")));
        Assert.Contains(a.Facts, f => f.Label == "Sound" && f.Value.Contains("stereo", StringComparison.Ordinal));
    }

    [Fact]
    public void Files_that_are_not_media_or_that_name_other_files_are_refused_before_any_work()
    {
        var text = _dir.Write("notes.mp4", "just some text"u8.ToArray());
        Assert.False(MediaJobs.Run(Request("media.video-to-webm", text)).Success);
        // A playlist pointing at the network, and a concat list pointing at another file, named as videos.
        var playlist = _dir.Write("playlist.mp4", "#EXTM3U\n#EXTINF:10,\nhttp://127.0.0.1:9/never.ts\n"u8.ToArray());
        Assert.False(MediaJobs.Run(Request("media.video-to-webm", playlist)).Success);
        var concat = _dir.Write("list.mp3", System.Text.Encoding.UTF8.GetBytes($"ffconcat version 1.0\nfile '{Clip("real.wav").Replace("\\", "/", StringComparison.Ordinal)}'\n"));
        Assert.False(MediaJobs.Run(Request("media.audio-to-mp3", concat)).Success);
        var empty = _dir.Write("empty.mp3", []);
        Assert.Equal(ErrorCode.DecodeFailed, MediaJobs.Run(Request("media.audio-to-wav", empty)).Error);
        Assert.Equal(["empty.mp3", "list.mp3", "notes.mp4", "playlist.mp4", "real.wav"], Names());
    }

    [Fact]
    public void An_existing_name_is_never_overwritten()
    {
        var clip = Clip("clip.mp4");
        File.WriteAllText(Path.Combine(_dir.Path, "clip.webm"), "mine");
        var r = RunBelieved(Request("media.video-to-webm", clip));
        Assert.Equal("clip (1).webm", Path.GetFileName(r.Outputs.Single().Path));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir.Path, "clip.webm")));
    }

    [Fact]
    public async Task The_real_worker_process_runs_a_job_the_host_believes()
    {
        var song = Clip("song.wav");
        var client = new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Media.exe"), TimeSpan.FromMinutes(2), MediaJobModule.Instance, parallelism: 1);
        var results = await client.RunAsync([Request("media.audio-to-mp3", song), Request(MediaCapabilities.AudioInfoId, song)], CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(Path.Combine(_dir.Path, "song.mp3"), results[0].Outputs.Single().Path);
    }

    // ---- Trim, Split and Crop (Tangerine parity): the form's answer is the one parameter ----

    private static JobRequest WithForm(string capability, string answer, params string[] inputs) =>
        new(Guid.NewGuid().ToString("N"), capability, inputs, Parameters: new Dictionary<string, string> { [DialogParameters.Name] = answer });

    [Fact]
    public void Trim_keeps_the_part_between_start_and_end_of_a_video_and_of_audio()
    {
        var clip = Clip("clip.mp4", seconds: 6);
        var before = Hash.Of(clip);
        var r = RunBelieved(WithForm(MediaCapabilities.TrimVideoId, "start=1;end=3.5", clip));
        Assert.Equal("clip (trimmed).mp4", Path.GetFileName(r.Outputs.Single().Path));
        var probe = MediaJobs.Probe(r.Outputs[0].Path)!;
        Assert.InRange(probe.Duration, 2.3, 2.7);
        Assert.NotNull(probe.Video);
        Assert.NotNull(probe.Audio);
        Assert.Equal(before, Hash.Of(clip));
        // No end: everything after the start.
        var song = Clip("song.mp3", seconds: 5);
        var a = RunBelieved(WithForm(MediaCapabilities.TrimAudioId, "start=2;end=", song));
        Assert.Equal("song (trimmed).mp3", Path.GetFileName(a.Outputs[0].Path));
        Assert.InRange(MediaJobs.Probe(a.Outputs[0].Path)!.Duration, 2.7, 3.3);
    }

    [Fact]
    public void Trim_past_the_end_says_so_and_an_end_beyond_the_file_keeps_the_rest_with_a_notice()
    {
        var song = Clip("song.wav", seconds: 3);
        var past = MediaJobs.Run(WithForm(MediaCapabilities.TrimAudioId, "start=10;end=", song));
        Assert.Equal(ErrorCode.NothingToDo, past.Error);
        var r = RunBelieved(WithForm(MediaCapabilities.TrimAudioId, "start=1;end=60", song));
        Assert.Contains(r.Facts, f => f is { Label: "End", Notice: true });
        Assert.InRange(MediaJobs.Probe(r.Outputs[0].Path)!.Duration, 1.8, 2.2);
    }

    [Fact]
    public void A_form_command_runs_only_with_a_valid_answer_and_other_commands_take_none()
    {
        var song = Clip("song.wav", seconds: 2);
        Assert.Equal(ErrorCode.InvalidRequest, MediaJobs.Run(Request(MediaCapabilities.TrimAudioId, song)).Error);
        Assert.Equal(ErrorCode.InvalidRequest, MediaJobs.Run(WithForm(MediaCapabilities.TrimAudioId, "start=2;end=1", song)).Error);
        Assert.Equal(ErrorCode.InvalidRequest, MediaJobs.Run(WithForm(MediaCapabilities.TrimAudioId, "start=01;end=", song)).Error); // not canonical
        Assert.Equal(ErrorCode.InvalidRequest, MediaJobs.Run(WithForm(MediaCapabilities.MonoId, "start=0;end=", song)).Error);
        Assert.Equal(["song.wav"], Names());
    }

    [Fact]
    public void Split_writes_equal_parts_into_a_new_folder()
    {
        var clip = Clip("clip.mp4", seconds: 6);
        var r = RunBelieved(WithForm(MediaCapabilities.SplitVideoId, "parts=3", clip));
        var folder = r.Outputs.Single().Path;
        Assert.Equal("clip (parts)", Path.GetFileName(folder));
        var parts = Directory.GetFiles(folder).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["part 00.mp4", "part 01.mp4", "part 02.mp4"], parts.Select(p => Path.GetFileName(p)));
        Assert.All(parts, p => Assert.InRange(MediaJobs.Probe(p)!.Duration, 1.6, 2.4));
        var song = Clip("song.m4a", seconds: 4);
        var a = RunBelieved(WithForm(MediaCapabilities.SplitAudioId, "parts=2", song));
        Assert.Equal(2, Directory.GetFiles(a.Outputs[0].Path, "part *.m4a").Length);
        Assert.Equal(ErrorCode.NothingToDo, MediaJobs.Run(WithForm(MediaCapabilities.SplitAudioId, "parts=20", Clip("short.wav", seconds: 2))).Error);
    }

    [Fact]
    public void Trim_that_keeps_the_whole_file_cuts_nothing_and_says_so()
    {
        // The default answer (0:00 and no end) would have been a lossy copy of the whole file (review, #38).
        var song = Clip("song.wav", seconds: 3);
        Assert.Equal(ErrorCode.NothingToDo, MediaJobs.Run(WithForm(MediaCapabilities.TrimAudioId, "start=0;end=", song)).Error);
        Assert.Equal(ErrorCode.NothingToDo, MediaJobs.Run(WithForm(MediaCapabilities.TrimAudioId, "start=0;end=3", song)).Error);
        Assert.Equal(ErrorCode.NothingToDo, MediaJobs.Run(WithForm(MediaCapabilities.TrimAudioId, "start=0;end=60", song)).Error);
        Assert.Equal(["song.wav"], Names());
    }

    [Theory]
    [InlineData("clip.m4v", MediaCapabilities.TrimVideoId, "clip (trimmed).mp4")]
    [InlineData("song.aif", MediaCapabilities.TrimAudioId, "song (trimmed).aiff")]
    [InlineData("song.oga", MediaCapabilities.TrimAudioId, "song (trimmed).ogg")]
    [InlineData("song.flac", MediaCapabilities.TrimAudioId, "song (trimmed).flac")]
    public void A_file_under_a_second_name_for_its_format_is_written_under_the_usual_one(string name, string capability, string output)
    {
        // There is no muxer named after .m4v, .aif or .oga, so those edits once failed (review, #38).
        var r = RunBelieved(WithForm(capability, "start=1;end=", Clip(name, seconds: 3)));
        Assert.Equal(output, Path.GetFileName(r.Outputs[0].Path));
        Assert.InRange(MediaJobs.Probe(r.Outputs[0].Path)!.Duration, 1.7, 2.3);
    }

    [Fact]
    public void Split_of_FLAC_writes_parts_whose_headers_hold_their_own_length()
    {
        // The segment muxer left every FLAC part with the whole file's header (review, #38).
        var r = RunBelieved(WithForm(MediaCapabilities.SplitAudioId, "parts=3", Clip("song.flac", seconds: 6)));
        var parts = Directory.GetFiles(r.Outputs[0].Path).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["part 00.flac", "part 01.flac", "part 02.flac"], parts.Select(p => Path.GetFileName(p)));
        Assert.All(parts, p => Assert.InRange(MediaJobs.Probe(p)!.Duration, 1.8, 2.2));
    }

    [Fact]
    public void The_host_believes_a_split_only_with_the_parts_the_form_asked_for()
    {
        var clip = Clip("clip.mp4", seconds: 4);
        var two = WithForm(MediaCapabilities.SplitVideoId, "parts=2", clip);
        var r = RunBelieved(two);
        // The same folder, as it is on the disk, claimed for three parts: two are too few (review, #38).
        var three = two with { Parameters = new Dictionary<string, string> { [DialogParameters.Name] = "parts=3" } };
        Assert.False(MediaJobModule.Instance.IsBelievable(three, r));
        Assert.False(MediaJobModule.Instance.IsBelievable(two with { Parameters = null }, r));
    }

    [Fact]
    public void Crop_measures_the_picture_as_it_is_shown_when_pixels_are_not_square()
    {
        // 240 x 180 stored, shown 320 x 180 (4:3 pixels): already 16:9 on screen, so all of it is kept, in square pixels.
        var clip = Clip("wide.mp4", seconds: 2, width: 240, height: 180, sar: "4/3");
        var r = RunBelieved(Request(MediaCapabilities.CropWideId, clip));
        var v = MediaJobs.Probe(r.Outputs[0].Path)!.Video!;
        Assert.Equal((320, 180), (v.Width, v.Height));
    }

    [Theory]
    [InlineData(MediaCapabilities.CropSquareId, 240, 240, "clip (1x1).mp4")]
    [InlineData(MediaCapabilities.CropWideId, 320, 180, "clip (16x9).mp4")]
    [InlineData(MediaCapabilities.CropTallId, 134, 240, "clip (9x16).mp4")]
    public void Crop_cuts_the_centre_to_the_shape(string capability, int width, int height, string name)
    {
        var clip = Clip("clip.mp4", seconds: 2);
        var r = RunBelieved(Request(capability, clip));
        Assert.Equal(name, Path.GetFileName(r.Outputs[0].Path));
        var v = MediaJobs.Probe(r.Outputs[0].Path)!.Video!;
        Assert.Equal((width, height), (v.Width, v.Height));
    }

    [Fact]
    public void A_percent_sign_in_the_folder_is_not_read_as_a_pattern()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir.Path, "100%d done")).FullName;
        var clip = Clip("clip.mp4", seconds: 3);
        var moved = Path.Combine(sub, "clip.mp4");
        File.Move(clip, moved);
        var r = RunBelieved(Request(MediaCapabilities.FramesId, moved));
        Assert.StartsWith(sub, r.Outputs[0].Path, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.GetFiles(r.Outputs[0].Path, "frame *.png"));
    }
}
