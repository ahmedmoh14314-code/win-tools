using Spike.Interaction.Core.Input;
using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Session;
using Xunit;

namespace Spike.Interaction.Core.Tests;

public class TriggerSpecTests
{
    [Theory]
    [InlineData("space", VK.SPACE, false, false, false)]
    [InlineData("ctrl+space", VK.SPACE, true, false, false)]
    [InlineData("Ctrl+Shift+Space", VK.SPACE, true, true, false)]
    [InlineData("f8", VK.F1 + 7, false, false, false)]
    [InlineData("q", 'Q', false, false, false)]
    public void Parses_key_triggers(string text, int vk, bool ctrl, bool shift, bool alt)
    {
        var t = TriggerSpec.Parse(text);
        Assert.Equal(vk, t.VirtualKey);
        Assert.Equal(ctrl, t.Ctrl); Assert.Equal(shift, t.Shift); Assert.Equal(alt, t.Alt);
        Assert.Null(t.Button);
    }

    [Theory]
    [InlineData("mbutton", MouseButton.Middle)]
    [InlineData("xbutton1", MouseButton.X1)]
    [InlineData("x2", MouseButton.X2)]
    public void Parses_mouse_triggers(string text, MouseButton b)
    {
        var t = TriggerSpec.Parse(text);
        Assert.Equal(b, t.Button);
        Assert.Null(t.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl")]
    [InlineData("space+mbutton")]
    [InlineData("ctrl+mbutton")]
    [InlineData("banana")]
    public void Rejects_invalid(string text) => Assert.Throws<ArgumentException>(() => TriggerSpec.Parse(text));
}

public class TriggerDetectorTests
{
    private static MouseEvent Move(int x, int y) => new(null, false, x, y, false, 0);
    private static MouseEvent Left(bool down, int x = 0, int y = 0) => new(MouseButton.Left, down, x, y, false, 0);
    private static KeyEvent Key(int vk, bool down) => new(vk, down, false, 0);

    [Fact]
    public void Space_while_left_button_held_activates_once_until_released()
    {
        var d = new TriggerDetector(TriggerSpec.Parse("space"));
        Assert.Equal(TriggerDetector.Decision.None, d.OnMouse(Left(true, 10, 10)));
        Assert.Equal(TriggerDetector.Decision.DragStarted, d.OnMouse(Move(30, 30)));
        Assert.Equal(TriggerDetector.Decision.Activate, d.OnKey(Key(VK.SPACE, true)));
        Assert.Equal(TriggerDetector.Decision.None, d.OnKey(Key(VK.SPACE, true)));  // auto-repeat ignored
        Assert.Equal(TriggerDetector.Decision.None, d.OnKey(Key(VK.SPACE, false)));
        Assert.Equal(TriggerDetector.Decision.Activate, d.OnKey(Key(VK.SPACE, true))); // second press toggles
        Assert.Equal(TriggerDetector.Decision.LeftButtonReleased, d.OnMouse(Left(false)));
    }

    [Fact]
    public void Space_without_left_button_does_nothing()
    {
        var d = new TriggerDetector(TriggerSpec.Parse("space"));
        Assert.Equal(TriggerDetector.Decision.None, d.OnKey(Key(VK.SPACE, true)));
    }

    [Fact]
    public void Ctrl_space_requires_ctrl_and_plain_space_does_not_fire()
    {
        var d = new TriggerDetector(TriggerSpec.Parse("ctrl+space"));
        d.OnMouse(Left(true));
        Assert.Equal(TriggerDetector.Decision.None, d.OnKey(Key(VK.SPACE, true)));
        d.OnKey(Key(VK.SPACE, false));
        d.OnKey(Key(VK.LCONTROL, true));
        Assert.Equal(TriggerDetector.Decision.Activate, d.OnKey(Key(VK.SPACE, true)));
    }

    [Fact]
    public void Plain_space_trigger_does_not_fire_when_ctrl_is_held()
    {
        var d = new TriggerDetector(TriggerSpec.Parse("space"));
        d.OnMouse(Left(true));
        d.OnKey(Key(VK.CONTROL, true));
        Assert.Equal(TriggerDetector.Decision.None, d.OnKey(Key(VK.SPACE, true)));
    }

    [Fact]
    public void Middle_button_trigger_fires_only_while_left_is_held()
    {
        var d = new TriggerDetector(TriggerSpec.Parse("mbutton"));
        Assert.Equal(TriggerDetector.Decision.None, d.OnMouse(new MouseEvent(MouseButton.Middle, true, 0, 0, false, 0)));
        d.OnMouse(new MouseEvent(MouseButton.Middle, false, 0, 0, false, 0));
        d.OnMouse(Left(true));
        Assert.Equal(TriggerDetector.Decision.Activate, d.OnMouse(new MouseEvent(MouseButton.Middle, true, 0, 0, false, 0)));
    }

    [Fact]
    public void Escape_is_reported()
    {
        var d = new TriggerDetector(TriggerSpec.Parse("space"));
        Assert.Equal(TriggerDetector.Decision.Escape, d.OnKey(Key(VK.ESCAPE, true)));
    }

    [Fact]
    public void Trigger_key_is_only_flagged_for_swallowing_while_left_is_down()
    {
        var d = new TriggerDetector(TriggerSpec.Parse("space"));
        Assert.False(d.IsTriggerKey(Key(VK.SPACE, true)));
        d.OnMouse(Left(true));
        Assert.True(d.IsTriggerKey(Key(VK.SPACE, true)));
        Assert.False(d.IsTriggerKey(Key('A', true)));
    }
}

public class InteractionSessionTests
{
    // Release must NOT hide immediately: the hook sees the button-up before the drag loop does, and hiding
    // the window makes OLE send DragLeave instead of Drop (Spike 001, run 3). The overlay waits for the drop.
    [Fact]
    public void Release_with_drag_inside_waits_for_drop_without_short_timer_and_ignores_esc()
    {
        var s = new InteractionSession(requireDrag: true);
        Assert.Equal(SessionCommand.None, s.Apply(TriggerDetector.Decision.DragStarted, true, true));
        Assert.Equal(SessionCommand.ShowOverlay, s.Apply(TriggerDetector.Decision.Activate, true, true));
        Assert.Equal(SessionState.OverlayShown, s.State);
        s.NotifyDragEntered();
        Assert.Equal(SessionCommand.AwaitDrop, s.Apply(TriggerDetector.Decision.LeftButtonReleased, false, false));
        Assert.Equal(SessionState.AwaitingDrop, s.State);
        Assert.Equal(SessionCommand.None, s.Apply(TriggerDetector.Decision.Activate, false, false)); // ignored while awaiting
        Assert.Equal(SessionCommand.None, s.Apply(TriggerDetector.Decision.Escape, false, false));   // Esc must not pull the window away
        Assert.Equal(SessionState.AwaitingDrop, s.State);
        s.NotifyDropHandled();
        Assert.Equal(SessionState.Idle, s.State);
        Assert.False(s.DragInside);
        Assert.Equal(SessionCommand.None, s.NotifyGraceExpired()); // late timer is harmless
    }

    [Fact]
    public void Release_with_no_drag_inside_uses_short_grace()
    {
        var s = new InteractionSession();
        s.Apply(TriggerDetector.Decision.DragStarted, true, true);
        s.Apply(TriggerDetector.Decision.Activate, true, true);
        // DragEnter never arrived (non-OLE drag, or the shell cancelled the drag as with the middle button)
        Assert.Equal(SessionCommand.AwaitDropGrace, s.Apply(TriggerDetector.Decision.LeftButtonReleased, false, false));
        Assert.Equal(SessionCommand.HideOverlay, s.NotifyGraceExpired());
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Equal(SessionCommand.None, s.NotifyGraceExpired());
    }

    [Fact]
    public void Drag_leave_before_release_means_grace_and_drag_leave_after_release_hides()
    {
        var s = new InteractionSession();
        s.Apply(TriggerDetector.Decision.DragStarted, true, true);
        s.Apply(TriggerDetector.Decision.Activate, true, true);
        s.NotifyDragEntered();
        Assert.Equal(SessionCommand.None, s.NotifyDragLeft());          // cursor left the ring mid-drag: stay visible
        Assert.Equal(SessionState.OverlayShown, s.State);
        Assert.False(s.DragInside);
        Assert.Equal(SessionCommand.AwaitDropGrace, s.Apply(TriggerDetector.Decision.LeftButtonReleased, false, false)); // released outside

        var t = new InteractionSession();
        t.Apply(TriggerDetector.Decision.DragStarted, true, true);
        t.Apply(TriggerDetector.Decision.Activate, true, true);
        t.NotifyDragEntered();
        Assert.Equal(SessionCommand.AwaitDrop, t.Apply(TriggerDetector.Decision.LeftButtonReleased, false, false));
        Assert.Equal(SessionCommand.HideOverlay, t.NotifyDragLeft());   // terminal DragLeave: drop went elsewhere
        Assert.Equal(SessionState.Idle, t.State);
    }

    [Fact]
    public void Drop_arriving_after_watchdog_does_not_corrupt_state()
    {
        var s = new InteractionSession();
        s.Apply(TriggerDetector.Decision.DragStarted, true, true);
        s.Apply(TriggerDetector.Decision.Activate, true, true);
        s.NotifyDragEntered();
        s.Apply(TriggerDetector.Decision.LeftButtonReleased, false, false);
        Assert.Equal(SessionCommand.HideOverlay, s.NotifyGraceExpired()); // watchdog
        s.NotifyDropHandled();                                             // late Drop
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Equal(SessionCommand.None, s.NotifyDragLeft());
    }

    [Fact]
    public void Activate_before_drag_threshold_is_ignored_when_drag_required()
    {
        var s = new InteractionSession(requireDrag: true);
        Assert.Equal(SessionCommand.None, s.Apply(TriggerDetector.Decision.Activate, true, false));
        var s2 = new InteractionSession(requireDrag: false);
        Assert.Equal(SessionCommand.ShowOverlay, s2.Apply(TriggerDetector.Decision.Activate, true, false));
    }

    [Fact]
    public void Second_activate_toggles_off_and_escape_hides()
    {
        var s = new InteractionSession();
        s.Apply(TriggerDetector.Decision.DragStarted, true, true);
        s.Apply(TriggerDetector.Decision.Activate, true, true);
        Assert.Equal(SessionCommand.HideOverlay, s.Apply(TriggerDetector.Decision.Activate, true, true));
        Assert.Equal(SessionCommand.ShowOverlay, s.Apply(TriggerDetector.Decision.Activate, true, true));
        Assert.Equal(SessionCommand.HideOverlay, s.Apply(TriggerDetector.Decision.Escape, true, true));
        Assert.Equal(SessionState.Dragging, s.State);
    }

    [Fact]
    public void Release_without_overlay_is_a_no_op()
    {
        var s = new InteractionSession();
        Assert.Equal(SessionCommand.None, s.Apply(TriggerDetector.Decision.LeftButtonReleased, false, false));
    }
}
