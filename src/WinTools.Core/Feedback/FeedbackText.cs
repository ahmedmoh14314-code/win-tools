using System.Globalization;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;

namespace WinTools.Core.Feedback;

public enum FeedbackKind { Success, Partial, Failure, Nothing }

/// <summary>What a toast says: a headline and one line of detail.</summary>
public sealed record FeedbackMessage(FeedbackKind Kind, string Title, string Detail);

/// <summary>One request of a finished job: what it was about (a file name), the worker's result and the sizes before
/// and after (bytes).</summary>
public sealed record JobItem(string SourceName, JobResult Result, long? SourceBytes = null, long? OutputBytes = null, int Inputs = 1);

/// <summary>
/// The words shown to the user when a batch ends. Short, plain and actionable: the outcome first, the reason in the
/// user's terms (never an error code, a stack trace or a full path). The detailed result stays in the local log. Every
/// sentence comes from <see cref="UiText"/>; the English tables hold today's English exactly.
/// </summary>
public static class FeedbackText
{
    public static FeedbackMessage For(Operation operation, IReadOnlyList<JobItem> items) => For(WordsOf(operation), items);

    /// <summary>The image module's words for an operation.</summary>
    public static FeedbackWords WordsOf(Operation op) =>
        new(Working(op), Verb(op), Done(op), NothingReason(op), "image", "images", Savings: op.Kind == OperationKind.Compress);

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
                // A believed success may carry a notice the user must see (entries skipped or renamed in an archive).
                return new(FeedbackKind.Success, words.SuccessTitle ?? UiText.F("toast.created", OutputName(item)), Join(Join(words.Done, Sizes(words, item)), Notices(item)));
            if (item.Result.Error == ErrorCode.NothingToDo)
                return new(FeedbackKind.Nothing, UiText.T("toast.nothing"), words.NothingReason);
            return new(FeedbackKind.Failure, UiText.F("toast.couldNot", words.Verb, item.SourceName), Reason(item.Result.Error, words));
        }

        var n = items.Count;
        if (failed.Length == 0 && unchanged.Length == 0)
            return new(FeedbackKind.Success, UiText.F("toast.completed", Images(n)), Join(words.Done, TotalSizes(words, ok)));
        if (ok.Length == 0 && failed.Length == 0)
            return new(FeedbackKind.Nothing, UiText.T("toast.nothing"), $"{words.NothingReason} ({Images(n)})");
        if (ok.Length == 0 && unchanged.Length == 0)
            return new(FeedbackKind.Failure, UiText.F("toast.couldNot", words.Verb, Images(n)), MostCommonReason(failed, words));

        var parts = new List<string>();
        if (words.Savings && TotalSizes(words, ok) is { } sizes) parts.Add(sizes); // what was saved is the point
        if (failed.Length > 0) parts.Add(UiText.F("toast.failed", failed.Length, MostCommonReason(failed, words).TrimEnd('.')));
        if (unchanged.Length > 0) parts.Add(UiText.F(words.Savings ? "toast.alreadyOptimized" : "toast.unchanged", unchanged.Length));
        var kind = failed.Length > 0 ? FeedbackKind.Partial : FeedbackKind.Success;
        return new(kind, UiText.F("toast.someCompleted", Images(ok.Length), n), string.Join(" · ", parts));
    }

    /// <summary>
    /// What a command that fills the clipboard says (Copy path): what it copied, for how many items, or why it could not.
    /// </summary>
    public static FeedbackMessage Copied(FeedbackWords words, JobItem item) => item.Result.Success
        ? new(FeedbackKind.Success, item.Inputs == 1 ? words.Done : UiText.F("toast.copied", words.Count(item.Inputs)), item.SourceName)
        : new(FeedbackKind.Failure, item.Inputs == 1 ? UiText.F("toast.couldNotThe", words.Verb, words.Noun) : UiText.F("toast.couldNot", words.Verb, words.Count(item.Inputs)), Reason(item.Result.Error, words));

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

    private static string NothingReason(Operation op) => op.Kind switch
    {
        OperationKind.Resize => "Already that size or smaller",
        OperationKind.Compress => "Already optimized: a smaller copy would not save enough or would look different",
        OperationKind.StripMetadata => "No metadata to remove",
        _ => "Nothing needed doing",
    };

    /// <summary>
    /// The reason for a failure in the module's own terms (V2 PR 2.1): the image module's words are V1's; another module's
    /// noun ("PDF") replaces "image" where the reason names what was worked on, and its limits are not the image limits.
    /// </summary>
    public static string Reason(ErrorCode error, FeedbackWords words) =>
        words.Reasons is { } own && own.TryGetValue(error, out var reason) ? reason : ModuleReason(error, words);

    private static string ModuleReason(ErrorCode error, FeedbackWords words) => words.Noun is "image" or "صورة" ? Reason(error) : error switch
    {
        ErrorCode.InputTooLarge => UiText.F("reasonm.InputTooLarge", words.Noun),
        ErrorCode.ContentMismatch => UiText.F("reasonm.ContentMismatch", words.Noun),
        ErrorCode.UnsupportedContent => UiText.F("reasonm.UnsupportedContent", words.Noun),
        ErrorCode.DecodeFailed => UiText.F("reasonm.DecodeFailed", words.Noun),
        ErrorCode.EncodeFailed or ErrorCode.OutputValidationFailed => UiText.T("reasonm.EncodeFailed"),
        ErrorCode.OutputNameInvalid or ErrorCode.NoFreeOutputName => UiText.T("reasonm.NoFreeOutputName"),
        ErrorCode.PasswordRequired => words.PasswordRequired ?? UiText.F("reasonm.PasswordRequired", words.Noun),
        ErrorCode.PasswordIncorrect => UiText.F("reasonm.PasswordIncorrect", words.Noun),
        ErrorCode.LimitExceeded => UiText.F("reasonm.LimitExceeded", words.Noun),
        _ => Reason(error),
    };

    /// <summary>The reason for a failure, in the user's terms.</summary>
    public static string Reason(ErrorCode error) => UiText.T("reason." + error switch
    {
        ErrorCode.SourceNotFound or ErrorCode.SourceNotReadable or ErrorCode.InputTooLarge or ErrorCode.ImageTooLarge or ErrorCode.ContentMismatch
            or ErrorCode.UnsupportedContent or ErrorCode.UnsupportedOperation or ErrorCode.DecodeFailed or ErrorCode.EncodeFailed
            or ErrorCode.OutputDirectoryNotWritable or ErrorCode.OutputDirectoryIsLink or ErrorCode.NoFreeOutputName or ErrorCode.DiskFull
            or ErrorCode.WorkerTimeout or ErrorCode.WorkerCrashed or ErrorCode.Cancelled or ErrorCode.InvalidRequest or ErrorCode.IsAFolder
            or ErrorCode.ClipboardUnavailable or ErrorCode.OnlineOnly => error.ToString(),
        ErrorCode.OutputValidationFailed => nameof(ErrorCode.EncodeFailed),
        ErrorCode.OutputNameInvalid => nameof(ErrorCode.NoFreeOutputName),
        ErrorCode.WorkerProtocolError => nameof(ErrorCode.WorkerCrashed),
        _ => "Other",
    });

    private static string MostCommonReason(IEnumerable<JobItem> failed, FeedbackWords words) =>
        Reason(failed.GroupBy(f => f.Result.Error).OrderByDescending(g => g.Count()).First().Key, words);

    private static string OutputName(JobItem item) => item.Result.OutputPath is { } p ? Path.GetFileName(p) : item.SourceName;

    private static string? Sizes(FeedbackWords words, JobItem item) =>
        item is { SourceBytes: { } a, OutputBytes: { } b } ? $"{Size(a)} → {Size(b)}{Change(words, a, b)}" : null;

    private static string? TotalSizes(FeedbackWords words, JobItem[] ok)
    {
        if (ok.Length == 0 || ok.Any(i => i.SourceBytes is null || i.OutputBytes is null)) return null;
        var a = ok.Sum(i => i.SourceBytes!.Value);
        var b = ok.Sum(i => i.OutputBytes!.Value);
        return $"{Size(a)} → {Size(b)}{Change(words, a, b)}";
    }

    /// <summary>The size change: "saved 75%" for Compress (what the user asked for), "(+12%)" or "(−40%)" otherwise.</summary>
    private static string Change(FeedbackWords words, long before, long after)
    {
        if (before <= 0) return "";
        var pct = (int)Math.Round((after - before) * 100.0 / before);
        if (words.Savings && pct < 0) return UiText.F("toast.saved", -pct);
        return pct == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" ({(pct > 0 ? "+" : "−")}{Math.Abs(pct)}%)");
    }

    private static string Join(string a, string? b) => b is null ? a : $"{a} · {b}";

    /// <summary>Every notice a believed success carries (skipped and renamed entries), or null.</summary>
    private static string? Notices(JobItem item) =>
        item.Result.Facts.Where(f => f.Notice).Select(f => f.Value).ToArray() is { Length: > 0 } n ? string.Join(" · ", n) : null;

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => UiText.F("size.bytes", bytes),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.##} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };
}
