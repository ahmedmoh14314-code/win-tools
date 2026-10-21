using WinTools.Windows.Native;

namespace WinTools.Windows.Rendering;

/// <summary>How a label is drawn in its box: the size used and one or two lines; <see cref="Ellipsis"/> when it still did not fit.</summary>
internal sealed record TextFit(int SizePx, string[] Lines, bool Ellipsis);

/// <summary>
/// The UI font (Segoe UI, ClearType) at the sizes the surfaces use, and text measurement. Fonts are created once per
/// pixel size and weight and kept for the life of the process: the set is small (a few sizes per monitor DPI).
/// Measurement uses a private memory DC. One thread at a time (the UI thread).
/// </summary>
internal static class Fonts
{
    public const uint DT_END_ELLIPSIS = 0x8000, DT_RIGHT = 0x2;
    private const string Face = "Segoe UI";

    private static readonly Dictionary<(int, int), nint> Cache = [];
    private static readonly Dictionary<(string, int, int, int, int, bool), TextFit> Fits = [];
    private static nint _measureDc;

    public static nint Get(int sizePx, int weight)
    {
        if (Cache.TryGetValue((sizePx, weight), out var f)) return f;
        f = Gdi32.CreateFontW(-sizePx, 0, 0, 0, weight, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, Face);
        Cache[(sizePx, weight)] = f;
        return f;
    }

    public static int Width(string text, int sizePx, int weight)
    {
        if (_measureDc == 0) _measureDc = Gdi32.CreateCompatibleDC(0);
        var old = Gdi32.SelectObject(_measureDc, Get(sizePx, weight));
        Ui.GetTextExtentPoint32W(_measureDc, text, text.Length, out var size);
        Gdi32.SelectObject(_measureDc, old);
        return size.Cx;
    }

    /// <summary>
    /// Fits <paramref name="text"/> into <paramref name="maxWidth"/>: on one line at the base size, else on two lines
    /// (split at a space or after "×") when <paramref name="twoLines"/>, else at a slightly smaller size, else on one
    /// line with an ellipsis. Results are cached.
    /// </summary>
    public static TextFit Fit(string text, int maxWidth, int baseSizePx, int weight, bool twoLines)
    {
        var key = (text, maxWidth, baseSizePx, weight, 0, twoLines);
        if (Fits.TryGetValue(key, out var fit)) return fit;
        fit = Compute(text, maxWidth, baseSizePx, weight, twoLines);
        Fits[key] = fit;
        return fit;
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
