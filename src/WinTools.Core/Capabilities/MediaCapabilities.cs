using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Output;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The Media module (V2 media sprint, ADR-0028): video and audio through the bundled FFmpeg (LGPL build), run by the media
/// worker as a child process. The wheel offers what fits the dropped file:
/// <list type="bullet">
/// <item>Video: Convert (MP4, WebM, MKV, MOV, GIF, AVI, WMV), Compress, Edit (speed, join), Audio (extract, remove), and
/// Tools (save frames, info).</item>
/// <item>Audio: Convert (MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA), Compress, Edit (normalize, mono, stereo), and Tools (info).</item>
/// </list>
/// Every result is a new file next to the original (or in the output folder), never over an existing one.
/// </summary>
public static class MediaCapabilities
{
    public const string Module = "media";

    public const string VideoInfoId = "media.video-info", AudioInfoId = "media.audio-info";
    public const string CompressVideoId = "media.compress", CompressVideo720Id = "media.compress-720", CompressAudioId = "media.compress-audio";
    public const string Speed2xId = "media.speed-2x", SpeedHalfId = "media.speed-half", JoinId = "media.join";
    public const string ExtractAudioId = "media.extract-audio", MuteId = "media.mute", FramesId = "media.frames";
    public const string NormalizeId = "media.normalize", MonoId = "media.mono", StereoId = "media.stereo";

    /// <summary>The most files Join takes at once.</summary>
    public const int MaxJoinInputs = 50;

    public static IReadOnlyList<CapabilityCategory> Categories { get; } =
    [
        new("convert", "Convert", "convert", 10),
        new("compress", "Compress", "compress", 20),
        new("edit", "Edit", "edit", 30),
        new("sound", "Audio", "sound", 50),
        new("tools", "Tools", "tools", 90),
    ];

    /// <summary>A target format: its id suffix, label, extension, and the kinds that already are it (no "To MP4" on an MP4).</summary>
    public sealed record Target(string Key, string Label, string Extension, IReadOnlySet<string> SameKinds);

    private static HashSet<string> Kinds(params FileKind[] kinds) => kinds.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<Target> VideoTargets { get; } =
    [
        new("mp4", "To MP4", ".mp4", Kinds(FileKinds.Mp4)),
        new("webm", "To WebM", ".webm", Kinds(FileKinds.WebM)),
        new("mkv", "To MKV", ".mkv", Kinds(FileKinds.Mkv)),
        new("mov", "To MOV", ".mov", Kinds(FileKinds.Mov)),
        new("gif", "To GIF", ".gif", Kinds(FileKinds.Gif)),
        new("avi", "To AVI", ".avi", Kinds(FileKinds.Avi)),
        new("wmv", "To WMV", ".wmv", Kinds(FileKinds.Wmv)),
    ];

    public static IReadOnlyList<Target> AudioTargets { get; } =
    [
        new("mp3", "To MP3", ".mp3", Kinds(FileKinds.Mp3)),
        new("m4a", "To M4A", ".m4a", Kinds(FileKinds.M4a)),
        new("wav", "To WAV", ".wav", Kinds(FileKinds.Wav)),
        new("flac", "To FLAC", ".flac", Kinds(FileKinds.Flac)),
        new("ogg", "To OGG", ".ogg", Kinds(FileKinds.Ogg)),
        new("opus", "To Opus", ".opus", Kinds(FileKinds.Opus)),
        new("aiff", "To AIFF", ".aiff", Kinds(FileKinds.Aiff)),
        new("wma", "To WMA", ".wma", Kinds(FileKinds.Wma)),
    ];

    public static string ConvertId(Target target, bool video) => (video ? "media.video-to-" : "media.audio-to-") + target.Key;

    private static readonly IReadOnlySet<string> Video = FileKinds.Video.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Audio = FileKinds.Audio.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> MovingVideo = FileKinds.Video.Where(k => k != FileKinds.Gif).Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

    private static FeedbackWords Words(string working, string verb, string done, string noun = "video", string nouns = "videos") =>
        new(working, verb, done, "Nothing needed doing", noun, nouns);

    private static CapabilityDescriptor Convert(Target t, bool video) =>
        new(ConvertId(t, video), t.Label, "convert", Module, new InputRule((video ? Video : Audio).Except(t.SameKinds).ToHashSet(StringComparer.Ordinal)),
            CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: video ? Words("Converting", "convert", $"Converted {t.Label.ToLowerInvariant()}") : Words("Converting", "convert", $"Converted {t.Label.ToLowerInvariant()}", "audio file", "audio files"),
            Payload: t);

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        .. VideoTargets.Select(t => Convert(t, video: true)),
        .. AudioTargets.Select(t => Convert(t, video: false)),
        new(CompressVideoId, "Smaller", "compress", Module, new InputRule(MovingVideo), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Compressing", "compress", "Compressed", "Already small: a smaller copy would not save enough", "video", "videos", Savings: true)),
        new(CompressVideo720Id, "720p, smaller", "compress", Module, new InputRule(MovingVideo), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Compressing", "compress", "Compressed to 720p", "Already small: a smaller copy would not save enough", "video", "videos", Savings: true)),
        new(CompressAudioId, "Smaller", "compress", Module, new InputRule(Audio), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Compressing", "compress", "Compressed", "Already small: a smaller copy would not save enough", "audio file", "audio files", Savings: true)),
        new(Speed2xId, "2× speed", "edit", Module, new InputRule(MovingVideo), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: Words("Speeding up", "speed up", "Twice as fast")),
        new(SpeedHalfId, "½ speed", "edit", Module, new InputRule(MovingVideo), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: Words("Slowing down", "slow down", "Half as fast")),
        new(JoinId, "Join", "edit", Module, new InputRule(MovingVideo, MinCount: 2, MaxCount: MaxJoinInputs), CapabilityShape.Selection, CapabilityOutput.File, LongRunning: true,
            Feedback: Words("Joining", "join", "Joined")),
        new(ExtractAudioId, "Extract audio", "sound", Module, new InputRule(MovingVideo), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: Words("Extracting the audio of", "extract the audio of", "Audio extracted")),
        new(MuteId, "Remove audio", "sound", Module, new InputRule(MovingVideo), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: Words("Removing the audio of", "remove the audio of", "Audio removed")),
        new(NormalizeId, "Normalize volume", "edit", Module, new InputRule(Audio), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: Words("Normalizing", "normalize", "Volume normalized", "audio file", "audio files")),
        new(MonoId, "To mono", "edit", Module, new InputRule(Audio), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Converting", "convert", "Now mono", "Already mono", "audio file", "audio files")),
        new(StereoId, "To stereo", "edit", Module, new InputRule(Audio), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Converting", "convert", "Now stereo", "Already stereo", "audio file", "audio files")),
        new(FramesId, "Save frames", "tools", Module, new InputRule(MovingVideo), CapabilityShape.PerFile, CapabilityOutput.Folder, LongRunning: true,
            Feedback: Words("Saving frames of", "save frames of", "Frames saved (one per second)")),
        new(VideoInfoId, "Video info", "tools", Module, new InputRule(Video), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: false,
            Feedback: Words("Reading", "read", "Read")),
        new(AudioInfoId, "Audio info", "tools", Module, new InputRule(Audio), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: false,
            Feedback: Words("Reading", "read", "Read", "audio file", "audio files")),
    ];

    /// <summary>The target of a Convert command, or null.</summary>
    public static Target? TargetOf(string capabilityId) =>
        Catalog.FirstOrDefault(c => c.Id == capabilityId)?.Payload as Target;

    /// <summary>The extension of a command's output ("report.mp4", "song.m4a"), or null for one that writes no file.</summary>
    public static string? ExtensionOf(string capabilityId, string sourcePath)
    {
        var source = FileKinds.ByExtensionOf(sourcePath);
        return capabilityId switch
        {
            ExtractAudioId => ".m4a",
            FramesId or VideoInfoId or AudioInfoId => null,
            JoinId => ".mp4",
            _ when TargetOf(capabilityId) is { } t => t.Extension,
            // A re-encoded video is H.264 in MP4, which every player opens; WebM stays WebM (VP9).
            CompressVideoId or CompressVideo720Id or Speed2xId or SpeedHalfId => source == FileKinds.WebM ? ".webm" : ".mp4",
            // A smaller copy of a lossless file is AAC in M4A; a lossy one keeps its format at a lower rate.
            CompressAudioId when source == FileKinds.Wav || source == FileKinds.Flac || source == FileKinds.Aiff => ".m4a",
            // Otherwise the source's own container: the audio removed, a quieter or mono copy of the same kind of file.
            _ => Path.GetExtension(sourcePath).ToLowerInvariant(),
        };
    }

    /// <summary>The words added to an output's name ("clip (compressed).mp4"); none for a conversion, which changes the extension.</summary>
    public static string? NameSuffix(string capabilityId) => capabilityId switch
    {
        CompressVideoId or CompressAudioId => "compressed",
        CompressVideo720Id => "720p",
        Speed2xId => "2x",
        SpeedHalfId => "half speed",
        JoinId => "joined",
        ExtractAudioId => "audio",
        MuteId => "no audio",
        NormalizeId => "normalized",
        MonoId => "mono",
        StereoId => "stereo",
        FramesId => "frames",
        _ => null,
    };

    /// <summary>The names to try for a command's output, in order, never an existing one (the worker moves without overwriting).</summary>
    public static IEnumerable<string> Candidates(string capabilityId, string sourcePath, string? outputDirectory) =>
        capabilityId == FramesId
            ? OutputNaming.FolderCandidates(sourcePath, NameSuffix(capabilityId)!, outputDirectory)
            : OutputNaming.CandidatesWithExtension(sourcePath, ExtensionOf(capabilityId, sourcePath)!, NameSuffix(capabilityId), outputDirectory);
}
