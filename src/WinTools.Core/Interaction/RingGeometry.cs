namespace WinTools.Core.Interaction;

/// <summary>
/// Pure geometry of the radial overlay: placement clamped to the monitor work area, DPI scaling and sector hit-testing.
/// Angles run clockwise from 12 o'clock in [0, 2π). Sector i of n spans [start + i·w, start + (i+1)·w) with w = 2π/n;
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
    public static int HitSector(int dx, int dy, int innerRadius, int outerRadius, int sectorCount, double start = 0)
    {
        if (sectorCount <= 0) return -1;
        var d2 = (double)dx * dx + (double)dy * dy;
        if (d2 < (double)innerRadius * innerRadius || d2 > (double)outerRadius * outerRadius) return -1;
        return Math.Min((int)(Normalize(Angle(dx, dy) - start) / (2 * Math.PI / sectorCount)), sectorCount - 1);
    }

    /// <summary>Middle angle of sector <paramref name="index"/> of <paramref name="count"/>.</summary>
    public static double SectorCentre(int index, int count, double start = 0) => Normalize(start + (index + 0.5) * 2 * Math.PI / count);

    public static double Normalize(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }
}
