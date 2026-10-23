using WinTools.Core.Interaction;

namespace WinTools.Core.Tests;

public sealed class TriggerDetectorTests
{
    private const int Space = 0x20, Escape = 0x1B, Ctrl = 0xA2, Shift = 0xA0;

    private static TriggerDetector Dragging()
    {
        var d = new TriggerDetector();
        d.OnMouseButton(MouseButton.Left, true, 100, 100);
        d.OnMouseMove(110, 100);
        Assert.True(d.IsDragging);
        return d;
    }

    [Fact]
    public void Space_with_the_left_button_down_is_a_trigger()
    {
        Assert.Equal(InputSignal.Trigger, Dragging().OnKey(Space, true));
    }

    [Fact]
    public void Space_without_the_left_button_is_ignored()
    {
        Assert.Equal(InputSignal.None, new TriggerDetector().OnKey(Space, true));
    }

    [Fact]
    public void Key_auto_repeat_does_not_trigger_again()
    {
        var d = Dragging();
        Assert.Equal(InputSignal.Trigger, d.OnKey(Space, true));
        Assert.Equal(InputSignal.None, d.OnKey(Space, true));
        d.OnKey(Space, false);
        Assert.Equal(InputSignal.Trigger, d.OnKey(Space, true));
    }

    [Fact]
    public void Resync_clears_a_modifier_whose_key_up_was_missed()
    {
        var d = Dragging();
        d.OnKey(0xA4, true);                       // Alt key-up never seen (secure desktop)
        Assert.Equal(InputSignal.TriggerRefused, d.OnKey(Space, true));
        d.OnKey(Space, false);
        d.Resync(leftDown: true, ctrl: false, shift: false, alt: false);
        Assert.Equal(InputSignal.Trigger, d.OnKey(Space, true));
    }

    [Fact]
    public void Resync_clears_a_button_whose_release_was_missed()
    {
        var d = Dragging();                         // button-up happened over an elevated window
        d.Resync(leftDown: false, ctrl: false, shift: false, alt: false);
        Assert.False(d.LeftButtonDown);
        Assert.Equal(InputSignal.None, d.OnKey(Space, true)); // typing a space must not open the ring
    }

    [Fact]
    public void Movement_below_the_threshold_is_not_a_drag()
    {
        var d = new TriggerDetector();
        d.OnMouseButton(MouseButton.Left, true, 100, 100);
        d.OnMouseMove(102, 101);
        Assert.False(d.IsDragging);
        Assert.True(d.LeftButtonDown);
    }

    [Fact]
    public void Left_release_is_reported_once_and_other_buttons_are_ignored()
    {
        var d = Dragging();
        Assert.Equal(InputSignal.None, d.OnMouseButton(MouseButton.Right, false, 0, 0));
        Assert.Equal(InputSignal.LeftButtonReleased, d.OnMouseButton(MouseButton.Left, false, 0, 0));
        Assert.Equal(InputSignal.None, d.OnMouseButton(MouseButton.Left, false, 0, 0));
        Assert.False(d.IsDragging);
    }

    [Fact]
    public void Escape_is_forwarded_on_key_down()
    {
        var d = new TriggerDetector();
        Assert.Equal(InputSignal.Escape, d.OnKey(Escape, true));
        Assert.Equal(InputSignal.None, d.OnKey(Escape, false));
    }
}

public sealed class RingGeometryTests
{
    [Theory]
    [InlineData(0, -80, 0)]   // 12 o'clock
    [InlineData(80, 0, 1)]    // 3 o'clock
    [InlineData(0, 80, 2)]    // 6 o'clock
    [InlineData(-80, 0, 3)]   // 9 o'clock
    [InlineData(5, 5, -1)]    // dead zone
    [InlineData(200, 0, -1)]  // outside the ring
    public void Four_sectors_run_clockwise_from_twelve(int dx, int dy, int expected)
    {
        Assert.Equal(expected, RingGeometry.HitSector(dx, dy, 28, 110, 4));
    }

    [Theory]
    [InlineData(0, -60)]
    [InlineData(60, 60)]
    [InlineData(-60, 10)]
    public void A_single_command_owns_the_whole_ring(int dx, int dy)
    {
        Assert.Equal(0, RingGeometry.HitSector(dx, dy, 28, 110, 1));
    }

    [Fact]
    public void No_commands_means_no_sector_anywhere()
    {
        Assert.Equal(-1, RingGeometry.HitSector(60, 0, 28, 110, 0));
    }

    [Theory]
    [InlineData(500, 500, 390, 390)]     // fits: centred
    [InlineData(5, 5, 0, 0)]             // top-left corner: clamped
    [InlineData(1915, 1015, 1700, 800)]  // bottom-right corner: clamped
    public void Placement_centres_on_the_cursor_inside_the_work_area(int x, int y, int left, int top)
    {
        Assert.Equal((left, top), RingGeometry.Place(x, y, 220, 0, 0, 1920, 1020));
    }

    [Theory]
    [InlineData(96u, 220)]
    [InlineData(120u, 275)]
    [InlineData(144u, 330)]
    [InlineData(192u, 440)]
    public void Size_scales_with_dpi(uint dpi, int expected)
    {
        Assert.Equal(expected, RingGeometry.Scale(220, dpi));
    }
}

public sealed class SignalPackingTests
{
    [Theory]
    [InlineData(InputSignal.Trigger, true, true, 1919, 1079)]
    [InlineData(InputSignal.LeftButtonReleased, false, false, 0, 0)]
    [InlineData(InputSignal.Escape, true, false, -1920, -300)]   // monitor left of / above the primary
    [InlineData(InputSignal.Trigger, true, true, 32767, -32768)]
    public void Signals_round_trip_with_the_pointer_position(InputSignal kind, bool leftDown, bool dragging, int x, int y)
    {
        Assert.Equal(new SignalPacking.Signal(kind, leftDown, dragging, x, y), SignalPacking.Unpack(SignalPacking.Pack(kind, leftDown, dragging, x, y)));
    }

    [Fact]
    public void Detector_reports_the_pointer_where_the_trigger_happened()
    {
        var d = new TriggerDetector();
        d.OnMouseButton(MouseButton.Left, true, 100, 100);
        d.OnMouseMove(140, 130);
        Assert.Equal(InputSignal.Trigger, d.OnKey(0x20, true));
        Assert.Equal((140, 130), d.Position);
    }
}
