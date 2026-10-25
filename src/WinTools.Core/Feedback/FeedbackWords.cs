namespace WinTools.Core.Feedback;

/// <summary>
/// The words a module gives the toasts about one of its commands (V2 PR 1.4), so the presenter needs no module
/// knowledge: what it is doing ("Converting"), the verb for a failure ("convert"), what it did ("Converted to WebP"),
/// why nothing changed, and what it works on ("image"/"images"). <paramref name="Savings"/>: the size saved is the point
/// of the command (Compress: "saved 62%", "already optimized"). <paramref name="PasswordRequired"/>: what to do about a
/// file that needs a password, when the module has its own way (an archive: drop it on its own, V2 PR 5.3).
/// </summary>
/// <remarks><c>SuccessTitle</c>: the title of a success that created nothing ("Files renamed"), instead of
/// "<i>name</i> created" (V2 PR 7.2).</remarks>
public sealed record FeedbackWords(string Working, string Verb, string Done, string NothingReason, string Noun, string Nouns, bool Savings = false,
    string? PasswordRequired = null, string? SuccessTitle = null)
{
    /// <summary>Plain words from a command's label, for a module that gives none.</summary>
    public static FeedbackWords From(string label) =>
        new("Working on", $"run \"{label}\" on", label, "Nothing needed doing", "file", "files");

    /// <summary>"1 image", "3 images".</summary>
    public string Count(int n) => n == 1 ? $"1 {Noun}" : $"{n} {Nouns}";
}
