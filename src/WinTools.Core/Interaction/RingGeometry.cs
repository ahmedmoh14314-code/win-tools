namespace WinTools.Core.Interaction;

/// <summary>
/// Pure geometry of the radial overlay: placement clamped to the monitor work area, DPI scaling and sector hit-testing.
/// Angles run clockwise from 12 o'clock in [0, 2π). Sector i of n spans [start + i·w, start + (i+1)·w) with w = 2π/n,
/// unless sector 0 is given its own width (<c>firstSweep</c>), in which case the others share the rest equally;
/// -1 means the dead zone or outside the ring.
/// </summary>
public static class RingGeometry
{
    /// <summary>The dead zone: each level appears with the pointer here, so leaving it is the movement threshold.</summary>
    public const int InnerRadius96 = 28;
    /// <summary>Outer edge of the ring; the overlay window is this circle.</summary>
    public const int OuterRadius96 = 110;
    /// <summary>Reaching this far into a category (or Back) opens it: a deliberate stroke, not a graze.</summary>
    public const int OpenRadius96 = 80;

    public static int Scale(int size96, uint dpi) => (int)Math.Round(size96 * dpi / 96.0);

    /// <summary>Top-left of a size×size window centred on the cursor and kept inside the work area.</summary>
    public static (int Left, int Top) Place(int cursorX, int cursorY, int size, int workLeft, int workTop, int workRight, int workBottom)
    {
        static int Clamp(int v, int lo, int hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);
        return (Clamp(cursorX - size / 2, workLeft, workRight - size), Clamp(cursorY - size / 2, workTop, workBottom - size));
    }

    /// <summary>Angle of an offset from the centre: 0 = up, clockwise positive, in [0, 2π).</summary>
    public static double Angle(double dx, double dy) => Normalize(Math.Atan2(dx, -dy));

    /// <summary>Offset from the centre of the point at <paramref name="angle"/> and <paramref name="radius"/> (down is positive).</summary>
    public static (double Dx, double Dy) PointAt(double angle, double radius) => (radius * Math.Sin(angle), -radius * Math.Cos(angle));

    /// <param name="dx">Horizontal offset from the ring centre in pixels.</param>
    /// <param name="dy">Vertical offset from the ring centre in pixels (down is positive).</param>
    /// <param name="start">Where sector 0 begins (radians clockwise from 12 o'clock).</param>
    /// <param name="firstSweep">Width of sector 0, or 0 for equal sectors.</param>
    public static int HitSector(int dx, int dy, int innerRadius, int outerRadius, int sectorCount, double start = 0, double firstSweep = 0)
    {
        if (sectorCount <= 0) return -1;
        var d2 = (double)dx * dx + (double)dy * dy;
        if (d2 < (double)innerRadius * innerRadius || d2 > (double)outerRadius * outerRadius) return -1;
        var rel = Normalize(Angle(dx, dy) - start);
        if (firstSweep <= 0 || sectorCount == 1) return Math.Min((int)(rel / (2 * Math.PI / sectorCount)), sectorCount - 1);
        if (rel < firstSweep) return 0;
        return Math.Min(1 + (int)((rel - firstSweep) / ((2 * Math.PI - firstSweep) / (sectorCount - 1))), sectorCount - 1);
    }

    /// <summary>Start and end angle of sector <paramref name="index"/> (end may exceed 2π).</summary>
    public static (double From, double To) SectorBounds(int index, int count, double start = 0, double firstSweep = 0)
    {
        if (firstSweep <= 0 || count == 1)
        {
            var w = 2 * Math.PI / count;
            return (start + index * w, start + (index + 1) * w);
        }
        if (index == 0) return (start, start + firstSweep);
        var rest = (2 * Math.PI - firstSweep) / (count - 1);
        return (start + firstSweep + (index - 1) * rest, start + firstSweep + index * rest);
    }

    /// <summary>Middle angle of sector <paramref name="index"/> of <paramref name="count"/>.</summary>
    public static double SectorCentre(int index, int count, double start = 0, double firstSweep = 0)
    {
        var (from, to) = SectorBounds(index, count, start, firstSweep);
        return Normalize((from + to) / 2);
    }

    public static double Normalize(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }
}
