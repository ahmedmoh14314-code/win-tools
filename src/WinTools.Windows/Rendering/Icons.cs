using WinTools.Core.Interaction;
using WinTools.Windows.Native;

namespace WinTools.Windows.Rendering;

internal enum Glyph { Convert, Compress, Resize, Rotate, Tools, Back, Check, Alert, Info, None, Mark, More }

/// <summary>
/// The product's line icons, drawn as vectors on a 24-unit grid (stroke 2 units, round caps), so they are crisp at every
/// DPI and need no asset files. Simple geometric shapes of our own; no third-party icon set is used.
/// </summary>
internal static class Icons
{
    /// <summary>The glyph for a category's icon key (module data); an unknown key gets the generic tools glyph.</summary>
    public static Glyph For(string? icon) => icon switch
    {
        "convert" => Glyph.Convert,
        "compress" => Glyph.Compress,
        "resize" => Glyph.Resize,
        "rotate" => Glyph.Rotate,
        "more" => Glyph.More,
        _ => Glyph.Tools,
    };

    /// <summary>Draws <paramref name="glyph"/> centred on (<paramref name="cx"/>, <paramref name="cy"/>), <paramref name="size"/> pixels across.</summary>
    public static void Draw(Painter p, Glyph glyph, float cx, float cy, float size, Argb colour)
    {
        var u = size / 24f;                         // one grid unit
        var w = Math.Max(1.2f, 2f * u);             // stroke
        PointF P(float x, float y) => new(cx + (x - 12) * u, cy + (y - 12) * u);
        void L(float x1, float y1, float x2, float y2) { var a = P(x1, y1); var b = P(x2, y2); p.Line(a.X, a.Y, b.X, b.Y, colour, w); }
        void Poly(params PointF[] pts) => p.Polyline(pts, colour, w);

        switch (glyph)
        {
            case Glyph.Convert: // two opposed arrows: transform one thing into another
                L(4, 8, 19, 8); Poly(P(15, 4), P(19, 8), P(15, 12));
                L(20, 16, 5, 16); Poly(P(9, 12), P(5, 16), P(9, 20));
                break;
            case Glyph.Compress: // two arrows pressing in towards a centre line
                L(12, 2.5f, 12, 9); Poly(P(8.5f, 6), P(12, 9.5f), P(15.5f, 6));
                L(12, 21.5f, 12, 15); Poly(P(8.5f, 18), P(12, 14.5f), P(15.5f, 18));
                L(4, 12, 20, 12);
                break;
            case Glyph.Resize: // a frame corner and a diagonal arrow out of it
                Poly(P(4, 10), P(4, 4), P(10, 4));
                Poly(P(20, 14), P(20, 20), P(14, 20));
                L(6, 6, 18, 18);
                break;
            case Glyph.Rotate: // a circular arrow
                p.Arc(cx, cy, 7.5f * u, Math.PI * 0.2, Math.PI * 1.6, colour, w); // clockwise, open at the top
                Poly(P(3f, 5.5f), P(7.6f, 5.9f), P(6.5f, 10.4f));                  // head at the end, pointing clockwise
                break;
            case Glyph.Tools: // three sliders
                L(4, 6, 20, 6); L(4, 12, 20, 12); L(4, 18, 20, 18);
                p.FillCircle(P(9, 6).X, P(9, 6).Y, 2.4f * u, colour);
                p.FillCircle(P(16, 12).X, P(16, 12).Y, 2.4f * u, colour);
                p.FillCircle(P(7, 18).X, P(7, 18).Y, 2.4f * u, colour);
                break;
            case Glyph.Back: // arrow pointing back
                L(19, 12, 5, 12); Poly(P(11, 6), P(5, 12), P(11, 18));
                break;
            case Glyph.Check:
                Poly(P(5, 12.5f), P(10, 17.5f), P(19, 7));
                break;
            case Glyph.Alert: // exclamation mark
                L(12, 5, 12, 13.5f);
                p.FillCircle(P(12, 18.5f).X, P(12, 18.5f).Y, 1.5f * u, colour);
                break;
            case Glyph.Info:
                L(12, 10.5f, 12, 18.5f);
                p.FillCircle(P(12, 6).X, P(12, 6).Y, 1.5f * u, colour);
                break;
            case Glyph.None: // circle with a slash: nothing applies
                p.StrokeCircle(cx, cy, 8.5f * u, colour, w);
                L(6, 18, 18, 6);
                break;
            case Glyph.More: // three dots: there is more than the ring shows
                foreach (var x in new[] { 5f, 12f, 19f }) p.FillCircle(P(x, 12).X, P(x, 12).Y, 2f * u, colour);
                break;
            case Glyph.Mark:
                Mark(p, cx, cy, size / 2, colour, Palette.Edge);
                break;
        }
    }

    /// <summary>
    /// The WinTools mark: a ring of five segments, one of them lit, around a hub, the radial menu reduced to a symbol.
    /// </summary>
    public static void Mark(Painter p, float cx, float cy, float radius, Argb accent, Argb rest)
    {
        var inner = radius * 0.5f;
        var gap = Math.Max(1f, radius * 0.16f);
        for (var i = 0; i < 5; i++)
        {
            var (a0, a1) = RingGeometry.SectorBounds(i, 5, -Math.PI / 5);
            var c = i == 0 ? accent : rest;
            p.FillAnnularSector(cx, cy, inner, radius, a0, a1, gap, c, c);
        }
        p.FillCircle(cx, cy, inner * 0.55f, accent);
    }
}
