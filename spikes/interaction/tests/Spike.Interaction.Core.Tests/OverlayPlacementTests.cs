using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Overlay;
using Xunit;

namespace Spike.Interaction.Core.Tests;

public class OverlayPlacementTests
{
    private static readonly RECT Work = new(0, 0, 1920, 1040); // taskbar at the bottom

    [Fact]
    public void Centres_on_cursor_when_there_is_room()
    {
        var r = OverlayPlacement.Place(new POINT(960, 520), Work, 220, 220);
        Assert.Equal(new RECT(850, 410, 1070, 630), r);
    }

    [Theory]
    [InlineData(0, 0)]          // top-left corner
    [InlineData(1919, 0)]       // top-right
    [InlineData(0, 1039)]       // bottom-left
    [InlineData(1919, 1039)]    // bottom-right
    [InlineData(5, 500)]        // left edge
    [InlineData(1915, 500)]     // right edge
    public void Stays_fully_inside_work_area_near_edges_and_corners(int x, int y)
    {
        var r = OverlayPlacement.Place(new POINT(x, y), Work, 220, 220);
        Assert.True(r.Left >= Work.Left && r.Top >= Work.Top && r.Right <= Work.Right && r.Bottom <= Work.Bottom, r.ToString());
        Assert.Equal(220, r.Width);
        Assert.Equal(220, r.Height);
    }

    [Fact]
    public void Works_on_a_secondary_monitor_with_negative_coordinates()
    {
        var work = new RECT(-2560, -300, 0, 1140);
        var r = OverlayPlacement.Place(new POINT(-2559, 1139), work, 220, 220);
        Assert.Equal(new RECT(-2560, 920, -2340, 1140), r);
    }

    [Fact]
    public void Anchors_top_left_when_overlay_is_larger_than_work_area()
    {
        var work = new RECT(100, 100, 200, 200);
        var r = OverlayPlacement.Place(new POINT(150, 150), work, 500, 500);
        Assert.Equal(100, r.Left);
        Assert.Equal(100, r.Top);
    }

    [Theory]
    [InlineData(220, 96, 220)]
    [InlineData(220, 120, 275)]
    [InlineData(220, 144, 330)]
    [InlineData(220, 192, 440)]
    public void Scales_design_size_by_dpi(int size96, uint dpi, int expected)
    {
        Assert.Equal(expected, OverlayPlacement.Scale(size96, dpi));
    }
}
