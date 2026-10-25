using System.Globalization;
using System.Text;

namespace WinTools.Core.Jobs;

/// <summary>A new password as the policy accepted it (exactly as typed), or why it was refused.</summary>
public readonly record struct PasswordCheck(string? Password, string? Problem)
{
    public bool Ok => Password is not null;
}

/// <summary>
/// An existing password as typed, with its NFKC form when that differs (tried second), or why it was refused.
/// </summary>
public readonly record struct ExistingPasswordCheck(string? Password, string? Normalized, string? Problem)
{
    public bool Ok => Password is not null;
}

/// <summary>
/// What a password may be (V2 PR 2.2, ADR-0026). The dialog, the host and the worker each apply it, so no step trusts
/// the one before.
/// <list type="bullet">
/// <item>Always: not empty, and no control character. qpdf reads its arguments one per line on stdin (<c>@-</c>), so a
/// line break would begin another argument, and 0x1A ends the input early.</item>
/// <item><b>A new password</b> (<see cref="CheckNew"/>, Protect) is used exactly as typed, so every reader derives the
/// same key whether it normalizes or not (qpdf does not; the standard's SASLprep does, for AES-256):
///   <list type="bullet">
///   <item>it must equal its own NFKC form, and a password NFKC would change ("x²") is refused (evaluator, PR 2.2);</item>
///   <item>SASLprep's prohibited characters are refused: invisible formatting (in and outside the BMP), line and
///   paragraph separators, private use, unassigned and non-characters;</item>
///   <item>so are the characters it maps to nothing (variation selectors, the combining grapheme joiner);</item>
///   <item>at most <see cref="MaxUtf8Bytes"/> bytes of UTF-8, where AES-256 PDF passwords are cut without a word.</item>
///   </list></item>
/// <item><b>An existing password</b> (<see cref="CheckExisting"/>, Remove protection) was chosen elsewhere, so only the
/// channel's rules apply and no length rule (readers cut it themselves). It is tried as typed, then in its NFKC form,
/// because a file may have been protected either way.</item>
/// </list>
/// The product runs with invariant globalization, where .NET does not normalize, so the dialog and the host pass Windows'
/// NFKC (<c>WinTools.Windows.UnicodeText.ToFormKC</c>); the worker, which cannot, re-checks the rest.
/// </summary>
public static class PasswordPolicy
{
    public const int MaxUtf8Bytes = 127;

    /// <summary>The most characters an existing password may have: far beyond what any reader uses.</summary>
    public const int MaxExistingChars = 1024;

    /// <summary>The parameter a job carries its password in, as typed; never logged.</summary>
    public const string ParameterName = "password";

    /// <summary>Remove protection only: the password's NFKC form, when it differs, tried after the typed one.</summary>
    public const string NormalizedParameterName = "password-nfkc";

    /// <param name="normalizeC">NFC, to tell a canonical difference (marks in another order, a decomposed letter) from a
    /// compatibility one ("²") in the message; the verdict is the same.</param>
    public static PasswordCheck CheckNew(string? typed, Func<string, string?>? normalizeKC = null, Func<string, string?>? normalizeC = null)
    {
        if (string.IsNullOrEmpty(typed)) return new(null, "Enter a password.");
        if ((ChannelProblem(typed) ?? ForbiddenProblem(typed)) is { } problem) return new(null, problem);
        if (normalizeKC is not null)
        {
            var normalized = normalizeKC(typed);
            if (normalized is null) return new(null, "The password contains characters that cannot be used.");
            if (!string.Equals(normalized, typed, StringComparison.Ordinal))
                return new(null, normalizeC?.Invoke(typed) is { } c && !string.Equals(c, typed, StringComparison.Ordinal)
                    ? "Some marks are in an order (or a form) that PDF readers change, so some would not accept the password. Retype them in the other order (for example the vowel mark before the shadda), or type the letter as one character."
                    : "Some characters would change under Unicode normalization (for example ² becomes 2, or a non-breaking space becomes a space), so some PDF readers would not accept the password. Type them as plain characters.");
        }
        if (Encoding.UTF8.GetByteCount(typed) > MaxUtf8Bytes) return new(null, "The password is too long (at most 127 bytes: fewer characters in some scripts).");
        return new(typed, null);
    }

    public static ExistingPasswordCheck CheckExisting(string? typed, Func<string, string?>? normalizeKC = null)
    {
        if (string.IsNullOrEmpty(typed)) return new(null, null, "Enter the password.");
        if (typed.Length > MaxExistingChars) return new(null, null, "The password is too long.");
        if (ChannelProblem(typed) is { } problem) return new(null, null, problem);
        var normalized = normalizeKC?.Invoke(typed);
        var variant = normalized is { Length: > 0 } && !string.Equals(normalized, typed, StringComparison.Ordinal) && ChannelProblem(normalized) is null
            ? normalized : null;
        return new(typed, variant, null);
    }

    /// <summary>What the channel to qpdf cannot carry: control characters (a line break, NUL, 0x1A) and halves of pairs.</summary>
    private static string? ChannelProblem(string text)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0) return "A password cannot contain a line break.";
        if (text.Any(char.IsControl)) return "A password cannot contain control characters (such as a tab).";
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            return "The password contains characters that cannot be used."; // a lone half of a pair
        }
        return null;
    }

    /// <summary>
    /// What SASLprep forbids (RFC 4013 §2.3) or maps to nothing (RFC 3454 table B.1), by scalar value, so characters
    /// outside the BMP are seen too (evaluator, PR 2.2).
    /// </summary>
    private static string? ForbiddenProblem(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            var category = Rune.GetUnicodeCategory(rune);
            if (v == 0x200D) return "Emoji joined from several (such as a family or a profession) cannot be used; use single emoji.";
            if (category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return "A password cannot contain invisible characters (direction marks, zero-width spaces, separators).";
            if (category is UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned || (v & 0xFFFE) == 0xFFFE || v is >= 0xFDD0 and <= 0xFDEF)
                return "A password cannot contain unassigned or private-use characters.";
            if (v is 0x034F or 0x1806 or (>= 0x180B and <= 0x180D) or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF)
                or 0x0340 or 0x0341 or (>= 0x2FF0 and <= 0x2FFB) or (>= 0xFFF9 and <= 0xFFFD))
                return "A password cannot contain variation selectors or other characters that readers drop or refuse (such as the one after an emoji like ❤).";
        }
        return null;
    }
}
