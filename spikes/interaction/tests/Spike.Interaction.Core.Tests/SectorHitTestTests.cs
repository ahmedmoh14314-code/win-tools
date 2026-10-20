using Spike.Interaction.Core.Overlay;
using Xunit;

namespace Spike.Interaction.Core.Tests;

public class SectorHitTestTests
{
    // Centre (100,100), dead zone radius 20, outer radius 100, 4 sectors clockwise from 12 o'clock.
    [Theory]
    [InlineData(100, 30, 0)]    // up → sector 0 (A)
    [InlineData(170, 100, 1)]   // right → sector 1 (B)
    [InlineData(100, 170, 2)]   // down → sector 2 (C)
    [InlineData(30, 100, 3)]    // left → sector 3 (D)
    [InlineData(150, 50, 0)]    // up-right diagonal is still inside sector 0 (0..90° clockwise)
    [InlineData(100, 100, -1)]  // dead zone
    [InlineData(100, 90, -1)]   // inside dead zone radius
    [InlineData(250, 100, -1)]  // outside ring
    public void Maps_points_to_sectors(int x, int y, int expected)
    {
        Assert.Equal(expected, SectorHitTest.Hit(x, y, 100, 100, 20, 100, 4));
    }

    [Fact]
    public void Handles_zero_sectors()
    {
        Assert.Equal(-1, SectorHitTest.Hit(100, 30, 100, 100, 20, 100, 0));
    }
}
