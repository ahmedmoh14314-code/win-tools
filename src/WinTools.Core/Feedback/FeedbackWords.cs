namespace WinTools.Core.Feedback;

/// <summary>
/// The words a module gives the toasts about one of its commands (V2 PR 1.4), so the presenter needs no module
/// knowledge: what it is doing ("Converting"), the verb for a failure ("convert"), what it did ("Converted to WebP"),
/// why nothing changed, and what it works on ("image"/"images").
/// </summary>
public sealed record FeedbackWords(string Working, string Verb, string Done, string NothingReason, string Noun, string Nouns)
{
    /// <summary>Plain words from a command's label, for a module that gives none.</summary>
    public static FeedbackWords From(string label) =>
        new("Working on", $"run \"{label}\" on", label, "Nothing needed doing", "file", "files");

    /// <summary>"1 image", "3 images".</summary>
    public string Count(int n) => n == 1 ? $"1 {Noun}" : $"{n} {Nouns}";
}
