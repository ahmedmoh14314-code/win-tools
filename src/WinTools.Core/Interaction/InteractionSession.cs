namespace WinTools.Core.Interaction;

public enum SessionState { Idle, OverlayShown, AwaitingDrop }

/// <summary>What the host must do after feeding an event to the session.</summary>
public enum SessionAction { None, ShowOverlay, HideOverlay, StartReleaseGrace, StartDropWatchdog }

/// <summary>
/// The drag → trigger → drop state machine, independent of any window technology (Spike 001/002 rules).
/// <list type="bullet">
/// <item>The overlay appears only when the trigger arrives while a left-button drag is in progress.</item>
/// <item>The low-level hook sees the button-up before the drag loop delivers Drop. If DragEnter already reached the
/// overlay, the release does not hide it: OLE owes a terminal Drop or DragLeave, and a long watchdog only guards a
/// dead source. Without DragEnter, a short grace covers a DragEnter/Drop still in flight.</item>
/// <item>Escape hides the overlay while it is shown, but not after the release: a Drop may already be in flight.</item>
/// <item>A drop executes a command only on a valid sector; anywhere else it reports "nothing dropped".</item>
/// </list>
/// Single-threaded: every call comes from the overlay thread.
/// </summary>
public sealed class InteractionSession
{
    /// <summary>A trigger that reaches the UI thread later than this is stale (the user has moved on) and is ignored.</summary>
    public static readonly TimeSpan MaxTriggerAge = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan ReleaseGrace = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan DropWatchdog = TimeSpan.FromSeconds(5);

    private bool _dragInside;

    public SessionState State { get; private set; } = SessionState.Idle;
    public bool DragInside => _dragInside;

    /// <param name="leftDown">Left button down at the time the hook saw the trigger.</param>
    /// <param name="dragging">The pointer had moved past the drag threshold with the button down.</param>
    /// <param name="age">Time from the hook callback to this call.</param>
    public SessionAction OnTrigger(bool leftDown, bool dragging, TimeSpan age)
    {
        if (State != SessionState.Idle) return SessionAction.None;
        if (!leftDown || !dragging || age > MaxTriggerAge) return SessionAction.None;
        State = SessionState.OverlayShown;
        _dragInside = false;
        return SessionAction.ShowOverlay;
    }

    public SessionAction OnEscape()
    {
        if (State != SessionState.OverlayShown) return SessionAction.None;
        Reset();
        return SessionAction.HideOverlay;
    }

    public SessionAction OnLeftButtonReleased()
    {
        if (State != SessionState.OverlayShown) return SessionAction.None;
        State = SessionState.AwaitingDrop;
        return _dragInside ? SessionAction.StartDropWatchdog : SessionAction.StartReleaseGrace;
    }

    /// <summary>
    /// OLE DragEnter. If it arrives after the release (the drag loop was still on its way to the overlay), the short
    /// grace is replaced by the drop watchdog: OLE now owes Drop or DragLeave, and hiding early would let the drop land
    /// on the window underneath.
    /// </summary>
    public SessionAction OnDragEntered()
    {
        if (State == SessionState.Idle) return SessionAction.None;
        var wasInside = _dragInside;
        _dragInside = true;
        return State == SessionState.AwaitingDrop && !wasInside ? SessionAction.StartDropWatchdog : SessionAction.None;
    }

    public SessionAction OnDragLeft()
    {
        _dragInside = false;
        if (State != SessionState.AwaitingDrop) return SessionAction.None; // still dragging: the user may come back
        Reset();
        return SessionAction.HideOverlay;
    }

    /// <summary>OLE Drop on the overlay. Returns the sector to execute, or null when nothing must run.</summary>
    /// <param name="sector">Sector under the drop point, -1 for the dead zone or outside the ring.</param>
    /// <param name="commandCount">Number of commands the overlay showed.</param>
    public int? OnDrop(int sector, int commandCount)
    {
        if (State == SessionState.Idle) return null;
        Reset();
        return sector >= 0 && sector < commandCount ? sector : null;
    }

    /// <summary>The release grace or the drop watchdog fired.</summary>
    public SessionAction OnTimerExpired()
    {
        if (State != SessionState.AwaitingDrop) return SessionAction.None;
        Reset();
        return SessionAction.HideOverlay;
    }

    private void Reset()
    {
        State = SessionState.Idle;
        _dragInside = false;
    }
}
