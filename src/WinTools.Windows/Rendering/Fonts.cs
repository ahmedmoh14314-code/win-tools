using WinTools.Windows.Native;

namespace WinTools.Windows.Rendering;

/// <summary>How a label is drawn in its box: the size used and one or two lines; <see cref="Ellipsis"/> when it still did not fit.</summary>
internal sealed record TextFit(int SizePx, string[] Lines, bool Ellipsis);

/// <summary>
/// The UI font (Segoe UI, ClearType) at the sizes the surfaces use, and text measurement. Fonts are created once per
/// pixel size and weight and kept for the life of the process: the set is small (a few sizes per monitor DPI).
/// Measurement uses a private memory DC, under one lock: the overlay and the toasts draw on different threads.
/// </summary>
internal static class Fonts
{
    public const uint DT_END_ELLIPSIS = 0x8000, DT_RIGHT = 0x2, DT_RTLREADING = 0x20000;
    private const string Face = "Segoe UI";

    private static readonly Dictionary<(int, int), nint> Cache = [];
    private static readonly Dictionary<(string, int, int, int, int, bool), TextFit> Fits = [];
    private static nint _measureDc;

    /// <summary>
    /// One thread at a time: the overlay and the toasts draw on different threads, and one measuring DC with a font
    /// selected into it (and the caches) shared between them gives wrong widths (seen as a flaky fit test, PR #37).
    /// </summary>
    private static readonly Lock Gate = new();

    public static nint Get(int sizePx, int weight)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue((sizePx, weight), out var f)) return f;
            f = Gdi32.CreateFontW(-sizePx, 0, 0, 0, weight, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, Face);
            Cache[(sizePx, weight)] = f;
            return f;
        }
    }

    public static int Width(string text, int sizePx, int weight)
    {
        lock (Gate)
        {
            if (_measureDc == 0) _measureDc = Gdi32.CreateCompatibleDC(0);
            var old = Gdi32.SelectObject(_measureDc, Get(sizePx, weight));
            int width;
            var (drawn, flags) = Prepare(text);
            if (flags == 0)
            {
                Ui.GetTextExtentPoint32W(_measureDc, text, text.Length, out var size);
                width = size.Cx;
            }
            else
            {
                // Right-to-left text is measured exactly as it is drawn: the same string and reading order.
                var rect = new RECT();
                User32.DrawTextW(_measureDc, drawn, -1, ref rect, User32.DT_SINGLELINE | User32.DT_NOPREFIX | DT_CALCRECT | flags);
                width = rect.Right - rect.Left;
            }
            Gdi32.SelectObject(_measureDc, old);
            return width;
        }
    }

    private const uint DT_CALCRECT = 0x400;

    /// <summary>
    /// A text as GDI draws it, and the reading-order flag: in a right-to-left language, a text holding a right-to-left
    /// letter reads right to left, with Latin digits (U+206F NOMINAL DIGIT SHAPES: the values beside it are written with
    /// them, and Windows' contextual digits would mix both shapes in one line); any other text (a file name, "4032 × 3024
    /// px") keeps its own left-to-right order. Unchanged in a left-to-right language.
    /// </summary>
    public static (string Text, uint Flags) Prepare(string text) =>
        WinTools.Core.Localization.UiText.IsRightToLeft && HasRightToLeftLetter(text) ? ("\u206F" + text, DT_RTLREADING) : (text, 0u);

    /// <summary>Whether a text holds a Hebrew or Arabic letter (U+0590 to U+08FF and their presentation forms).</summary>
    private static bool HasRightToLeftLetter(string text)
    {
        foreach (var c in text)
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF') return true;
        return false;
    }

    /// <summary>
    /// Fits <paramref name="text"/> into <paramref name="maxWidth"/>: on one line at the base size, else on two lines
    /// (split at a space or after "×") when <paramref name="twoLines"/>, else at a slightly smaller size, else on one
    /// line with an ellipsis. Results are cached.
    /// </summary>
    public static TextFit Fit(string text, int maxWidth, int baseSizePx, int weight, bool twoLines)
    {
        var key = (text, maxWidth, baseSizePx, weight, 0, twoLines);
        lock (Gate)
        {
            if (Fits.TryGetValue(key, out var fit)) return fit;
            fit = Compute(text, maxWidth, baseSizePx, weight, twoLines);
            Fits[key] = fit;
            return fit;
        }
    }

    private static TextFit Compute(string text, int maxWidth, int baseSizePx, int weight, bool twoLines)
    {
        foreach (var scale in new[] { 1.0, 0.92, 0.85 })
        {
            var size = Math.Max(1, (int)Math.Round(baseSizePx * scale));
            if (Width(text, size, weight) <= maxWidth) return new(size, [text], false);
            if (!twoLines) continue;
            string[]? best = null;
            var bestWidth = int.MaxValue;
            for (var i = 1; i < text.Length; i++)
            {
                string a, b;
                if (text[i] == ' ') { a = text[..i]; b = text[(i + 1)..]; }
                else if (text[i - 1] == '×') { a = text[..i]; b = text[i..]; }
                else continue;
                var w = Math.Max(Width(a, size, weight), Width(b, size, weight));
                if (w <= maxWidth && w < bestWidth) { best = [a, b]; bestWidth = w; }
            }
            if (best is not null) return new(size, best, false);
        }
        return new(Math.Max(1, (int)Math.Round(baseSizePx * 0.85)), [text], true);
    }
}
