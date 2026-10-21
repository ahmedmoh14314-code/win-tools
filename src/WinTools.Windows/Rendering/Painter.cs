using WinTools.Windows.Native;

namespace WinTools.Windows.Rendering;

internal enum TextAlign { Left, Centre, Right }

/// <summary>A text drawing queued until the shapes are done (GDI draws on top of what GDI+ drew).</summary>
internal readonly record struct TextRun(string Text, float Left, float Top, float Right, float Bottom, Argb Colour, int SizePx, int Weight, TextAlign Align, bool Ellipsis);

/// <summary>
/// Draws one frame on a <see cref="Surface"/>. Coordinates are pixels from the surface's top-left; angles for arcs are
/// radians clockwise from 12 o'clock, like <see cref="Core.Interaction.RingGeometry"/>. Shapes are drawn with GDI+
/// immediately; texts are queued and drawn with GDI ClearType when the painter is disposed, after which the pixels
/// under them are made opaque again. Texts must therefore only be placed over opaque areas.
/// </summary>
internal sealed class Painter : IDisposable
{
    private readonly Surface _surface;
    private readonly List<TextRun> _texts = [];
    private nint _g;

    public uint Dpi { get; }

    public Painter(Surface surface, nint graphics, uint dpi)
    {
        _surface = surface;
        _g = graphics;
        Dpi = dpi;
    }

    /// <summary>Pixels at this DPI for a length at 96 DPI.</summary>
    public float S(double length96) => (float)(length96 * Dpi / 96.0);

    private static float Deg(double radians) => (float)(radians * 180 / Math.PI) - 90; // GDI+: degrees clockwise from 3 o'clock

    public void FillCircle(float cx, float cy, float r, Argb colour)
    {
        _ = Gdip.GdipCreateSolidFill(colour.Value, out var b);
        _ = Gdip.GdipFillEllipse(_g, b, cx - r, cy - r, 2 * r, 2 * r);
        _ = Gdip.GdipDeleteBrush(b);
    }

    public void StrokeCircle(float cx, float cy, float r, Argb colour, float width)
    {
        var pen = Pen(colour, width);
        _ = Gdip.GdipDrawEllipse(_g, pen, cx - r, cy - r, 2 * r, 2 * r);
        _ = Gdip.GdipDeletePen(pen);
    }

    /// <summary>
    /// Fills the part of the annulus between <paramref name="inner"/> and <paramref name="outer"/> from angle
    /// <paramref name="a0"/> to <paramref name="a1"/>, leaving a gap of <paramref name="gap"/> pixels to each neighbour
    /// (the same width at both radii). A vertical gradient from <paramref name="top"/> to <paramref name="bottom"/> gives depth.
    /// </summary>
    public void FillAnnularSector(float cx, float cy, float inner, float outer, double a0, double a1, float gap, Argb top, Argb bottom)
    {
        var full = a1 - a0 >= 2 * Math.PI - 1e-9;
        _ = Gdip.GdipCreatePath(Gdip.FillModeAlternate, out var path);
        if (full)
        {
            _ = Gdip.GdipAddPathArc(path, cx - outer, cy - outer, 2 * outer, 2 * outer, 0, 360);
            _ = Gdip.GdipClosePathFigure(path);
            _ = Gdip.GdipStartPathFigure(path);
            _ = Gdip.GdipAddPathArc(path, cx - inner, cy - inner, 2 * inner, 2 * inner, 0, 360);
            _ = Gdip.GdipClosePathFigure(path);
        }
        else
        {
            var go = gap / 2 / outer;
            var gi = gap / 2 / Math.Max(inner, 1);
            var (o0, o1) = (a0 + go, a1 - go);
            var (i0, i1) = (a0 + gi, a1 - gi);
            if (o1 <= o0 || i1 <= i0) { _ = Gdip.GdipDeletePath(path); return; }
            _ = Gdip.GdipAddPathArc(path, cx - outer, cy - outer, 2 * outer, 2 * outer, Deg(o0), (float)((o1 - o0) * 180 / Math.PI));
            _ = Gdip.GdipAddPathArc(path, cx - inner, cy - inner, 2 * inner, 2 * inner, Deg(i1), (float)(-(i1 - i0) * 180 / Math.PI));
            _ = Gdip.GdipClosePathFigure(path);
        }
        nint brush;
        if (top == bottom) _ = Gdip.GdipCreateSolidFill(top.Value, out brush);
        else _ = Gdip.GdipCreateLineBrush(new PointF(cx, cy - outer - 1), new PointF(cx, cy + outer + 1), top.Value, bottom.Value, Gdip.WrapModeTileFlipXY, out brush);
        _ = Gdip.GdipFillPath(_g, brush, path);
        _ = Gdip.GdipDeleteBrush(brush);
        _ = Gdip.GdipDeletePath(path);
    }

    public void FillRoundRect(float x, float y, float w, float h, float r, Argb colour)
    {
        var path = RoundRect(x, y, w, h, r);
        _ = Gdip.GdipCreateSolidFill(colour.Value, out var b);
        _ = Gdip.GdipFillPath(_g, b, path);
        _ = Gdip.GdipDeleteBrush(b);
        _ = Gdip.GdipDeletePath(path);
    }

    public void StrokeRoundRect(float x, float y, float w, float h, float r, Argb colour, float width)
    {
        var path = RoundRect(x, y, w, h, r);
        var pen = Pen(colour, width);
        _ = Gdip.GdipDrawPath(_g, pen, path);
        _ = Gdip.GdipDeletePen(pen);
        _ = Gdip.GdipDeletePath(path);
    }

    private static nint RoundRect(float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        _ = Gdip.GdipCreatePath(Gdip.FillModeAlternate, out var path);
        var d = 2 * r;
        _ = Gdip.GdipAddPathArc(path, x, y, d, d, 180, 90);
        _ = Gdip.GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        _ = Gdip.GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        _ = Gdip.GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        _ = Gdip.GdipClosePathFigure(path);
        return path;
    }

    /// <summary>A soft drop shadow under a rounded rectangle, as a few widening, fading outlines.</summary>
    public void Shadow(float x, float y, float w, float h, float r, float spread, double strength)
    {
        const int Steps = 6;
        for (var i = Steps; i >= 1; i--)
        {
            var grow = spread * i / Steps;
            var a = strength * (1 - (i - 1) / (double)Steps) / Steps * 1.6;
            FillRoundRect(x - grow, y - grow + spread * 0.35f, w + 2 * grow, h + 2 * grow, r + grow, Palette.Shadow.WithAlpha(a));
        }
    }

    public void Line(float x1, float y1, float x2, float y2, Argb colour, float width)
    {
        var pen = Pen(colour, width);
        _ = Gdip.GdipDrawLine(_g, pen, x1, y1, x2, y2);
        _ = Gdip.GdipDeletePen(pen);
    }

    public unsafe void Polyline(ReadOnlySpan<PointF> points, Argb colour, float width)
    {
        var pen = Pen(colour, width);
        fixed (PointF* p = points) _ = Gdip.GdipDrawLines(_g, pen, p, points.Length);
        _ = Gdip.GdipDeletePen(pen);
    }

    public unsafe void FillPolygon(ReadOnlySpan<PointF> points, Argb colour)
    {
        _ = Gdip.GdipCreateSolidFill(colour.Value, out var b);
        fixed (PointF* p = points) _ = Gdip.GdipFillPolygon(_g, b, p, points.Length, Gdip.FillModeAlternate);
        _ = Gdip.GdipDeleteBrush(b);
    }

    /// <summary>An arc of a circle (radians clockwise from 12 o'clock).</summary>
    public void Arc(float cx, float cy, float r, double from, double sweep, Argb colour, float width)
    {
        var pen = Pen(colour, width);
        _ = Gdip.GdipDrawArc(_g, pen, cx - r, cy - r, 2 * r, 2 * r, Deg(from), (float)(sweep * 180 / Math.PI));
        _ = Gdip.GdipDeletePen(pen);
    }

    private static nint Pen(Argb colour, float width)
    {
        _ = Gdip.GdipCreatePen1(colour.Value, width, Gdip.UnitPixel, out var pen);
        _ = Gdip.GdipSetPenStartCap(pen, Gdip.LineCapRound);
        _ = Gdip.GdipSetPenEndCap(pen, Gdip.LineCapRound);
        _ = Gdip.GdipSetPenLineJoin(pen, Gdip.LineJoinRound);
        return pen;
    }

    /// <summary>Queues a text, drawn on one line inside the rectangle (vertically centred), over an opaque area.</summary>
    public void Text(string text, float left, float top, float right, float bottom, Argb colour, int sizePx, int weight = 400, TextAlign align = TextAlign.Centre, bool ellipsis = true) =>
        _texts.Add(new TextRun(text, left, top, right, bottom, colour, sizePx, weight, align, ellipsis));

    public void Dispose()
    {
        if (_g == 0) return;
        _ = Gdip.GdipFlush(_g, Gdip.FlushIntentionSync);
        _ = Gdip.GdipDeleteGraphics(_g);
        _g = 0;
        if (_texts.Count == 0) return;
        var dc = _surface.Dc;
        _ = Gdi32.SetBkMode(dc, Gdi32.TRANSPARENT);
        var old = nint.Zero;
        foreach (var t in _texts)
        {
            var font = Fonts.Get(t.SizePx, t.Weight);
            var prev = Gdi32.SelectObject(dc, font);
            if (old == 0) old = prev;
            _ = Gdi32.SetTextColor(dc, t.Colour.ColorRef);
            var rect = new RECT { Left = (int)Math.Floor(t.Left), Top = (int)Math.Floor(t.Top), Right = (int)Math.Ceiling(t.Right), Bottom = (int)Math.Ceiling(t.Bottom) };
            var format = User32.DT_SINGLELINE | User32.DT_VCENTER | User32.DT_NOPREFIX | (t.Ellipsis ? Fonts.DT_END_ELLIPSIS : 0)
                | t.Align switch { TextAlign.Left => 0u, TextAlign.Right => Fonts.DT_RIGHT, _ => User32.DT_CENTER };
            User32.DrawTextW(dc, t.Text, -1, ref rect, format);
            _surface.MakeOpaque(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        if (old != 0) Gdi32.SelectObject(dc, old);
        Ui.GdiFlush();
        _texts.Clear();
    }
}
