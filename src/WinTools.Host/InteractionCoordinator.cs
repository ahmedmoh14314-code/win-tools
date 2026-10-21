using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Interaction;
using WinTools.Host.Jobs;
using WinTools.Windows;
using WinTools.Windows.Input;
using WinTools.Windows.Overlay;

namespace WinTools.Host;

/// <summary>
/// Connects input, session, overlay, capabilities and jobs on the UI thread. It translates events into calls on
/// <see cref="InteractionSession"/> and carries out the actions the session returns; the rules live in the session.
/// </summary>
internal sealed class InteractionCoordinator : IOverlayEvents, IDisposable
{
    private const nuint SessionTimer = 1;
    private const nuint ArmTimer = 2; // repaints a hovered command as armed when the arm delay has passed

    private readonly HostLog _log;
    private readonly JobRunner _jobs;
    private readonly CapabilityResolver _resolver;
    private readonly InteractionSession _session = new();
    private readonly MessageWindow _window;
    private readonly RadialOverlay _overlay;
    private readonly LowLevelHooks _hooks;
    private MenuNavigator? _navigator; // built on the first DragEnter of each overlay session
    private long _triggerTimestamp, _shownTimestamp;
    private uint _dpi = 96;
    private (int X, int Y) _anchor, _centre;

    /// <param name="catalog">The operations the menu may offer (<see cref="RadialMenu.Catalog"/>).</param>
    public InteractionCoordinator(HostLog log, JobRunner jobs, int triggerVirtualKey, IReadOnlyList<Operation> catalog)
    {
        _log = log;
        _jobs = jobs;
        _resolver = new CapabilityResolver(catalog);
        _window = new MessageWindow(OnMessage);
        _overlay = new RadialOverlay(this);
        _hooks = new LowLevelHooks(_window.Handle, triggerVirtualKey);
    }

    public void Start() => _hooks.Start();

    public void RequestClose() => _window.RequestClose();

    private bool OnMessage(uint msg, nuint wParam, nint lParam)
    {
        if (msg == LowLevelHooks.SignalMessage)
        {
            var s = LowLevelHooks.Unpack(wParam);
            OnSignal(s.Kind, s.LeftDown, s.Dragging, s.X, s.Y, lParam);
            return true;
        }
        if (MessageWindow.IsTimer(msg) && wParam == ArmTimer)
        {
            _window.StopTimer(ArmTimer);
            if (_navigator is { } nav && _overlay.IsVisible) _overlay.SetView(nav.View(Stopwatch.GetElapsedTime(_shownTimestamp)));
            return true;
        }
        if (MessageWindow.IsTimer(msg) && wParam == SessionTimer)
        {
            _window.StopTimer(SessionTimer);
            Apply(_session.OnTimerExpired(), "timer");
            return true;
        }
        return false;
    }

    private void OnSignal(InputSignal signal, bool leftDown, bool dragging, int x, int y, long hookTimestamp)
    {
        switch (signal)
        {
            case InputSignal.Trigger:
                var age = Stopwatch.GetElapsedTime(hookTimestamp);
                var action = _session.OnTrigger(leftDown, dragging, age);
                if (action == SessionAction.ShowOverlay) { _triggerTimestamp = hookTimestamp; _anchor = (x, y); }
                Apply(action, "trigger"); // show first; logging is not on the latency path
                _log.Event("trigger", new { leftDown, dragging, hookToUiMs = Math.Round(age.TotalMilliseconds, 3), action = action.ToString() });
                break;
            case InputSignal.LeftButtonReleased:
                Apply(_session.OnLeftButtonReleased(), "release");
                break;
            case InputSignal.Escape:
                Apply(_session.OnEscape(), "escape");
                break;
        }
    }

    private void Apply(SessionAction action, string reason)
    {
        switch (action)
        {
            case SessionAction.ShowOverlay:
                _navigator = null;
                var placed = _overlay.ShowAt(_anchor.X, _anchor.Y);
                _shownTimestamp = Stopwatch.GetTimestamp();
                _dpi = placed.Dpi;
                _centre = (placed.CentreX, placed.CentreY);
                _log.Event("overlay.show", new { hookToShownMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3), anchor = $"{_anchor.X},{_anchor.Y}", centre = $"{placed.CentreX},{placed.CentreY}", dpi = placed.Dpi });
                break;
            case SessionAction.HideOverlay:
                _window.StopTimer(SessionTimer);
                _window.StopTimer(ArmTimer);
                _overlay.Hide();
                _log.Event("overlay.hide", new { reason });
                break;
            case SessionAction.StartReleaseGrace:
                _window.StartTimer(SessionTimer, InteractionSession.ReleaseGrace);
                _log.Event("release", new { wait = "grace", dragInside = false });
                break;
            case SessionAction.StartDropWatchdog:
                _window.StartTimer(SessionTimer, InteractionSession.DropWatchdog);
                _log.Event("release", new { wait = "drop", dragInside = true });
                break;
        }
    }

    bool IOverlayEvents.DragEntered(IReadOnlyList<string> paths, int dx, int dy)
    {
        var action = _session.OnDragEntered();
        // One menu per overlay session, so a drag that leaves and comes back finds the menu where it left it. A first
        // DragEnter that brought no readable files does not fix an empty menu for the rest of the session.
        var first = _navigator is null || (_navigator.Groups.Count == 0 && paths.Count > 0);
        if (first)
        {
            _navigator = new MenuNavigator(RadialMenu.Build(_resolver.Resolve(paths)), _dpi);
            // The first ring was centred on the pointer at the trigger (unless clamped at an edge): that is the start.
            _navigator.Shown(_anchor.X - _centre.X, _anchor.Y - _centre.Y, TimeSpan.Zero, _dpi);
        }
        var hot = Track(dx, dy);
        Apply(action, "dragEnterAfterRelease");
        if (first)
            _log.Event("drag.enter", new { files = paths.Count, groups = _navigator!.Groups.Select(g => $"{g.Label}:{g.Commands.Count}"), hookToEnterMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3) });
        else
            _log.Event("drag.reenter");
        return hot;
    }

    bool IOverlayEvents.DragMoved(int dx, int dy) => _navigator is not null && Track(dx, dy);

    /// <summary>
    /// Feeds the pointer to the navigator. When the menu changes level, the ring is re-centred on the pointer and the
    /// arm timer started. Repaints if the view changed. Returns whether a command is under the pointer: the drop
    /// effect is COPY there even before the arm delay has passed, so the drop always reaches the overlay and is judged
    /// at the moment it happens (a release before the delay runs nothing), whatever OLE's DragOver timing.
    /// </summary>
    private bool Track(int dx, int dy)
    {
        var nav = _navigator!;
        var now = Stopwatch.GetElapsedTime(_shownTimestamp);
        if (nav.Move(dx, dy, now))
        {
            // A DragOver already in flight when the overlay was hidden must not bring it back.
            if (!_overlay.IsVisible || _session.State == SessionState.Idle) return false;
            var (px, py) = (_centre.X + dx, _centre.Y + dy);
            var placed = _overlay.MoveTo(px, py);
            _centre = (placed.CentreX, placed.CentreY);
            now = Stopwatch.GetElapsedTime(_shownTimestamp);
            nav.Shown(px - _centre.X, py - _centre.Y, now, placed.Dpi);
            _window.StartTimer(ArmTimer, MenuNavigator.ArmDelay + TimeSpan.FromMilliseconds(5));
            _log.Event(nav.Open is { } g ? "menu.open" : "menu.back", new { group = nav.Open?.Label, centre = $"{_centre.X},{_centre.Y}", dpi = placed.Dpi, msSinceShown = Math.Round(now.TotalMilliseconds, 1) });
        }
        _overlay.SetView(nav.View(now));
        return nav.Hot is not null;
    }

    void IOverlayEvents.DragLeft()
    {
        _log.Event("drag.leave");
        Apply(_session.OnDragLeft(), "dragLeave");
    }

    bool IOverlayEvents.Dropped(IReadOnlyList<string> paths, int dx, int dy, bool copyAllowed)
    {
        var now = Stopwatch.GetElapsedTime(_shownTimestamp);
        Operation? chosen = null, hot = null;
        var openMs = 0.0;
        if (copyAllowed && _navigator is { } nav)
        {
            if (!nav.Move(dx, dy, now)) // a drop deep in a category or Back changes the level: that is not a command
            {
                chosen = nav.Armed(now);
                hot = nav.Hot;
            }
            openMs = Math.Round(nav.ShownFor(now).TotalMilliseconds, 1);
        }
        var execute = _session.OnDrop(chosen is null ? -1 : 0, 1);
        _window.StopTimer(SessionTimer);
        _window.StopTimer(ArmTimer);
        _overlay.Hide();
        if (execute is null || chosen is null)
        {
            var reason = !copyAllowed ? "copyNotAllowed" : hot is not null ? "notArmed" : "noCommand";
            if (reason == "notArmed") MessageWindow.InfoBeep(); // released on a command too early: say that nothing ran
            _log.Event("drop", new { accepted = false, reason, hot = hot?.Id, openMs, files = paths.Count, at = $"{dx},{dy}" });
            return false;
        }
        // Re-resolve from the drop's own data object; run only if it still offers the command that was armed.
        if (!_resolver.Resolve(paths).Any(o => o.Id == chosen.Id))
        {
            _log.Event("drop", new { accepted = false, reason = "commandsChanged", files = paths.Count });
            return false;
        }
        _log.Event("drop", new { accepted = true, op = chosen.Id, openMs, files = paths.Count });
        _ = _jobs.Start(chosen, paths);
        return true;
    }

    public void Dispose()
    {
        _hooks.Dispose();
        _overlay.Dispose();
        _window.Dispose();
    }
}
