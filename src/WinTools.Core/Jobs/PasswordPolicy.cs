using System.Text;

namespace WinTools.Core.Jobs;

/// <summary>A password as the policy accepted it (normalized), or why it was refused.</summary>
public readonly record struct PasswordCheck(string? Password, string? Problem)
{
    public bool Ok => Password is not null;
}

/// <summary>
/// What a password may be (V2 PR 2.2, ADR-0026); the dialog, the host and the worker each apply it, so no step trusts
/// the one before.
/// <list type="bullet">
/// <item>Not empty; no line break or NUL: qpdf reads its arguments one per line on stdin (<c>@-</c>), so a line break
/// would end the password and begin another argument. No other control or invisible formatting character either (a tab,
/// a bidi mark, a zero-width space): SASLprep maps or forbids them, and nobody can tell what they typed.</item>
/// <item>Normalized to NFKC by the caller's <c>normalizeKC</c>, as the standard's SASLprep asks of the writer, so other
/// readers derive the same key from what the user typed; qpdf does not normalize (its manual, "Unicode Passwords").
/// The product runs with invariant globalization, where .NET does not normalize, so the dialog and the host pass
/// Windows' own (<c>WinTools.Windows.UnicodeText.ToFormKC</c>); the worker re-checks the rest.</item>
/// <item>At most <see cref="MaxUtf8Bytes"/> bytes of UTF-8 after normalizing: AES-256 PDF passwords are cut there
/// without a word, so a longer one would be protected by its first 127 bytes only.</item>
/// </list>
/// </summary>
public static class PasswordPolicy
{
    public const int MaxUtf8Bytes = 127;

    /// <summary>The parameter a job carries its password in; never logged.</summary>
    public const string ParameterName = "password";

    public static PasswordCheck Check(string? password, Func<string, string?>? normalizeKC = null)
    {
        if (string.IsNullOrEmpty(password)) return new(null, "Enter a password.");
        if (Problem(password) is { } before) return new(null, before);
        var normalized = normalizeKC is null ? password : normalizeKC(password);
        if (string.IsNullOrEmpty(normalized)) return new(null, "The password contains characters that cannot be used.");
        if (Problem(normalized) is { } after) return new(null, after);
        if (Encoding.UTF8.GetByteCount(normalized) > MaxUtf8Bytes) return new(null, "The password is too long (at most 127 bytes: fewer characters in some scripts).");
        return new(normalized, null);
    }

    private static string? Problem(string text)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0) return "A password cannot contain a line break.";
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogate(text[i]))
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
                return "The password contains characters that cannot be used."; // a lone half of a pair
            }
            if (char.IsControl(text[i]) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(text[i]) == System.Globalization.UnicodeCategory.Format)
                return "A password cannot contain invisible characters (tabs, direction marks, zero-width spaces).";
        }
        return null;
    }
}
