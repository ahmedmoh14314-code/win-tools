using WinTools.Core.Capabilities;
using WinTools.Core.Localization;

namespace WinTools.Core.Feedback;

/// <summary>
/// A command's toast words in the language in use. English keeps each module's own words ("Converting", "convert",
/// "Converted to WebP"); Arabic builds them from the command's translated name ("جارٍ: إلى WebP", "تعذّر تنفيذ «إلى
/// WebP»"), so no English verb or noun reaches an Arabic toast. The module's nouns, the archive's password hint and Batch
/// rename's own reasons come from the tables too.
/// </summary>
public static class LocalWords
{
    public static FeedbackWords For(CapabilityDescriptor capability)
    {
        var words = capability.Words;
        if (!UiText.IsRightToLeft) return words;
        var label = UiText.TOr("cmd." + capability.Id, capability.Label);
        return words with
        {
            Working = UiText.F("words.working", label),
            Verb = UiText.F("words.verb", label),
            Done = UiText.F("words.done", label),
            NothingReason = UiText.T("words.nothing"),
            // The command's own noun where the module's does not fit it ("Add to ZIP" counts files, not archives).
            Noun = UiText.Get(UiText.Language, "noun." + capability.Id) ?? UiText.TOr("noun." + capability.Module, words.Noun),
            Nouns = UiText.Get(UiText.Language, "nouns." + capability.Id) ?? UiText.TOr("nouns." + capability.Module, words.Nouns),
            PasswordRequired = words.PasswordRequired is null ? null : UiText.T("toast.dropAlone"),
            SuccessTitle = words.SuccessTitle is null ? null : UiText.T("words.renamed"),
            Reasons = words.Reasons?.ToDictionary(r => r.Key, r => UiText.TOr($"reason.rename.{r.Key}", r.Value)),
        };
    }
}
