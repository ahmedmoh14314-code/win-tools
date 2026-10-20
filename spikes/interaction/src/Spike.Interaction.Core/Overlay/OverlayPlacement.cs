using Spike.Interaction.Core.Native;

namespace Spike.Interaction.Core.Overlay;

/// <summary>
/// Pure placement math. Given the physical cursor position, the monitor work area and the overlay size,
/// returns the window rectangle centred on the cursor and clamped so it is fully inside the work area.
/// If the overlay is larger than the work area it is anchored to the top-left of the work area.
/// </summary>
public static class OverlayPlacement
{
    public static RECT Place(POINT cursor, RECT workArea, int width, int height)
    {
        var left = cursor.X - width / 2;
        var top = cursor.Y - height / 2;

        var maxLeft = workArea.Right - width;
        var maxTop = workArea.Bottom - height;

        left = maxLeft < workArea.Left ? workArea.Left : Math.Clamp(left, workArea.Left, maxLeft);
        top = maxTop < workArea.Top ? workArea.Top : Math.Clamp(top, workArea.Top, maxTop);

        return new RECT(left, top, left + width, top + height);
    }

    /// <summary>Scale a 96-dpi design size to the monitor's DPI.</summary>
    public static int Scale(int size96, uint dpi) => (int)Math.Round(size96 * dpi / 96.0);
}
