namespace WinTools.Core.Interaction;

/// <summary>
/// Pure geometry of the radial overlay: placement clamped to the monitor work area, DPI scaling and sector hit-testing.
/// Sector 0 starts at 12 o'clock and sectors run clockwise; -1 means the dead zone or outside the ring.
/// </summary>
public static class RingGeometry
{
    public const int OuterRadius96 = 110;
    public const int InnerRadius96 = 28;

    public static int Scale(int size96, uint dpi) => (int)Math.Round(size96 * dpi / 96.0);

    /// <summary>Top-left of a size×size window centred on the cursor and kept inside the work area.</summary>
    public static (int Left, int Top) Place(int cursorX, int cursorY, int size, int workLeft, int workTop, int workRight, int workBottom)
    {
        static int Clamp(int v, int lo, int hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);
        return (Clamp(cursorX - size / 2, workLeft, workRight - size), Clamp(cursorY - size / 2, workTop, workBottom - size));
    }

    /// <param name="dx">Horizontal offset from the ring centre in pixels.</param>
    /// <param name="dy">Vertical offset from the ring centre in pixels (down is positive).</param>
    public static int HitSector(int dx, int dy, int innerRadius, int outerRadius, int sectorCount)
    {
        if (sectorCount <= 0) return -1;
        var d2 = (double)dx * dx + (double)dy * dy;
        if (d2 < (double)innerRadius * innerRadius || d2 > (double)outerRadius * outerRadius) return -1;
        var angle = Math.Atan2(dx, -dy); // 0 = up, clockwise positive
        if (angle < 0) angle += 2 * Math.PI;
        return Math.Min((int)(angle / (2 * Math.PI / sectorCount)), sectorCount - 1);
    }
}
