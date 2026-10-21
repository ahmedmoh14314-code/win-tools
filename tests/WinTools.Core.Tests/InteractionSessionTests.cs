using WinTools.Core.Interaction;

namespace WinTools.Core.Tests;

public sealed class InteractionSessionTests
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(2);

    private static InteractionSession Shown()
    {
        var s = new InteractionSession();
        Assert.Equal(SessionAction.ShowOverlay, s.OnTrigger(leftDown: true, dragging: true, Fresh));
        return s;
    }

    [Fact]
    public void Trigger_during_a_drag_shows_the_overlay()
    {
        var s = Shown();
        Assert.Equal(SessionState.OverlayShown, s.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Trigger_without_a_left_button_drag_does_nothing(bool leftDown, bool dragging)
    {
        var s = new InteractionSession();
        Assert.Equal(SessionAction.None, s.OnTrigger(leftDown, dragging, Fresh));
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void Stale_trigger_is_ignored()
    {
        var s = new InteractionSession();
        Assert.Equal(SessionAction.None, s.OnTrigger(true, true, InteractionSession.MaxTriggerAge + TimeSpan.FromMilliseconds(1)));
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void Second_trigger_while_shown_is_ignored()
    {
        var s = Shown();
        Assert.Equal(SessionAction.None, s.OnTrigger(true, true, Fresh));
        Assert.Equal(SessionState.OverlayShown, s.State);
    }

    [Fact]
    public void Release_after_DragEnter_keeps_the_overlay_and_waits_for_the_terminal_OLE_call()
    {
        var s = Shown();
        _ = s.OnDragEntered();
        Assert.Equal(SessionAction.StartDropWatchdog, s.OnLeftButtonReleased());
        Assert.Equal(SessionState.AwaitingDrop, s.State); // not hidden: the Spike 001 lost-drop bug must not return
        Assert.Equal(1, s.OnDrop(sector: 1, commandCount: 2));
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void DragEnter_after_the_release_replaces_the_grace_with_the_drop_watchdog()
    {
        var s = Shown();
        Assert.Equal(SessionAction.StartReleaseGrace, s.OnLeftButtonReleased());
        Assert.Equal(SessionAction.StartDropWatchdog, s.OnDragEntered()); // OLE now owes Drop or DragLeave
        Assert.Equal(SessionAction.None, s.OnDragEntered());              // a second enter changes nothing
        Assert.Equal(0, s.OnDrop(0, 1));
    }

    [Fact]
    public void DragEnter_before_the_release_needs_no_timer()
    {
        var s = Shown();
        Assert.Equal(SessionAction.None, s.OnDragEntered());
    }

    [Fact]
    public void Release_without_DragEnter_starts_the_short_grace()
    {
        var s = Shown();
        Assert.Equal(SessionAction.StartReleaseGrace, s.OnLeftButtonReleased());
        Assert.Equal(SessionAction.HideOverlay, s.OnTimerExpired());
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void Drop_can_arrive_before_the_hook_reports_the_release()
    {
        var s = Shown();
        _ = s.OnDragEntered();
        Assert.Equal(0, s.OnDrop(0, 1));
        Assert.Equal(SessionAction.None, s.OnLeftButtonReleased()); // the late button-up changes nothing
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void DragLeave_after_release_hides_and_runs_nothing()
    {
        var s = Shown();
        _ = s.OnDragEntered();
        s.OnLeftButtonReleased();
        Assert.Equal(SessionAction.HideOverlay, s.OnDragLeft());
        Assert.Null(s.OnDrop(0, 1));
    }

    [Fact]
    public void DragLeave_while_still_dragging_keeps_the_overlay()
    {
        var s = Shown();
        _ = s.OnDragEntered();
        Assert.Equal(SessionAction.None, s.OnDragLeft());
        Assert.Equal(SessionState.OverlayShown, s.State);
        Assert.False(s.DragInside);
        Assert.Equal(SessionAction.StartReleaseGrace, s.OnLeftButtonReleased()); // released outside: only the grace
    }

    [Fact]
    public void Escape_while_shown_hides()
    {
        var s = Shown();
        Assert.Equal(SessionAction.HideOverlay, s.OnEscape());
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void Escape_after_release_is_ignored_because_a_drop_may_be_in_flight()
    {
        var s = Shown();
        _ = s.OnDragEntered();
        s.OnLeftButtonReleased();
        Assert.Equal(SessionAction.None, s.OnEscape());
        Assert.Equal(SessionState.AwaitingDrop, s.State);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    public void Drop_outside_a_command_runs_nothing(int sector, int commands)
    {
        var s = Shown();
        _ = s.OnDragEntered();
        Assert.Null(s.OnDrop(sector, commands));
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void Drop_when_idle_runs_nothing()
    {
        Assert.Null(new InteractionSession().OnDrop(0, 1));
    }

    [Fact]
    public void Watchdog_ends_a_session_whose_source_never_finishes()
    {
        var s = Shown();
        _ = s.OnDragEntered();
        s.OnLeftButtonReleased();
        Assert.Equal(SessionAction.HideOverlay, s.OnTimerExpired());
        Assert.Equal(SessionAction.None, s.OnTimerExpired());
        Assert.Equal(SessionAction.ShowOverlay, s.OnTrigger(true, true, Fresh)); // usable again
    }

    [Fact]
    public void Timer_while_shown_and_dragging_does_nothing()
    {
        var s = Shown();
        Assert.Equal(SessionAction.None, s.OnTimerExpired());
        Assert.Equal(SessionState.OverlayShown, s.State);
    }
}
