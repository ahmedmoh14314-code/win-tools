using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;
using WinTools.Worker;

namespace WinTools.Worker.Media;

/// <summary>One stream of a probed file.</summary>
internal sealed record MediaStream(string Type, string Codec, int Width, int Height, int Channels, int SampleRate, double FrameRate);

/// <summary>What ffprobe found: the container, the duration, the overall bit rate and the streams.</summary>
internal sealed record MediaProbe(string Format, double Duration, long BitRate, IReadOnlyList<MediaStream> Streams)
{
    public MediaStream? Video => Streams.FirstOrDefault(s => s.Type == "video");
    public MediaStream? Audio => Streams.FirstOrDefault(s => s.Type == "audio");
}

/// <summary>
/// The media module's jobs (ADR-0028): every input is probed first and must be a container on the allowed list with the
/// streams its kind needs; the work runs in the bundled FFmpeg into a temporary file in the destination, which is probed
/// again (the right streams, a duration) before it takes its final name without overwriting anything. A failure leaves
/// nothing. The source is only read. The result carries the strictest Mark-of-the-Web zone of its sources.
/// </summary>
internal static class MediaJobs
{
    /// <summary>The largest file the module takes, and the longest.</summary>
    public const long MaxInputBytes = 20L << 30;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);

    /// <summary>The engine's own time limit; the host's per-request limit is longer, so this one ends first and cleans up.</summary>
    private static readonly TimeSpan EngineTimeout = TimeSpan.FromMinutes(55);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Most frames Save frames writes (one per second): five minutes of video.</summary>
    public const int MaxFrames = 300;

    /// <summary>A copy must save at least this much to count as smaller (as image Compress, ADR-0017).</summary>
    private const double MinSaving = 0.08;

    private const int ErrorFileExists = unchecked((int)0x80070050), ErrorAlreadyExists = unchecked((int)0x800700B7);

    /// <summary>The containers ffprobe may report for an input: everything else (playlists, image sequences, devices,
    /// raw streams) is refused before any work.</summary>
    private static readonly HashSet<string> AllowedFormats = new(StringComparer.Ordinal)
    {
        "mov,mp4,m4a,3gp,3g2,mj2", "matroska,webm", "avi", "asf", "gif", "mp3", "wav", "flac", "ogg", "aiff", "aac",
    };

    private sealed class MediaFailure(ErrorCode code, string message) : Exception(message)
    {
        public ErrorCode Code { get; } = code;
    }

    public static JobResult Run(JobRequest request)
    {
        var id = request.Id;
        if (id is not { Length: > 0 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return JobResult.Fail("invalid", ErrorCode.InvalidRequest, "Request id must be 1-64 ASCII letters or digits.");
        // Trim and Split take their form's answer, read again here; every other command takes nothing.
        var form = request.Parameters is { Count: 1 } fp && fp.TryGetValue(DialogParameters.Name, out var answer) ? Forms.Read(request.CapabilityId ?? "", answer) : null;
        if (Forms.For(request.CapabilityId ?? "") is not null ? form is null : request.Parameters is { Count: > 0 })
            return JobResult.Fail(id, ErrorCode.InvalidRequest, form is null && request.Parameters is not { Count: > 0 } ? "This command needs its form's answer." : "This command takes no such parameters.");
        if (request.Inputs.Count == 0 || !request.Inputs.All(PathPolicy.IsSupported) || request.Inputs.Any(Path.EndsInDirectorySeparator))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "Every input must be a supported file path.");
        if (request.OutputDirectory is { } od && !PathPolicy.IsSupported(Path.Combine(od, "x.mp4")))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        if (MediaCapabilities.Catalog.All(c => c.Id != request.CapabilityId))
            return JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command.");
        if (!Ffmpeg.IsPresent) return JobResult.Fail(id, ErrorCode.Internal, "The media engine is missing from this copy of WinTools.");
        if (request.CapabilityId != MediaCapabilities.JoinId && request.Inputs.Count != 1)
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "One file per request.");
        try
        {
            return request.CapabilityId switch
            {
                MediaCapabilities.VideoInfoId or MediaCapabilities.AudioInfoId => Info(request),
                MediaCapabilities.JoinId => Join(request),
                MediaCapabilities.FramesId => Frames(request),
                MediaCapabilities.SplitVideoId or MediaCapabilities.SplitAudioId => Split(request, form!),
                _ => Transform(request, form),
            };
        }
        catch (MediaFailure ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (UnauthorizedAccessException) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (IOException ex) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, $"Writing failed (0x{ex.HResult:X8})."); }
    }

    // ---- Probing ----

    /// <summary>The file probed and checked: a kind the module knows, a container on the list, the streams it needs.</summary>
    private static MediaProbe Source(string path, bool needsVideo, bool needsAudio)
    {
        if (Directory.Exists(path)) throw new MediaFailure(ErrorCode.IsAFolder, "The source is a folder, not a file.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & (FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000)) != 0)
            throw new MediaFailure(ErrorCode.OnlineOnly, "The file is online only; reading it would download it.");
        if (info.Length > MaxInputBytes) throw new MediaFailure(ErrorCode.InputTooLarge, $"The file is larger than {MaxInputBytes >> 30} GB.");
        if (info.Length == 0) throw new MediaFailure(ErrorCode.DecodeFailed, "The file is empty.");
        var probe = Probe(path) ?? throw new MediaFailure(ErrorCode.DecodeFailed, "The file could not be read as audio or video; it may be damaged.");
        if (!AllowedFormats.Contains(probe.Format)) throw new MediaFailure(ErrorCode.ContentMismatch, "The file is not a video or audio format WinTools reads, whatever its name says.");
        if (needsVideo && probe.Video is null) throw new MediaFailure(ErrorCode.ContentMismatch, "The file has no picture.");
        if (needsAudio && probe.Audio is null) throw new MediaFailure(ErrorCode.UnsupportedContent, "The file has no sound.");
        if (probe.Video is null && probe.Audio is null) throw new MediaFailure(ErrorCode.ContentMismatch, "The file has neither picture nor sound.");
        if (probe.Duration > MaxDuration.TotalSeconds) throw new MediaFailure(ErrorCode.LimitExceeded, $"The file is longer than {MaxDuration.TotalHours:0} hours.");
        return probe;
    }

    internal static MediaProbe? Probe(string path)
    {
        var run = Ffmpeg.Probe(["-v", "error", "-protocol_whitelist", "file", "-show_entries",
            "format=format_name,duration,bit_rate:stream=codec_type,codec_name,width,height,channels,sample_rate,avg_frame_rate",
            "-of", "json", Ffmpeg.FileUrl(path)], ProbeTimeout);
        if (!run.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(run.Output);
            var root = doc.RootElement;
            if (!root.TryGetProperty("format", out var format)) return null;
            static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            static double Num(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) ? v.ValueKind switch
                {
                    JsonValueKind.Number => v.GetDouble(),
                    JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                    _ => 0,
                } : 0;
            static double Rate(string r)
            {
                var parts = r.Split('/');
                return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0 ? a / b : 0;
            }
            var streams = new List<MediaStream>();
            if (root.TryGetProperty("streams", out var list))
                foreach (var s in list.EnumerateArray())
                    streams.Add(new(Str(s, "codec_type"), Str(s, "codec_name"), (int)Num(s, "width"), (int)Num(s, "height"),
                        (int)Num(s, "channels"), (int)Num(s, "sample_rate"), Rate(Str(s, "avg_frame_rate"))));
            // A still picture stored as a "video" stream (cover art in an MP3, an attached thumbnail) is not a video.
            streams.RemoveAll(s => s.Type == "video" && s.Codec is "mjpeg" or "png" && Str(format, "format_name") is not "avi" and not "matroska,webm");
            return new MediaProbe(Str(format, "format_name"), Num(format, "duration"), (long)Num(format, "bit_rate"), streams);
        }
        catch (JsonException) { return null; }
    }

    // ---- Info ----

    private static JobResult Info(JobRequest request)
    {
        var video = request.CapabilityId == MediaCapabilities.VideoInfoId;
        var path = request.Inputs[0];
        var p = Source(path, needsVideo: video, needsAudio: !video);
        var facts = new List<JobFact>
        {
            new("Type", FormatName(p.Format, path)),
            new("Duration", Clock(p.Duration)),
            new("Size", WinTools.Core.Feedback.FeedbackText.Size(new FileInfo(path).Length)),
        };
        if (p.Video is { } v)
        {
            facts.Add(new("Picture", $"{v.Width} × {v.Height} px, {v.Codec.ToUpperInvariant()}"));
            if (v.FrameRate > 0) facts.Add(new("Frame rate", $"{v.FrameRate.ToString("0.##", CultureInfo.InvariantCulture)} fps"));
        }
        if (p.Audio is { } a)
            facts.Add(new("Sound", $"{a.Codec.ToUpperInvariant()}, {Channels(a.Channels)}{(a.SampleRate > 0 ? $", {a.SampleRate / 1000.0:0.#} kHz" : "")}"));
        else if (video) facts.Add(new("Sound", "None"));
        if (p.BitRate > 0) facts.Add(new("Bit rate", $"{p.BitRate / 1000:N0} kbit/s"));
        return JobResult.WithFacts(request.Id, facts);
    }

    private static string Channels(int n) => n switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => $"{n} channels" };

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatName(string format, string path) => format switch
    {
        "mov,mp4,m4a,3gp,3g2,mj2" => Path.GetExtension(path).ToLowerInvariant() switch { ".mov" => "QuickTime video", ".m4a" or ".aac" => "M4A audio", _ => "MP4 video" },
        "matroska,webm" => Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "WebM video" : "MKV video",
        "avi" => "AVI video",
        "asf" => Path.GetExtension(path).Equals(".wma", StringComparison.OrdinalIgnoreCase) ? "WMA audio" : "WMV video",
        "gif" => "GIF animation",
        "mp3" => "MP3 audio",
        "wav" => "WAV audio",
        "flac" => "FLAC audio",
        "ogg" => Path.GetExtension(path).Equals(".opus", StringComparison.OrdinalIgnoreCase) ? "Opus audio" : "OGG audio",
        "aiff" => "AIFF audio",
        "aac" => "AAC audio",
        _ => format,
    };

    // ---- Encoders ----

    /// <summary>The muxer for an output extension: the temporary file's own name says nothing to the engine.</summary>
    private static string Muxer(string extension) => extension switch
    {
        ".mp4" => "mp4", ".mov" => "mov", ".mkv" => "matroska", ".webm" => "webm", ".gif" => "gif", ".avi" => "avi", ".wmv" or ".wma" => "asf",
        ".mp3" => "mp3", ".m4a" => "ipod", ".wav" => "wav", ".flac" => "flac", ".ogg" => "ogg", ".opus" => "opus", ".aiff" => "aiff",
        _ => throw new MediaFailure(ErrorCode.UnsupportedOperation, "No output format for this file."),
    };

    /// <summary>H.264 through Windows' own encoder (Media Foundation, licensed with Windows), at a quality or a bit rate.</summary>
    private static string[] H264(long? bitRate) => bitRate is { } b
        ? ["-c:v", "h264_mf", "-rate_control", "cbr", "-b:v", b.ToString(CultureInfo.InvariantCulture)]
        : ["-c:v", "h264_mf", "-rate_control", "quality", "-quality", "75"];

    /// <summary>OpenH264 at the same bit rate, when Windows' encoder is missing (Windows N editions).</summary>
    private static string[] OpenH264(long? bitRate) => ["-c:v", "libopenh264", "-b:v", (bitRate ?? 4_000_000).ToString(CultureInfo.InvariantCulture)];

    /// <summary>The video and audio codecs of an output container, at a bit rate when one is given.</summary>
    private static string[][] VideoCodecs(string extension, long? bitRate) => extension switch
    {
        ".webm" => [["-c:v", "libvpx-vp9", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", .. bitRate is { } b ? new[] { "-b:v", b.ToString(CultureInfo.InvariantCulture) } : ["-b:v", "0", "-crf", "32"], "-c:a", "libopus", "-b:a", "128k"]],
        ".avi" => [["-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-q:a", "3"]],
        ".wmv" => [["-c:v", "wmv2", "-b:v", Math.Clamp(bitRate ?? 4_000_000, 500_000, 8_000_000).ToString(CultureInfo.InvariantCulture), "-c:a", "wmav2", "-b:a", "192k"]],
        // MP4, MOV, MKV: H.264 and AAC; the second set is the fallback encoder.
        _ => [[.. H264(bitRate), "-c:a", "aac", "-b:a", "160k"], [.. OpenH264(bitRate), "-c:a", "aac", "-b:a", "160k"]],
    };

    private static string[] AudioCodec(string extension, int? kbps = null) => extension switch
    {
        ".mp3" => kbps is { } k ? ["-c:a", "libmp3lame", "-b:a", $"{k}k"] : ["-c:a", "libmp3lame", "-q:a", "2"],
        ".m4a" => ["-c:a", "aac", "-b:a", $"{kbps ?? 192}k"],
        ".wav" => ["-c:a", "pcm_s16le"],
        ".aiff" => ["-c:a", "pcm_s16be"],
        ".flac" => ["-c:a", "flac"],
        ".ogg" => kbps is { } k2 ? ["-c:a", "libvorbis", "-b:a", $"{k2}k"] : ["-c:a", "libvorbis", "-q:a", "5"],
        ".opus" => ["-c:a", "libopus", "-b:a", $"{kbps ?? 128}k"],
        ".wma" => ["-c:a", "wmav2", "-b:a", $"{kbps ?? 192}k"],
        _ => throw new MediaFailure(ErrorCode.UnsupportedOperation, "No audio format for this file."),
    };

    /// <summary>Even dimensions (H.264 and MPEG-4 need them), and the 4:2:0 pixels every player shows.</summary>
    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    // ---- One file in, one file out ----

    private static JobResult Transform(JobRequest request, FormValues? form)
    {
        var path = request.Inputs[0];
        var id = request.CapabilityId;
        var target = MediaCapabilities.TargetOf(id);
        var isVideoKind = FileKinds.ByExtensionOf(path) is { Family: FileFamily.Video };
        var videoOut = target is not null ? MediaCapabilities.VideoTargets.Contains(target)
            : id is MediaCapabilities.CompressVideoId or MediaCapabilities.CompressVideo720Id or MediaCapabilities.Speed2xId or MediaCapabilities.SpeedHalfId or MediaCapabilities.MuteId
                or MediaCapabilities.TrimVideoId || MediaCapabilities.AspectOf(id) is not null;
        var p = Source(path, needsVideo: videoOut || isVideoKind && id != MediaCapabilities.ExtractAudioId,
            needsAudio: !videoOut && !(isVideoKind && id == MediaCapabilities.MuteId));
        var ext = MediaCapabilities.ExtensionOf(id, path)!;
        var input = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file", "-i", Ffmpeg.FileUrl(path) };
        var notices = new List<JobFact>();
        var variants = new List<string[]>();
        var srcVideoBits = p.BitRate > 0 ? p.BitRate - (p.Audio is null ? 0 : 160_000) : 0;

        switch (id)
        {
            case MediaCapabilities.TrimVideoId or MediaCapabilities.TrimAudioId:
            {
                // Seeking on the input and re-encoding: the cut is at the moment asked for, not at the keyframe before it.
                var start = form!.Get(Forms.TrimStart)!.Value;
                var end = form.Get(Forms.TrimEnd);
                if (p.Duration > 0 && start >= p.Duration - 0.05) throw new MediaFailure(ErrorCode.NothingToDo, $"The file ends at {Clock(p.Duration)}, before the start.");
                if (end is { } late && p.Duration > 0 && late > p.Duration)
                {
                    notices.Add(new("End", $"The file ends at {Clock(p.Duration)}", Notice: true));
                    end = null;
                }
                // From the beginning to the end is no cut: a lossy copy of the whole file is not a trim (review, #38).
                if (start <= 0.05 && (end is null || p.Duration > 0 && end >= p.Duration - 0.05))
                    throw new MediaFailure(ErrorCode.NothingToDo, "Nothing to cut: the start is the beginning and the end is the end of the file.");
                input.InsertRange(input.IndexOf("-i"), ["-ss", Seconds(start)]);
                string[] length = end is { } e ? ["-t", Seconds(e - start)] : [];
                if (videoOut)
                    foreach (var codecs in VideoCodecs(ext, null))
                        variants.Add([.. length, "-map", "0:v:0", "-map", "0:a:0?", "-vf", $"{EvenSize},format=yuv420p", .. codecs, .. Faststart(ext)]);
                else variants.Add([.. length, "-vn", "-map", "0:a:0", .. AudioCodec(ext)]);
                break;
            }
            case var _ when MediaCapabilities.AspectOf(id) is { } aspect:
            {
                // Centred, on the picture as it is shown: a phone video's rotation is applied first, and non-square pixels (a
                // camcorder's 1440 × 1080 shown 16:9, a DVD's 720 × 576) are made square before the shape is measured (review,
                // #38). Even pixels.
                var a = $"{aspect.W}/{aspect.H}";
                var square = "scale='trunc(iw*if(gt(sar,0),sar,1)/2)*2':ih,setsar=1";
                var crop = $"{square},crop='trunc(if(gt(iw/ih,{a}),ih*{a},iw)/2)*2':'trunc(if(gt(iw/ih,{a}),ih,iw/({a}))/2)*2'";
                foreach (var codecs in VideoCodecs(ext, null))
                    variants.Add(["-map", "0:v:0", "-map", "0:a:0?", "-vf", $"{crop},setsar=1,format=yuv420p", .. codecs, .. Faststart(ext)]);
                break;
            }
            case MediaCapabilities.MuteId:
                // The picture as it is, the sound gone: no re-encoding, so no loss and little time.
                if (p.Audio is null) throw new MediaFailure(ErrorCode.NothingToDo, "The video has no sound.");
                variants.Add(["-map", "0", "-map", "-0:a", "-c", "copy"]);
                break;
            case MediaCapabilities.ExtractAudioId:
                // AAC already: copied as it is; otherwise encoded to AAC.
                variants.Add(p.Audio!.Codec == "aac" ? ["-vn", "-map", "0:a:0", "-c:a", "copy"] : ["-vn", "-map", "0:a:0", "-c:a", "aac", "-b:a", "192k"]);
                break;
            case MediaCapabilities.CompressVideoId or MediaCapabilities.CompressVideo720Id:
            {
                var to720 = id == MediaCapabilities.CompressVideo720Id && p.Video!.Height > 720;
                var target2 = (long)Math.Max(250_000, (srcVideoBits > 0 ? srcVideoBits : 4_000_000) * (to720 ? 0.35 : 0.5));
                if (to720) target2 = Math.Min(target2, 2_500_000);
                var filter = to720 ? $"scale=-2:720,{EvenSize}" : EvenSize;
                foreach (var codecs in VideoCodecs(ext, target2))
                    variants.Add(["-map", "0:v:0", "-map", "0:a:0?", "-vf", $"{filter},format=yuv420p", .. codecs, .. Faststart(ext)]);
                break;
            }
            case MediaCapabilities.Speed2xId or MediaCapabilities.SpeedHalfId:
            {
                var two = id == MediaCapabilities.Speed2xId;
                var v = two ? "setpts=PTS/2" : "setpts=2*PTS";
                var a = two ? "atempo=2" : "atempo=0.5";
                var graph = p.Audio is null ? $"[0:v:0]{v},{EvenSize},format=yuv420p[v]" : $"[0:v:0]{v},{EvenSize},format=yuv420p[v];[0:a:0]{a}[a]";
                foreach (var codecs in VideoCodecs(ext, null))
                    variants.Add(["-filter_complex", graph, "-map", "[v]", .. p.Audio is null ? Array.Empty<string>() : ["-map", "[a]"], .. codecs, .. Faststart(ext)]);
                break;
            }
            case MediaCapabilities.NormalizeId:
                variants.Add(["-vn", "-map", "0:a:0", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", Math.Max(p.Audio!.SampleRate, 44_100).ToString(CultureInfo.InvariantCulture), .. AudioCodec(ext)]);
                break;
            case MediaCapabilities.MonoId or MediaCapabilities.StereoId:
            {
                var channels = id == MediaCapabilities.MonoId ? 1 : 2;
                if (p.Audio!.Channels == channels) throw new MediaFailure(ErrorCode.NothingToDo, channels == 1 ? "Already mono." : "Already stereo.");
                variants.Add(["-vn", "-map", "0:a:0", "-ac", channels.ToString(CultureInfo.InvariantCulture), .. AudioCodec(ext)]);
                break;
            }
            case MediaCapabilities.CompressAudioId:
            {
                var kbps = ext switch { ".opus" => 64, ".m4a" when FileKinds.ByExtensionOf(path) is { } k && k != FileKinds.M4a => 192, _ => 128 };
                variants.Add(["-vn", "-map", "0:a:0", .. AudioCodec(ext, kbps)]);
                break;
            }
            case var _ when target is not null && videoOut:
                if (ext == ".gif")
                {
                    // An animated GIF of the first minute: 12 frames a second, at most 640 px wide, its own palette.
                    if (p.Duration > 60) notices.Add(new("Length", "The first minute only (GIFs grow large)", Notice: true));
                    variants.Add(["-t", "60", "-map", "0:v:0", "-vf", "fps=12,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4", "-loop", "0"]);
                }
                else
                    foreach (var codecs in VideoCodecs(ext, null))
                        variants.Add(["-map", "0:v:0", "-map", "0:a:0?", "-vf", $"{EvenSize},format=yuv420p", .. codecs, .. Faststart(ext)]);
                break;
            case var _ when target is not null:
                variants.Add(["-vn", "-map", "0:a:0", .. AudioCodec(ext)]);
                break;
            default:
                throw new MediaFailure(ErrorCode.UnsupportedOperation, "This worker does not run that command.");
        }

        var result = Produce(request, path, ext, input, variants, expectVideo: videoOut && ext != ".m4a", expectAudio: !videoOut || id != MediaCapabilities.MuteId && p.Audio is not null && ext != ".gif");
        if (MediaCapabilities.Catalog.First(c => c.Id == id).Feedback is { Savings: true } && result.Success)
        {
            var before = new FileInfo(path).Length;
            var after = new FileInfo(result.Outputs[0].Path).Length;
            if (after > before * (1 - MinSaving))
            {
                File.Delete(result.Outputs[0].Path);
                return JobResult.Fail(request.Id, ErrorCode.NothingToDo, "A smaller copy would not save enough.");
            }
        }
        return notices.Count == 0 || !result.Success ? result : result with { Facts = [.. result.Facts, .. notices] };
    }

    private static string[] Faststart(string ext) => ext is ".mp4" or ".mov" ? ["-movflags", "+faststart"] : [];

    /// <summary>Seconds as the engine reads them, invariant ("83.5").</summary>
    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs the engine into a temporary file in the destination with each variant in turn (the first that works wins:
    /// Windows' H.264 encoder, then OpenH264), probes the result, and gives it the first free name.
    /// </summary>
    private static JobResult Produce(JobRequest request, string source, string ext, IReadOnlyList<string> input, IReadOnlyList<string[]> variants,
        bool expectVideo, bool expectAudio, IReadOnlyList<string>? sources = null)
    {
        var temp = OutputNaming.TempPath(source, request.Id, request.OutputDirectory);
        var dir = Path.GetDirectoryName(temp)!;
        if (!Directory.Exists(dir)) throw new MediaFailure(ErrorCode.OutputDirectoryNotWritable, "The destination folder is not there.");
        var zone = (sources ?? [source]).Select(MarkOfTheWeb.ZoneToCarry).Aggregate((int?)null, (a, b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value));
        var committed = false;
        try
        {
            EngineRun? last = null;
            foreach (var variant in variants)
            {
                DeleteQuietly(temp);
                last = Ffmpeg.Convert([.. input, .. variant, "-f", Muxer(ext), Ffmpeg.FileUrl(temp)], EngineTimeout);
                if (last.Ok && File.Exists(temp) && new FileInfo(temp).Length > 0) break;
                last = last with { ExitCode = last.ExitCode == 0 ? -2 : last.ExitCode };
            }
            if (last is null || !last.Ok)
            {
                if (last?.TimedOut == true) return JobResult.Fail(request.Id, ErrorCode.WorkerTimeout, "The work took too long and was stopped; nothing was kept.");
                return JobResult.Fail(request.Id, ErrorCode.EncodeFailed, "The file could not be converted; it may be damaged or use a format WinTools cannot write.");
            }
            // The result must open, with the streams it should have.
            var check = Probe(temp);
            if (check is null || (expectVideo && check.Video is null) || (expectAudio && check.Audio is null) || (check.Duration <= 0 && ext != ".gif"))
                return JobResult.Fail(request.Id, ErrorCode.OutputValidationFailed, "The result did not check out; nothing was kept.");
            if (zone is { } z) MarkOfTheWeb.Apply(temp, z);
            string hash;
            using (var f = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read)) hash = Convert.ToHexStringLower(SHA256.HashData(f));
            foreach (var candidate in MediaCapabilities.Candidates(request.CapabilityId, source, request.OutputDirectory))
            {
                try
                {
                    File.Move(temp, candidate, overwrite: false);
                    committed = true;
                    return JobResult.Ok(request.Id, new JobOutput(candidate, hash));
                }
                catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists) { }
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free output name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) DeleteQuietly(temp);
        }
    }

    // ---- Join ----

    private static JobResult Join(JobRequest request)
    {
        var inputs = PdfLikeOrder(request.Inputs);
        var probes = inputs.Select(i => Source(i, needsVideo: true, needsAudio: false)).ToArray();
        var first = probes[0].Video!;
        var (w, h) = (first.Width + first.Width % 2, first.Height + first.Height % 2);
        var withSound = probes.All(p => p.Audio is not null);
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file" };
        foreach (var i in inputs) args.AddRange(["-i", Ffmpeg.FileUrl(i)]);
        var graph = new System.Text.StringBuilder();
        for (var i = 0; i < inputs.Count; i++)
        {
            // Every clip in the first one's frame (letterboxed when its shape differs), at one frame rate.
            graph.Append(CultureInfo.InvariantCulture, $"[{i}:v:0]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{i}];");
            if (withSound) graph.Append(CultureInfo.InvariantCulture, $"[{i}:a:0]aresample=48000,aformat=channel_layouts=stereo[a{i}];");
        }
        for (var i = 0; i < inputs.Count; i++) graph.Append(withSound ? $"[v{i}][a{i}]" : $"[v{i}]");
        graph.Append(CultureInfo.InvariantCulture, $"concat=n={inputs.Count}:v=1:a={(withSound ? 1 : 0)}[v]{(withSound ? "[a]" : "")}");
        var map = withSound ? new[] { "-map", "[v]", "-map", "[a]" } : ["-map", "[v]"];
        var variants = VideoCodecs(".mp4", null).Select(c => (string[])["-filter_complex", graph.ToString(), .. map, .. c, "-movflags", "+faststart"]).ToArray();
        var result = Produce(request, inputs[0], ".mp4", args, variants, expectVideo: true, expectAudio: withSound, inputs);
        if (result.Success && !withSound && probes.Any(p => p.Audio is not null))
            result = result with { Facts = [new("Sound", "Left out: not every video has sound", Notice: true)] };
        return result;
    }

    /// <summary>Natural name order ("clip 2" before "clip 10"), as Merge orders PDFs.</summary>
    private static IReadOnlyList<string> PdfLikeOrder(IReadOnlyList<string> inputs) => PdfCapabilities.MergeOrder(inputs);

    // ---- Frames ----

    private static JobResult Frames(JobRequest request)
    {
        var path = request.Inputs[0];
        var p = Source(path, needsVideo: true, needsAudio: false);
        var staging = OutputNaming.TempPath(path, request.Id, request.OutputDirectory);
        switch (ExclusiveDirectory.Create(staging))
        {
            case CreateOutcome.Created: break;
            case CreateOutcome.NotWritable: throw new MediaFailure(ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable.");
            default: throw new MediaFailure(ErrorCode.Internal, "Something else is using this job's folder in the destination.");
        }
        var committed = false;
        try
        {
            File.SetAttributes(staging, FileAttributes.Hidden | FileAttributes.Directory);
            var run = Ffmpeg.Convert(["-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file", "-i", Ffmpeg.FileUrl(path),
                "-map", "0:v:0", "-vf", "fps=1", "-frames:v", MaxFrames.ToString(CultureInfo.InvariantCulture), "-f", "image2", Ffmpeg.PatternUrl(staging, "frame %04d.png")], EngineTimeout);
            var frames = Directory.GetFiles(staging, "frame *.png");
            if (!run.Ok || frames.Length == 0)
                return JobResult.Fail(request.Id, run.TimedOut ? ErrorCode.WorkerTimeout : ErrorCode.EncodeFailed, "The frames could not be saved; nothing was kept.");
            if (MarkOfTheWeb.ZoneToCarry(path) is { } z) foreach (var f in frames) MarkOfTheWeb.Apply(f, z);
            File.SetAttributes(staging, FileAttributes.Directory);
            var shape = FolderManifest.ShapeOf(staging) ?? throw new MediaFailure(ErrorCode.OutputValidationFailed, "The frames did not check out; nothing was kept.");
            foreach (var candidate in MediaCapabilities.Candidates(request.CapabilityId, path, request.OutputDirectory))
            {
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                try { Directory.Move(staging, candidate); }
                catch (IOException) { continue; }
                committed = true;
                var facts = new List<JobFact> { new("Frames", frames.Length.ToString("N0", CultureInfo.InvariantCulture)) };
                if (p.Duration > MaxFrames + 1) facts.Add(new("Length", $"The first {MaxFrames / 60} minutes, one frame a second", Notice: true));
                return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, shape)], facts);
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free folder name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) { try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    // ---- Split ----

    /// <summary>
    /// Equal parts, each re-encoded so it starts on its own frame. A video goes through the segment muxer, which cuts at the
    /// given times, where keyframes are forced; sound is encoded one part per run, so each part has a whole header of its
    /// own (FLAC's holds its length). The parts go to a hidden folder in the destination, are each probed, and the folder takes its
    /// final name ("clip (parts)") only when every part checks out.
    /// </summary>
    private static JobResult Split(JobRequest request, FormValues form)
    {
        var path = request.Inputs[0];
        var video = request.CapabilityId == MediaCapabilities.SplitVideoId;
        var p = Source(path, needsVideo: video, needsAudio: !video);
        var parts = (int)form.Get(Forms.SplitParts)!.Value;
        if (p.Duration / parts < 1) throw new MediaFailure(ErrorCode.NothingToDo, $"The file is too short for {parts} parts of a second or more.");
        var ext = MediaCapabilities.ExtensionOf(video ? MediaCapabilities.TrimVideoId : MediaCapabilities.TrimAudioId, path)!;
        var times = string.Join(",", Enumerable.Range(1, parts - 1).Select(i => Seconds(Math.Round(p.Duration * i / parts, 3))));
        var staging = OutputNaming.TempPath(path, request.Id, request.OutputDirectory);
        switch (ExclusiveDirectory.Create(staging))
        {
            case CreateOutcome.Created: break;
            case CreateOutcome.NotWritable: throw new MediaFailure(ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable.");
            default: throw new MediaFailure(ErrorCode.Internal, "Something else is using this job's folder in the destination.");
        }
        var committed = false;
        try
        {
            File.SetAttributes(staging, FileAttributes.Hidden | FileAttributes.Directory);
            string[] input = ["-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file", "-i", Ffmpeg.FileUrl(path)];
            string[] faststart = ext is ".mp4" or ".mov" ? ["-segment_format_options", "movflags=+faststart"] : [];
            string[] segment = ["-f", "segment", "-segment_times", times, "-segment_format", Muxer(ext), "-reset_timestamps", "1", .. faststart,
                Ffmpeg.PatternUrl(staging, "part %02d" + ext)];
            var variants = VideoCodecs(ext, null).Select(c => (string[])["-map", "0:v:0", "-map", "0:a:0?", "-vf", $"{EvenSize},format=yuv420p", "-force_key_frames", times, .. c]);
            EngineRun? run = null;
            string[] files = [];
            if (video)
                foreach (var variant in variants)
                {
                    foreach (var f in Directory.GetFiles(staging)) File.Delete(f);
                    run = Ffmpeg.Convert([.. input, .. variant, .. segment], EngineTimeout);
                    files = Directory.GetFiles(staging, "part *" + ext);
                    if (run.Ok && files.Length == parts) break;
                }
            else
            {
                // Audio: each part in its own run, so each gets a whole header (the segment muxer leaves FLAC parts without
                // a correct one; review, #38). Decoding from a seek point is cheap for sound.
                var cuts = Enumerable.Range(0, parts + 1).Select(i => Math.Round(p.Duration * i / parts, 3)).ToArray();
                for (var i = 0; i < parts; i++)
                {
                    var part = Path.Combine(staging, $"part {i:00}{ext}");
                    run = Ffmpeg.Convert(["-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file", "-ss", Seconds(cuts[i]), "-i", Ffmpeg.FileUrl(path),
                        .. i < parts - 1 ? new[] { "-t", Seconds(cuts[i + 1] - cuts[i]) } : [], "-vn", "-map", "0:a:0", .. AudioCodec(ext), "-f", Muxer(ext), Ffmpeg.FileUrl(part)], EngineTimeout);
                    if (!run.Ok) break;
                }
                files = Directory.GetFiles(staging, "part *" + ext);
            }
            if (run is null || !run.Ok || files.Length != parts)
                return JobResult.Fail(request.Id, run?.TimedOut == true ? ErrorCode.WorkerTimeout : ErrorCode.EncodeFailed, "The file could not be split; nothing was kept.");
            foreach (var f in files)
                if (Probe(f) is not { Duration: > 0 } check || (video ? check.Video is null : check.Audio is null))
                    return JobResult.Fail(request.Id, ErrorCode.OutputValidationFailed, "A part did not check out; nothing was kept.");
            if (MarkOfTheWeb.ZoneToCarry(path) is { } z) foreach (var f in files) MarkOfTheWeb.Apply(f, z);
            File.SetAttributes(staging, FileAttributes.Directory);
            var shape = FolderManifest.ShapeOf(staging) ?? throw new MediaFailure(ErrorCode.OutputValidationFailed, "The parts did not check out; nothing was kept.");
            foreach (var candidate in MediaCapabilities.Candidates(request.CapabilityId, path, request.OutputDirectory))
            {
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                try { Directory.Move(staging, candidate); }
                catch (IOException) { continue; }
                committed = true;
                return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, shape)], [new("Parts", parts.ToString(CultureInfo.InvariantCulture), Notice: true)]);
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free folder name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) { try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
