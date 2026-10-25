using System.Runtime.InteropServices;
using WinTools.Windows.Native;

namespace WinTools.Windows;

/// <summary>Unicode operations the product needs while it runs with invariant globalization (V2 PR 2.2).</summary>
public static class UnicodeText
{
    /// <summary>
    /// The text in Unicode normalization form KC, from Windows' <c>NormalizeString</c>: .NET's own does nothing to
    /// non-ASCII text under <c>InvariantGlobalization</c>. Null when Windows refuses the text (a lone surrogate).
    /// </summary>
    public static string? ToFormKC(string text) => Normalize(text, Normaliz.NormalizationKC);

    /// <summary>The text in normalization form C (canonical composition and mark order only), or null as above.</summary>
    public static string? ToFormC(string text) => Normalize(text, Normaliz.NormalizationC);

    private static string? Normalize(string text, int form)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return text;
        // The first call estimates the length; the estimate can be short, so grow until it fits (the documented loop).
        var size = Normaliz.NormalizeString(form, text, text.Length, null, 0);
        for (var attempt = 0; attempt < 10 && size > 0; attempt++)
        {
            var buffer = new char[size];
            var written = Normaliz.NormalizeString(form, text, text.Length, buffer, buffer.Length);
            if (written > 0) return new string(buffer, 0, written);
            if (Marshal.GetLastPInvokeError() != Normaliz.ERROR_INSUFFICIENT_BUFFER) return null;
            size = -written;
        }
        return null;
    }
}
