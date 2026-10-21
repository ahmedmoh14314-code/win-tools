namespace WinTools.Core.Interaction;

/// <summary>
/// Pure geometry of the radial overlay: placement clamped to the monitor work area, DPI scaling and hit-testing.
/// Two levels share one centre: the categories fill the inner ring, and the commands of the active category fill an
/// arc of the outer band, centred on that category. Angles run clockwise from 12 o'clock in [0, 2π); -1 means the dead
/// zone, a gap, or outside.
/// </summary>
public static class RingGeometry
{
    /// <summary>The dead zone: the pointer starts here, so leaving it is the movement threshold.</summary>
    public const int InnerRadius96 = 28;
    /// <summary>Outer edge of the category ring.</summary>
    public const int OuterRadius96 = 110;
    /// <summary>Outer edge of the command band; the overlay window is this circle.</summary>
    public const int CommandRadius96 = 190;
    /// <summary>Widest angle one command gets, so a category with few commands keeps them near its own direction.</summary>
    public const double MaxCommandSweep = Math.PI / 4;

    public static int Scale(int size96, uint dpi) => (int)Math.Round(size96 * dpi / 96.0);

    /// <summary>Top-left of a size×size window centred on the cursor and kept inside the work area.</summary>
    public static (int Left, int Top) Place(int cursorX, int cursorY, int size, int workLeft, int workTop, int workRight, int workBottom)
    {
        static int Clamp(int v, int lo, int hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);
        return (Clamp(cursorX - size / 2, workLeft, workRight - size), Clamp(cursorY - size / 2, workTop, workBottom - size));
    }

    /// <summary>Angle of an offset from the centre: 0 = up, clockwise positive, in [0, 2π).</summary>
    public static double Angle(double dx, double dy)
    {
        var a = Math.Atan2(dx, -dy);
        return a < 0 ? a + 2 * Math.PI : a;
    }

    /// <summary>Offset from the centre of the point at <paramref name="angle"/> and <paramref name="radius"/> (down is positive).</summary>
    public static (double Dx, double Dy) PointAt(double angle, double radius) => (radius * Math.Sin(angle), -radius * Math.Cos(angle));

    /// <param name="dx">Horizontal offset from the ring centre in pixels.</param>
    /// <param name="dy">Vertical offset from the ring centre in pixels (down is positive).</param>
    public static int HitSector(int dx, int dy, int innerRadius, int outerRadius, int sectorCount)
    {
        if (sectorCount <= 0) return -1;
        var d2 = (double)dx * dx + (double)dy * dy;
        if (d2 < (double)innerRadius * innerRadius || d2 > (double)outerRadius * outerRadius) return -1;
        return Math.Min((int)(Angle(dx, dy) / (2 * Math.PI / sectorCount)), sectorCount - 1);
    }

    /// <summary>Middle angle of sector <paramref name="index"/> of <paramref name="count"/>.</summary>
    public static double SectorCentre(int index, int count) => (index + 0.5) * 2 * Math.PI / count;

    /// <summary>
    /// The arc of the outer band holding <paramref name="commandCount"/> commands of category <paramref name="group"/>:
    /// each command gets <c>Sweep</c> radians (at most <see cref="MaxCommandSweep"/>), and the arc is centred on the category.
    /// <c>Start</c> may be negative or beyond 2π; hit-testing normalises it.
    /// </summary>
    public static (double Start, double Sweep) CommandArc(int group, int groupCount, int commandCount)
    {
        var sweep = Math.Min(MaxCommandSweep, 2 * Math.PI / Math.Max(1, commandCount));
        return (SectorCentre(group, groupCount) - sweep * commandCount / 2, sweep);
    }

    /// <summary>Middle angle of command <paramref name="index"/> in an arc.</summary>
    public static double CommandCentre(double start, double sweep, int index) => Normalize(start + (index + 0.5) * sweep);

    /// <summary>Command under an offset in the outer band, or -1 outside the band or the arc.</summary>
    public static int HitCommand(int dx, int dy, int innerRadius, int outerRadius, double start, double sweep, int count)
    {
        if (count <= 0 || !(sweep > 0)) return -1;
        var d2 = (double)dx * dx + (double)dy * dy;
        if (d2 < (double)innerRadius * innerRadius || d2 > (double)outerRadius * outerRadius) return -1;
        var index = (int)(Normalize(Angle(dx, dy) - start) / sweep);
        if (index < count) return index;
        return sweep * count >= 2 * Math.PI - 1e-9 ? count - 1 : -1; // a full circle has no gap: rounding lands on the last one
    }

    private static double Normalize(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }
}
