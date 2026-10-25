using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Subtitles;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The Subtitles module (V2 PR 7.1, ADR-0024), run by the text worker: SRT ↔ WebVTT, timing shifts by fixed amounts
/// from the ring (a custom offset needs the dialog, a later PR), a clean-up that rewrites the file as UTF-8 with uniform
/// line endings, and Subtitle info. Every output is a new file; the source is only read.
/// </summary>
public static class SubtitleCapabilities
{
    public const string Module = "subtitles";
    public const string ToVttId = "subtitles.to-vtt", ToSrtId = "subtitles.to-srt", CleanUpId = "subtitles.utf8", InfoId = "subtitles.info";
    public const string ShiftEarlier1Id = "subtitles.shift-minus-1000", ShiftEarlierHalfId = "subtitles.shift-minus-500";
    public const string ShiftLaterHalfId = "subtitles.shift-plus-500", ShiftLater1Id = "subtitles.shift-plus-1000";

    /// <summary>The largest subtitle file read (a feature film's SRT is well under 1 MB).</summary>
    public const long MaxInputBytes = 20L << 20;

    /// <summary>Convert and Tools are shared with the image module; Timing is this module's own.</summary>
    public static IReadOnlyList<CapabilityCategory> Categories { get; } =
    [
        new("convert", "Convert", "convert", 10),
        new("timing", "Timing", "timing", 35),
        new("tools", "Tools", "tools", 11), // Convert, then Tools, then the module's own groups, Actions last (Tangerine's order)
    ];

    private static readonly IReadOnlySet<string> SrtOnly = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Srt.Id };
    private static readonly IReadOnlySet<string> VttOnly = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Vtt.Id };
    private static readonly IReadOnlySet<string> AnySubtitle = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Srt.Id, FileKinds.Vtt.Id };

    private static FeedbackWords Words(string working, string verb, string done, string nothing = "Nothing needed doing") => new(working, verb, done, nothing, "subtitle file", "subtitle files");

    private static CapabilityDescriptor Shift(string id, string label, string done) =>
        new(id, label, "timing", Module, new InputRule(AnySubtitle), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true, Feedback: Words("Shifting", "shift", done));

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        new(ToVttId, "To VTT", "convert", Module, new InputRule(SrtOnly), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true, Feedback: Words("Converting", "convert", "Converted to VTT")),
        new(ToSrtId, "To SRT", "convert", Module, new InputRule(VttOnly), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true, Feedback: Words("Converting", "convert", "Converted to SRT")),
        Shift(ShiftEarlier1Id, "Earlier 1 s", "Shown 1 s earlier"),
        Shift(ShiftEarlierHalfId, "Earlier 0.5 s", "Shown 0.5 s earlier"),
        Shift(ShiftLaterHalfId, "Later 0.5 s", "Shown 0.5 s later"),
        Shift(ShiftLater1Id, "Later 1 s", "Shown 1 s later"),
        new(InfoId, "Subtitle info", "tools", Module, new InputRule(AnySubtitle), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: false, Feedback: Words("Reading", "read", "Read")),
        new(CleanUpId, "Clean up (UTF-8)", "tools", Module, new InputRule(AnySubtitle), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: Words("Cleaning up", "clean up", "Saved as UTF-8", "Already clean UTF-8")),
    ];

    /// <summary>The shift of a timing command, or null.</summary>
    public static TimeSpan? ShiftOf(string id) => id switch
    {
        ShiftEarlier1Id => TimeSpan.FromMilliseconds(-1000),
        ShiftEarlierHalfId => TimeSpan.FromMilliseconds(-500),
        ShiftLaterHalfId => TimeSpan.FromMilliseconds(500),
        ShiftLater1Id => TimeSpan.FromMilliseconds(1000),
        _ => null,
    };

    /// <summary>The format a command writes for a source: the target of a conversion, otherwise the source's own.</summary>
    public static SubtitleFormat OutputFormat(string id, string source) => id switch
    {
        ToVttId => SubtitleFormat.Vtt,
        ToSrtId => SubtitleFormat.Srt,
        _ => FormatOf(source),
    };

    public static SubtitleFormat FormatOf(string path) => FileKinds.IdentifyByName(path) == FileKinds.Vtt ? SubtitleFormat.Vtt : SubtitleFormat.Srt;

    /// <summary>Output names: "film.vtt" for a conversion, "film (1 s later).srt", "film (UTF-8).srt".</summary>
    public static IEnumerable<string> Candidates(string id, string source, string? outputDirectory)
    {
        var ext = OutputFormat(id, source) == SubtitleFormat.Vtt ? ".vtt" : ".srt";
        string? suffix = id switch
        {
            ToVttId or ToSrtId => null,
            CleanUpId => "UTF-8",
            _ when ShiftOf(id) is { } by => $"{Math.Abs(by.TotalSeconds).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s {(by > TimeSpan.Zero ? "later" : "earlier")}",
            _ => throw new ArgumentException($"{id} writes no file.", nameof(id)),
        };
        return Output.OutputNaming.CandidatesWithExtension(source, ext, suffix, outputDirectory);
    }
}
