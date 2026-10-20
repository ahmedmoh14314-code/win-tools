namespace Spike.Interaction.Core.Overlay;

/// <summary>
/// Pure hit testing for a ring of N equal sectors around a centre with a dead zone in the middle.
/// Sector 0 starts at 12 o'clock and sectors run clockwise. Returns -1 for the dead zone or outside the ring.
/// </summary>
public static class SectorHitTest
{
    public static int Hit(int x, int y, int centerX, int centerY, int innerRadius, int outerRadius, int sectorCount)
    {
        if (sectorCount <= 0) return -1;
        var dx = x - centerX;
        var dy = y - centerY;
        var dist2 = (double)dx * dx + (double)dy * dy;
        if (dist2 < (double)innerRadius * innerRadius || dist2 > (double)outerRadius * outerRadius) return -1;

        // atan2 with y pointing down on screen; rotate so 0 is at 12 o'clock, clockwise positive.
        var angle = Math.Atan2(dx, -dy); // -pi..pi, 0 = up, +pi/2 = right
        if (angle < 0) angle += 2 * Math.PI;
        var sector = (int)(angle / (2 * Math.PI / sectorCount));
        return Math.Min(sector, sectorCount - 1);
    }
}
