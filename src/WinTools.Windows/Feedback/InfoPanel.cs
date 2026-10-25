using System.Runtime.InteropServices;
using WinTools.Core.Feedback;
using WinTools.Windows.Native;
using WinTools.Windows.Rendering;

namespace WinTools.Windows.Feedback;

/// <summary>One file's facts for the info panel.</summary>
public sealed record InfoSection(string FileName, IReadOnlyList<InfoRow> Rows);

/// <summary>
/// The "Image info" panel: a compact card next to where the user dropped, listing the facts of up to
/// <see cref="InfoPanelRenderer.MaxSections"/> files. A non-activating topmost layered window: it never takes focus. A
/// click anywhere on it closes it (it has no other control), as do WM_CLOSE, the next radial menu and a timeout.
/// Use on the UI thread.
/// </summary>
public sealed class InfoPanel : IDisposable
{
    public const string ClassName = "WinTools.InfoPanel";
    private const nuint DismissTimer = 1;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly LayeredWindow _window;
    private readonly Surface _surface = new();

    public InfoPanel() => _window = new LayeredWindow(ClassName, clickThrough: false, OnMessage);

    public bool IsVisible => _window.IsVisible;

    /// <summary>Shows the panel beside (<paramref name="screenX"/>, <paramref name="screenY"/>), inside that monitor's work area.</summary>
    public void Show(IReadOnlyList<InfoSection> sections, int moreFiles, int screenX, int screenY)
    {
        var monitor = User32.MonitorFromPoint(new POINT { X = screenX, Y = screenY }, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        User32.GetMonitorInfoW(monitor, ref info);
        var dpi = Shcore.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96;
        var work = info.rcWork;
        // As many files as fit the work area's height; the rest are counted in "+N more".
        while (sections.Count > 1 && InfoPanelRenderer.WindowSize(sections, moreFiles, dpi).H > work.Bottom - work.Top)
        {
            sections = sections.Take(sections.Count - 1).ToArray();
            moreFiles++;
        }
        var (w, h) = InfoPanelRenderer.WindowSize(sections, moreFiles, dpi);
        var gap = InfoPanelRenderer.Px(28, dpi);
        var x = screenX + gap + w <= work.Right ? screenX + gap : screenX - gap - w; // beside the drop, never under the pointer
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        var y = Math.Clamp(screenY - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
        using (var p = _surface.Begin(w, h, dpi)) InfoPanelRenderer.Draw(p, w, h, sections, moreFiles);
        _window.Present(_surface, x, y);
        _window.Show();
        _window.StartTimer(DismissTimer, (uint)Timeout.TotalMilliseconds);
    }

    public void Hide()
    {
        _window.StopTimer(DismissTimer);
        _window.Hide();
    }

    private nint? OnMessage(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Ui.WM_LBUTTONUP or Ui.WM_RBUTTONUP or Ui.WM_MBUTTONUP: Hide(); return 0;
            case Msg.CLOSE: Hide(); return 0; // never destroyed by WM_CLOSE: the window is reused
            case Msg.TIMER when wParam == DismissTimer: Hide(); return 0;
            default: return null;
        }
    }

    public void Dispose()
    {
        _window.Dispose();
        _surface.Dispose();
    }
}

/// <summary>Layout and painting of the info panel (window-independent, for screenshots and checks).</summary>
internal static class InfoPanelRenderer
{
    public const int MaxSections = 3;
    private const double Width96 = 324, Margin96 = 14, Pad96 = 16, Header96 = 40, Row96 = 22, Line96 = 16, SectionGap96 = 12, Footer96 = 26;
    private const double ValueLeft96 = 124, NoticeIndent96 = 13;

    /// <summary>A value wraps onto at most this many lines; beyond, the last line ends in an ellipsis.</summary>
    public const int MaxValueLines = 3;

    public static int Px(double v96, uint dpi) => (int)Math.Round(v96 * dpi / 96.0);

    public static int TextPx(uint dpi) => Px(12, dpi);

    /// <summary>Width of the value column, in pixels (a notice is indented by its dot).</summary>
    public static int ValueWidth(bool notice, uint dpi) => Px(Width96 - ValueLeft96 - Pad96 - (notice ? NoticeIndent96 : 0), dpi);

    /// <summary>
    /// A value in lines that fit the column (evaluator, PR #18: a SHA-256 or a mismatch notice was cut to an ellipsis).
    /// Breaks after a space or comma where it can, inside a word (a hash) where it must.
    /// </summary>
    public static IReadOnlyList<string> ValueLines(string value, bool notice, uint dpi)
    {
        var (width, px, weight) = (ValueWidth(notice, dpi), TextPx(dpi), notice ? 600 : 400);
        var lines = new List<string>();
        var rest = value;
        while (rest.Length > 0 && lines.Count < MaxValueLines)
        {
            if (lines.Count == MaxValueLines - 1 || Fonts.Width(rest, px, weight) <= width) { lines.Add(rest); break; } // the last line ellipsises
            var n = rest.Length - 1;
            while (n > 1 && Fonts.Width(rest[..n], px, weight) > width) n--;
            var space = rest.LastIndexOfAny([' ', ','], n - 1, n);
            if (space > 0) n = space + 1;
            lines.Add(rest[..n].TrimEnd());
            rest = rest[n..].TrimStart();
        }
        return lines;
    }

    private static double RowHeight96(InfoRow row, uint dpi) => Row96 + (ValueLines(row.Value, row.Notice, dpi).Count - 1) * Line96;

    private static double CardHeight96(IReadOnlyList<InfoSection> sections, int more, uint dpi) =>
        sections.Sum(s => Header96 + s.Rows.Sum(r => RowHeight96(r, dpi))) + (sections.Count - 1) * SectionGap96 + Pad96 - 4 + (more > 0 ? Footer96 : 0) + 6;

    public static (int W, int H) WindowSize(IReadOnlyList<InfoSection> sections, int more, uint dpi) =>
        (Px(Width96 + 2 * Margin96, dpi), Px(CardHeight96(sections, more, dpi) + 2 * Margin96, dpi));

    public static void Draw(Painter p, int w, int h, IReadOnlyList<InfoSection> sections, int more)
    {
        if (WinTools.Core.Localization.UiText.IsRightToLeft) p.Mirror(w);
        var m = p.S(Margin96);
        var (x, y, cw, ch) = (m, m, w - 2 * m, h - 2 * m);
        var radius = p.S(12);
        p.Shadow(x, y, cw, ch, radius, p.S(10), 0.6);
        p.FillRoundRect(x, y, cw, ch, radius, Palette.Surface);
        p.StrokeRoundRect(x + 0.5f, y + 0.5f, cw - 1, ch - 1, radius, Palette.Edge, 1);

        var pad = p.S(Pad96);
        var left = x + pad;
        var right = x + cw - pad;
        var valueLeft = x + p.S(ValueLeft96);
        var cy = y + p.S(6);
        var titlePx = Px(13.5, p.Dpi);
        var textPx = TextPx(p.Dpi);
        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];
            if (s > 0)
            {
                cy += p.S(SectionGap96 / 2);
                p.Line(left, cy, right, cy, Palette.Edge, 1);
                cy += p.S(SectionGap96 / 2);
            }
            // Header: the info glyph and the file name (with an ellipsis when long).
            var hy = cy + p.S(Header96 / 2);
            Icons.Draw(p, Glyph.Info, left + p.S(8), hy, p.S(16), Palette.Green);
            p.Text(section.FileName, left + p.S(22), hy - p.S(11), right - (s == 0 ? p.S(18) : 0), hy + p.S(11), Palette.Text, titlePx, 600, TextAlign.Left);
            if (s == 0)
            {
                // Close mark (the whole panel closes on a click).
                var cx = right - p.S(5);
                p.Line(cx - p.S(4), hy - p.S(4), cx + p.S(4), hy + p.S(4), Palette.TextSecondary, p.S(1.5));
                p.Line(cx + p.S(4), hy - p.S(4), cx - p.S(4), hy + p.S(4), Palette.TextSecondary, p.S(1.5));
            }
            cy += p.S(Header96);
            foreach (var row in section.Rows)
            {
                var top = cy;
                var bottom = cy + p.S(Row96);
                p.Text(row.Label, left, top, valueLeft - p.S(8), bottom, Palette.TextSecondary, textPx, 400, TextAlign.Left);
                var lines = ValueLines(row.Value, row.Notice, p.Dpi);
                if (row.Notice) p.FillCircle(valueLeft + p.S(3.5), (top + bottom) / 2, p.S(3.5), Palette.Warning);
                var textLeft = row.Notice ? valueLeft + p.S(NoticeIndent96) : valueLeft;
                for (var i = 0; i < lines.Count; i++)
                {
                    var lineTop = top + p.S(i * Line96);
                    p.Text(lines[i], textLeft, lineTop, right, lineTop + p.S(Row96), row.Notice ? Palette.Warning : Palette.Text, textPx, row.Notice ? 600 : 400, TextAlign.Left);
                }
                cy = bottom + p.S((lines.Count - 1) * Line96);
            }
        }
        if (more > 0)
        {
            cy += p.S(4);
            p.Text(WinTools.Core.Localization.UiText.Count("info.more", more), left, cy, right, cy + p.S(Footer96 - 6), Palette.TextSecondary, textPx, 400, TextAlign.Left);
        }
    }
}
