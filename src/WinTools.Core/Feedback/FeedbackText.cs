using System.Globalization;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Core.Feedback;

public enum FeedbackKind { Success, Partial, Failure, Nothing }

/// <summary>What a toast says: a headline and one line of detail.</summary>
public sealed record FeedbackMessage(FeedbackKind Kind, string Title, string Detail);

/// <summary>One request of a finished job: what it was about (a file name), the worker's result and the sizes before
/// and after (bytes).</summary>
public sealed record JobItem(string SourceName, JobResult Result, long? SourceBytes = null, long? OutputBytes = null, int Inputs = 1);

/// <summary>
/// The words shown to the user when a batch ends. Short, plain and actionable: the outcome first, the reason in the
/// user's terms (never an error code, a stack trace or a full path). The detailed result stays in the local log.
/// </summary>
public static class FeedbackText
{
    public static FeedbackMessage For(Operation operation, IReadOnlyList<JobItem> items) => For(WordsOf(operation), items);

    /// <summary>The image module's words for an operation.</summary>
    public static FeedbackWords WordsOf(Operation op) => new(Working(op), Verb(op), Done(op), NothingReason(op), "image", "images");

    public static FeedbackMessage For(FeedbackWords words, IReadOnlyList<JobItem> items)
    {
        string Images(int n) => words.Count(n);
        var ok = items.Where(i => i.Result.Success).ToArray();
        var unchanged = items.Where(i => i.Result.Error == ErrorCode.NothingToDo).ToArray();
        var failed = items.Where(i => !i.Result.Success && i.Result.Error != ErrorCode.NothingToDo).ToArray();

        if (items.Count == 1)
        {
            var item = items[0];
            if (item.Result.Success)
                return new(FeedbackKind.Success, $"{OutputName(item)} created", Join(words.Done, Sizes(item)));
            if (item.Result.Error == ErrorCode.NothingToDo)
                return new(FeedbackKind.Nothing, "Nothing to change", words.NothingReason);
            return new(FeedbackKind.Failure, $"Could not {words.Verb} {item.SourceName}", Reason(item.Result.Error));
        }

        var n = items.Count;
        if (failed.Length == 0 && unchanged.Length == 0)
            return new(FeedbackKind.Success, $"{Images(n)} completed", Join(words.Done, TotalSizes(ok)));
        if (ok.Length == 0 && failed.Length == 0)
            return new(FeedbackKind.Nothing, "Nothing to change", $"{words.NothingReason} ({Images(n)})");
        if (ok.Length == 0 && unchanged.Length == 0)
            return new(FeedbackKind.Failure, $"Could not {words.Verb} {Images(n)}", MostCommonReason(failed));

        var parts = new List<string>();
        if (failed.Length > 0) parts.Add($"{failed.Length} failed: {MostCommonReason(failed).TrimEnd('.')}");
        if (unchanged.Length > 0) parts.Add($"{unchanged.Length} unchanged");
        var kind = failed.Length > 0 ? FeedbackKind.Partial : FeedbackKind.Success;
        return new(kind, $"{Images(ok.Length)} of {n} completed", string.Join(" · ", parts));
    }

    /// <summary>
    /// What a command that fills the clipboard says (Copy path): what it copied, for how many items, or why it could not.
    /// </summary>
    public static FeedbackMessage Copied(FeedbackWords words, JobItem item) => item.Result.Success
        ? new(FeedbackKind.Success, item.Inputs == 1 ? words.Done : $"{words.Count(item.Inputs)} copied", item.SourceName)
        : new(FeedbackKind.Failure, $"Could not {words.Verb} {(item.Inputs == 1 ? $"the {words.Noun}" : words.Count(item.Inputs))}", Reason(item.Result.Error));

    /// <summary>What the operation did, as a short past-tense phrase ("Converted to WebP").</summary>
    public static string Done(Operation op) => op.Kind switch
    {
        OperationKind.Convert => $"Converted to {ImageFormats.DisplayName(op.Target)}",
        OperationKind.Compress => op.Compress == CompressLevel.Lossless ? "Compressed without loss" : $"Compressed ({op.Label.ToLowerInvariant()})",
        OperationKind.Resize => op.Resize!.Mode == ResizeMode.Percent ? $"Resized to {op.Resize.Percent}%" : $"Resized to fit {op.Label}",
        OperationKind.Rotate => op.RotateDegrees switch { 90 => "Rotated right", 270 => "Rotated left", _ => "Rotated 180°" },
        OperationKind.StripMetadata => "Metadata removed",
        _ => op.Label,
    };

    public static string Verb(Operation op) => op.Kind switch
    {
        OperationKind.Convert => "convert",
        OperationKind.Compress => "compress",
        OperationKind.Resize => "resize",
        OperationKind.Rotate => "rotate",
        OperationKind.StripMetadata => "remove metadata from",
        _ => "read",
    };

    /// <summary>What the operation is doing, for the progress toast ("Converting").</summary>
    public static string Working(Operation op) => op.Kind switch
    {
        OperationKind.Convert => "Converting",
        OperationKind.Compress => "Compressing",
        OperationKind.Resize => "Resizing",
        OperationKind.Rotate => "Rotating",
        OperationKind.StripMetadata => "Removing metadata from",
        _ => "Reading",
    };

    private static string NothingReason(Operation op) => op.Kind switch
    {
        OperationKind.Resize => "Already that size or smaller",
        OperationKind.Compress => "Compressing would not make it smaller",
        OperationKind.StripMetadata => "No metadata to remove",
        _ => "Nothing needed doing",
    };

    /// <summary>The reason for a failure, in the user's terms.</summary>
    public static string Reason(ErrorCode error) => error switch
    {
        ErrorCode.SourceNotFound => "The file is no longer there.",
        ErrorCode.SourceNotReadable => "The file could not be read. It may be open in another program or offline.",
        ErrorCode.InputTooLarge => "The file is larger than 256 MB.",
        ErrorCode.ImageTooLarge => "The image is larger than 50 megapixels.",
        ErrorCode.ContentMismatch => "The file's content does not match its name.",
        ErrorCode.UnsupportedContent => "This kind of image is not supported (for example animated, multi-page or 16-bit).",
        ErrorCode.UnsupportedOperation => "This command does not apply to this file.",
        ErrorCode.DecodeFailed => "The image is damaged or incomplete.",
        ErrorCode.EncodeFailed or ErrorCode.OutputValidationFailed => "The new image could not be written correctly. Nothing was saved.",
        ErrorCode.OutputDirectoryNotWritable => "The output folder cannot be written to.",
        ErrorCode.OutputDirectoryIsLink => "The output folder is a link; for safety nothing is written there.",
        ErrorCode.OutputNameInvalid or ErrorCode.NoFreeOutputName => "No free name for the new file in that folder.",
        ErrorCode.DiskFull => "The disk is full.",
        ErrorCode.WorkerTimeout => "It took too long and was stopped.",
        ErrorCode.WorkerCrashed or ErrorCode.WorkerProtocolError => "The worker stopped unexpectedly. The original is unchanged.",
        ErrorCode.Cancelled => "The job was cancelled.",
        ErrorCode.InvalidRequest => "The file's path is not supported.",
        ErrorCode.IsAFolder => "It is a folder; this command works on files.",
        ErrorCode.ClipboardUnavailable => "Another program is using the clipboard. Try again.",
        ErrorCode.OnlineOnly => "The file is stored online only. Open it once so Windows downloads it, then try again.",
        _ => "Something went wrong. The original is unchanged.",
    };

    private static string MostCommonReason(IEnumerable<JobItem> failed) =>
        Reason(failed.GroupBy(f => f.Result.Error).OrderByDescending(g => g.Count()).First().Key);

    private static string OutputName(JobItem item) => item.Result.OutputPath is { } p ? Path.GetFileName(p) : item.SourceName;

    private static string? Sizes(JobItem item) =>
        item is { SourceBytes: { } a, OutputBytes: { } b } ? $"{Size(a)} → {Size(b)}{Change(a, b)}" : null;

    private static string? TotalSizes(JobItem[] ok)
    {
        if (ok.Length == 0 || ok.Any(i => i.SourceBytes is null || i.OutputBytes is null)) return null;
        var a = ok.Sum(i => i.SourceBytes!.Value);
        var b = ok.Sum(i => i.OutputBytes!.Value);
        return $"{Size(a)} → {Size(b)}{Change(a, b)}";
    }

    private static string Change(long before, long after)
    {
        if (before <= 0) return "";
        var pct = (int)Math.Round((after - before) * 100.0 / before);
        return pct == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" ({(pct > 0 ? "+" : "−")}{Math.Abs(pct)}%)");
    }

    private static string Join(string a, string? b) => b is null ? a : $"{a} · {b}";

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.##} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };
}
