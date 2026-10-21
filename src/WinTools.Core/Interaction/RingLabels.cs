namespace WinTools.Core.Interaction;

/// <summary>Where a sector's label may be drawn: a box centred at (<see cref="X"/>, <see cref="Y"/>), offsets in pixels from the ring centre (down is positive).</summary>
public readonly record struct LabelBox(double X, double Y, double HalfWidth, double HalfHeight)
{
    public bool Intersects(LabelBox o, double gap) =>
        Math.Abs(X - o.X) < HalfWidth + o.HalfWidth + gap && Math.Abs(Y - o.Y) < HalfHeight + o.HalfHeight + gap;

    /// <summary>Nearest and farthest distance of the box from the ring centre.</summary>
    public (double Near, double Far) Reach()
    {
        var nx = Math.Max(0, Math.Abs(X) - HalfWidth);
        var ny = Math.Max(0, Math.Abs(Y) - HalfHeight);
        var fx = Math.Abs(X) + HalfWidth;
        var fy = Math.Abs(Y) + HalfHeight;
        return (Math.Sqrt(nx * nx + ny * ny), Math.Sqrt(fx * fx + fy * fy));
    }
}

/// <summary>
/// Lays out the label boxes of one ring so that no two overlap and none leaves the band of the ring it is drawn on.
/// Each box starts as wide as allowed and is narrowed only where a neighbour or the band edge is in the way (the wider
/// of two overlapping boxes first), so labels at the sides of the ring (neighbours above and below) keep more room than
/// labels at the top and bottom.
/// The renderer then fits each text into its box: one line, else two, else a smaller size, else an ellipsis.
/// </summary>
public static class RingLabels
{
    /// <param name="count">Sectors on the ring.</param>
    /// <param name="start">Where sector 0 begins (radians clockwise from 12 o'clock).</param>
    /// <param name="firstSweep">Width of sector 0, or 0 when all sectors are equal.</param>
    /// <param name="radius">Distance of the label centres from the ring centre.</param>
    /// <param name="halfHeight">Half the height of every box.</param>
    /// <param name="maxHalfWidth">Upper bound for half the width.</param>
    /// <param name="inner">Inner edge of the band the labels must stay in.</param>
    /// <param name="outer">Outer edge of that band.</param>
    /// <param name="gap">Space kept between neighbouring boxes.</param>
    public static LabelBox[] Layout(int count, double start, double firstSweep, double radius, double halfHeight, double maxHalfWidth,
        double inner, double outer, double gap)
    {
        var boxes = new LabelBox[count];
        for (var i = 0; i < count; i++)
        {
            var (x, y) = RingGeometry.PointAt(RingGeometry.SectorCentre(i, count, start, firstSweep), radius);
            boxes[i] = new LabelBox(x, y, maxHalfWidth, halfHeight);
        }
        const double step = 0.5, minHalfWidth = 6;
        for (var i = 0; i < count; i++)
            while (boxes[i].HalfWidth > minHalfWidth && boxes[i].Reach() is var (near, far) && (near < inner || far > outer))
                boxes[i] = boxes[i] with { HalfWidth = boxes[i].HalfWidth - step };
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < count; i++)
                for (var j = i + 1; j < count; j++)
                {
                    if (!boxes[i].Intersects(boxes[j], gap)) continue;
                    if (boxes[i].HalfWidth <= minHalfWidth && boxes[j].HalfWidth <= minHalfWidth) continue; // stacked: width cannot help
                    // Narrow the wider of the two first, so a box already narrowed by the band is not narrowed again.
                    var (wi, wj) = (boxes[i].HalfWidth, boxes[j].HalfWidth);
                    if (wi > minHalfWidth && wi >= wj) boxes[i] = boxes[i] with { HalfWidth = wi - step };
                    if (wj > minHalfWidth && wj >= wi) boxes[j] = boxes[j] with { HalfWidth = wj - step };
                    changed = true;
                }
        }
        return boxes;
    }
}
