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
    private const double Width96 = 324, Margin96 = 14, Pad96 = 16, Header96 = 40, Row96 = 22, SectionGap96 = 12, Footer96 = 26;

    public static int Px(double v96, uint dpi) => (int)Math.Round(v96 * dpi / 96.0);

    private static double CardHeight96(IReadOnlyList<InfoSection> sections, int more) =>
        sections.Sum(s => Header96 + s.Rows.Count * Row96) + (sections.Count - 1) * SectionGap96 + Pad96 - 4 + (more > 0 ? Footer96 : 0) + 6;

    public static (int W, int H) WindowSize(IReadOnlyList<InfoSection> sections, int more, uint dpi) =>
        (Px(Width96 + 2 * Margin96, dpi), Px(CardHeight96(sections, more) + 2 * Margin96, dpi));

    public static void Draw(Painter p, int w, int h, IReadOnlyList<InfoSection> sections, int more)
    {
        var m = p.S(Margin96);
        var (x, y, cw, ch) = (m, m, w - 2 * m, h - 2 * m);
        var radius = p.S(12);
        p.Shadow(x, y, cw, ch, radius, p.S(10), 0.6);
        p.FillRoundRect(x, y, cw, ch, radius, Palette.Surface);
        p.StrokeRoundRect(x + 0.5f, y + 0.5f, cw - 1, ch - 1, radius, Palette.Edge, 1);

        var pad = p.S(Pad96);
        var left = x + pad;
        var right = x + cw - pad;
        var valueLeft = x + p.S(124);
        var cy = y + p.S(6);
        var titlePx = Px(13.5, p.Dpi);
        var textPx = Px(12, p.Dpi);
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
                if (row.Notice)
                {
                    p.FillCircle(valueLeft + p.S(3.5), (top + bottom) / 2, p.S(3.5), Palette.Warning);
                    p.Text(row.Value, valueLeft + p.S(13), top, right, bottom, Palette.Warning, textPx, 600, TextAlign.Left);
                }
                else p.Text(row.Value, valueLeft, top, right, bottom, Palette.Text, textPx, 400, TextAlign.Left);
                cy = bottom;
            }
        }
        if (more > 0)
        {
            cy += p.S(4);
            p.Text(more == 1 ? "+1 more file" : $"+{more} more files", left, cy, right, cy + p.S(Footer96 - 6), Palette.TextSecondary, textPx, 400, TextAlign.Left);
        }
    }
}
