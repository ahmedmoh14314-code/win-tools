using Spike.Interaction.Core.Input;

namespace Spike.Interaction.Core.Session;

public enum SessionState { Idle, LeftDown, Dragging, OverlayShown, AwaitingDrop }

/// <summary>
/// AwaitDrop: the button was released while an OLE drag is inside the overlay; keep the overlay visible until
/// Drop or DragLeave arrives (the host may arm a long watchdog, never a short timer).
/// AwaitDropGrace: the button was released while no OLE drag is inside the overlay (never entered, or left);
/// keep the overlay visible for a short grace in case DragEnter/Drop is still in flight, then hide.
/// </summary>
public enum SessionCommand { None, ShowOverlay, HideOverlay, AwaitDrop, AwaitDropGrace }

/// <summary>
/// Pure state machine for one interaction: idle → left button down → drag → overlay shown → (button released:
/// awaiting the OLE drop) → hidden. It converts detector decisions into overlay commands and is independent of
/// any window system.
///
/// Why the post-release wait exists: the low-level mouse hook sees WM_LBUTTONUP *before* the drag source's
/// DoDragDrop loop processes it. Hiding the overlay at that moment changes the window under the cursor, so OLE
/// issues DragLeave and the Drop lands on whatever is underneath (with Explorer as source that is a real move
/// or copy). Once DragEnter has been received OLE guarantees a terminal Drop or DragLeave, so after a release
/// *inside* the overlay the session waits for that terminal call with no short timer. Only when no drag is
/// inside (DragEnter never arrived, or DragLeave came before the release) is a short grace used.
/// Esc after the release is ignored while a drop is pending for the same reason.
/// </summary>
public sealed class InteractionSession
{
    private readonly bool _requireDrag;
    private bool _dragInside;

    public InteractionSession(bool requireDrag = true) => _requireDrag = requireDrag;

    public SessionState State { get; private set; } = SessionState.Idle;

    /// <summary>True between OLE DragEnter and DragLeave/Drop on the overlay.</summary>
    public bool DragInside => _dragInside;

    public SessionCommand Apply(TriggerDetector.Decision decision, bool leftDown, bool dragging)
    {
        switch (decision)
        {
            case TriggerDetector.Decision.DragStarted:
                if (State is SessionState.Idle or SessionState.LeftDown) State = SessionState.Dragging;
                return SessionCommand.None;

            case TriggerDetector.Decision.Activate:
                if (State == SessionState.AwaitingDrop) return SessionCommand.None;
                if (State == SessionState.OverlayShown) { State = SessionState.Dragging; _dragInside = false; return SessionCommand.HideOverlay; } // toggle
                if (!leftDown) return SessionCommand.None;
                if (_requireDrag && !dragging) return SessionCommand.None;
                State = SessionState.OverlayShown;
                _dragInside = false;
                return SessionCommand.ShowOverlay;

            case TriggerDetector.Decision.LeftButtonReleased:
                if (State == SessionState.OverlayShown)
                {
                    State = SessionState.AwaitingDrop;
                    return _dragInside ? SessionCommand.AwaitDrop : SessionCommand.AwaitDropGrace;
                }
                if (State == SessionState.AwaitingDrop) return SessionCommand.None;
                State = SessionState.Idle;
                return SessionCommand.None;

            case TriggerDetector.Decision.Escape:
                if (State == SessionState.OverlayShown) { State = leftDown ? SessionState.Dragging : SessionState.Idle; _dragInside = false; return SessionCommand.HideOverlay; }
                // After the release a Drop may be in flight; Esc must not pull the window away from under it.
                if (State == SessionState.AwaitingDrop) return SessionCommand.None;
                return SessionCommand.None;

            default:
                if (State == SessionState.Idle && leftDown) State = SessionState.LeftDown;
                return SessionCommand.None;
        }
    }

    /// <summary>OLE DragEnter arrived on the overlay.</summary>
    public void NotifyDragEntered() => _dragInside = true;

    /// <summary>Called by the overlay after a drop completes so the session returns to idle.</summary>
    public void NotifyDropHandled() { _dragInside = false; State = SessionState.Idle; }

    /// <summary>OLE DragLeave arrived. After the release it is the terminal call: the drop went elsewhere, hide.</summary>
    public SessionCommand NotifyDragLeft()
    {
        _dragInside = false;
        if (State != SessionState.AwaitingDrop) return SessionCommand.None;
        State = SessionState.Idle;
        return SessionCommand.HideOverlay;
    }

    /// <summary>The post-release grace (no drag inside) or the watchdog (drag inside, source never finished) elapsed.</summary>
    public SessionCommand NotifyGraceExpired()
    {
        if (State != SessionState.AwaitingDrop) return SessionCommand.None;
        State = SessionState.Idle;
        _dragInside = false;
        return SessionCommand.HideOverlay;
    }
}
