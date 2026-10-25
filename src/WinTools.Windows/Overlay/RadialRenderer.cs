using WinTools.Core.Interaction;
using WinTools.Windows.Rendering;

namespace WinTools.Windows.Overlay;

/// <summary>
/// Paints one frame of the radial menu: an opaque base disc filling the whole window (every pixel of the disc is a drop
/// target, ADR-0001), the sectors with gaps between them, the hub, icons and labels. It reads a <see cref="MenuView"/>
/// and a <see cref="RingAnimation"/> and knows nothing about operations. Window-independent, so the same frames can be
/// rendered off screen for checks and screenshots.
/// <list type="bullet">
/// <item>Hovered: a raised surface and an accent lip along the outer edge (shape, not only colour).</item>
/// <item>Armed (a drop runs this command now): the whole sector filled in the armed green and grown out towards the
/// rim, with dark text. The hub stays neutral: a check there could suggest dropping in the centre, which runs nothing.</item>
/// <item>Back: a darker sector with an arrow and secondary text, never green.</item>
/// </list>
/// </summary>
internal sealed class RadialRenderer
{
    private readonly Dictionary<(int, double, double, uint), LabelBox[]> _layouts = [];

    /// <summary>
    /// The ring between the trigger and the first DragEnter, before the files are known: an empty band and the mark,
    /// never "No actions" (which is only shown for files that really offer nothing).
    /// </summary>
    public static readonly MenuView Waiting = new([], 0, -1, false, "");

    /// <summary>Text weight of the labels (semibold).</summary>
    public const int LabelWeight = 600;

    public static int WindowSize(uint dpi) => RingGeometry.Scale(RingGeometry.OuterRadius96 * 2, dpi);

    public LabelBox[] Layout(int count, double start, double firstSweep, uint dpi, bool withIcons)
    {
        var key = (count, start, firstSweep, dpi);
        if (_layouts.TryGetValue(key, out var boxes)) return boxes;
        var m = RingLabelMetrics.For(count, dpi, withIcons);
        boxes = RingLabels.Layout(count, start, firstSweep, m.Radius, m.HalfHeight, m.MaxHalfWidth, m.Inner, m.Outer, m.Gap);
        _layouts[key] = boxes;
        return boxes;
    }

    public void Draw(Painter p, int size, MenuView view, RingAnimation anim)
    {
        var dpi = p.Dpi;
        var c = size / 2f;
        var appear = anim.Appear;
        var k = (float)(0.92 + 0.08 * appear);       // the ring grows into place
        var fade = appear;                            // and its content fades in from readable to full

        p.FillCircle(c, c, c, Palette.Base);
        p.StrokeCircle(c, c, c - p.S(0.75), Palette.Edge, p.S(1.5));

        var inner = p.S(RingLabelMetrics.BandInner96) * k;
        var outer = p.S(RingLabelMetrics.BandOuter96) * k;
        var gap = p.S(3);
        var n = view.Labels.Count;
        var isLevel = view.Open is not null;

        if (ReferenceEquals(view, Waiting))
        {
            p.FillAnnularSector(c, c, inner, outer, 0, 2 * Math.PI, 0, Palette.Surface, Argb.Lerp(Palette.Surface, Argb.Rgb(0x000000), 0.10));
            Hub(p, c, k, new MenuView(["…"], 0, -1, false, ""), fade);
            return;
        }
        if (n == 0)
        {
            p.FillAnnularSector(c, c, inner, outer, 0, 2 * Math.PI, 0, Palette.Surface, Palette.Surface);
            Hub(p, c, k, view, fade);
            var y = c - p.S(68) * k;
            p.Text(view.EmptyText ?? "No actions", c - p.S(70), y - p.S(9), c + p.S(70), y + p.S(9), Argb.Lerp(Palette.Surface, Palette.TextSecondary, fade), (int)Math.Round(p.S(RingLabelMetrics.LabelText96)), LabelWeight);
            return;
        }

        var boxes = Layout(n, view.Start, view.FirstSweep, dpi, withIcons: !isLevel);
        for (var i = 0; i < n; i++)
        {
            var (a0, a1) = RingGeometry.SectorBounds(i, n, view.Start, view.FirstSweep);
            var back = isLevel && i == 0;
            var hover = anim.Hover(i);
            var armed = anim.Hot == i && !back ? anim.Arm : 0;
            var fill = back ? Argb.Lerp(Palette.Sunken, Palette.Surface, hover) : Argb.Lerp(Palette.Surface, Palette.Raised, hover);
            fill = Argb.Lerp(fill, Palette.GreenArmed, armed);
            // Armed, the sector also grows out towards the rim: a change of shape, not only of colour.
            var reach = outer + (float)(armed * p.S(4.5));
            p.FillAnnularSector(c, c, inner, reach, a0, a1, gap, Argb.Lerp(fill, Argb.Rgb(0xFFFFFF), 0.035), Argb.Lerp(fill, Argb.Rgb(0x000000), 0.10));
            if (hover > 0.01 && armed < 0.99)
            {
                var lip = back ? Palette.TextSecondary.WithAlpha(0.55 * hover) : Palette.GreenHover.WithAlpha(hover * (1 - armed));
                p.FillAnnularSector(c, c, outer - p.S(3) * k, outer, a0, a1, gap, lip, lip);
            }

            var box = boxes[i];
            var (bx, by) = (c + (float)box.X * k, c + (float)box.Y * k);
            var (hw, hh) = ((float)box.HalfWidth, (float)box.HalfHeight);
            var textColour = Argb.Lerp(back ? Palette.TextSecondary : Palette.Text, Palette.TextOnGreen, armed);
            textColour = Argb.Lerp(fill, textColour, fade);
            var labelPx = (int)Math.Round(p.S(RingLabelMetrics.LabelText96));
            var line = p.S(RingLabelMetrics.LineHeight96);

            if (!isLevel)
            {
                // Category: icon above a one-line label.
                var iconColour = Argb.Lerp(fill, Argb.Lerp(Palette.TextSecondary, Palette.GreenHover, hover), fade);
                var icon = p.S(RingLabelMetrics.Icon96);
                var glyph = view.Categories is { } cats && i < cats.Count ? Icons.For(cats[i]) : Glyph.Tools;
                Icons.Draw(p, glyph, bx, by - hh + icon / 2, icon, iconColour);
                var fit = Fonts.Fit(view.Labels[i], (int)(2 * hw), labelPx, LabelWeight, twoLines: false);
                p.Text(fit.Lines[0], bx - hw, by + hh - line - p.S(2), bx + hw, by + hh, textColour, fit.SizePx, LabelWeight);
                continue;
            }
            if (back)
            {
                var icon = p.S(13);
                Icons.Draw(p, Glyph.Back, bx, by - p.S(6.5), icon, textColour);
                p.Text(view.Labels[i], bx - hw, by + p.S(1), bx + hw, by + line + p.S(1), textColour, (int)Math.Round(p.S(11.5)), 500);
                continue;
            }
            var f = Fonts.Fit(view.Labels[i], (int)(2 * hw), labelPx, LabelWeight, twoLines: true);
            if (f.Lines.Length == 1)
                p.Text(f.Lines[0], bx - hw, by - line / 2, bx + hw, by + line / 2, textColour, f.SizePx, LabelWeight, ellipsis: f.Ellipsis);
            else
            {
                p.Text(f.Lines[0], bx - hw, by - line, bx + hw, by, textColour, f.SizePx, LabelWeight);
                p.Text(f.Lines[1], bx - hw, by, bx + hw, by + line, textColour, f.SizePx, LabelWeight);
            }
        }
        Hub(p, c, k, view, fade);
    }

    private static void Hub(Painter p, float c, float k, MenuView view, double fade)
    {
        var r = p.S(RingLabelMetrics.Hub96) * k;
        p.FillCircle(c, c, r, Palette.Sunken);
        p.StrokeCircle(c, c, r, Palette.Edge, p.S(1));
        if (view.Labels.Count == 0)
        {
            Icons.Draw(p, Glyph.None, c, c, p.S(18), Argb.Lerp(Palette.Sunken, Palette.TextSecondary, fade));
            return;
        }
        if (view.Open is { } open)
        {
            var colour = Argb.Lerp(Palette.Sunken, Palette.TextSecondary, fade);
            Icons.Draw(p, Icons.For(open), c, c - p.S(7), p.S(13), Argb.Lerp(Palette.Sunken, Palette.Green, fade));
            p.Text(view.Hub, c - r + p.S(3), c + p.S(1), c + r - p.S(3), c + p.S(15), colour, (int)Math.Round(p.S(RingLabelMetrics.SmallText96)), 600);
            return;
        }
        Icons.Mark(p, c, c, p.S(11), Argb.Lerp(Palette.Sunken, Palette.Green, fade), Argb.Lerp(Palette.Sunken, Palette.Edge, fade));
    }
}
