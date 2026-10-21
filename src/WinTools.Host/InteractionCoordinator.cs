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
    private (int X, int Y) _anchor;

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
                _log.Event("overlay.show", new { hookToShownMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3), anchor = $"{_anchor.X},{_anchor.Y}", centre = $"{placed.CentreX},{placed.CentreY}", dpi = placed.Dpi });
                break;
            case SessionAction.HideOverlay:
                _window.StopTimer(SessionTimer);
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
        var first = _navigator is null;
        // One menu per overlay session: a drag that leaves and comes back keeps the open category and its timing.
        _navigator ??= new MenuNavigator(RadialMenu.Build(_resolver.Resolve(paths)), _dpi);
        var armed = Track(dx, dy);
        Apply(action, "dragEnterAfterRelease");
        if (first)
            _log.Event("drag.enter", new { files = paths.Count, groups = _navigator.Groups.Select(g => $"{g.Label}:{g.Commands.Count}"), hookToEnterMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3) });
        else
            _log.Event("drag.reenter");
        return armed;
    }

    bool IOverlayEvents.DragMoved(int dx, int dy) => _navigator is not null && Track(dx, dy);

    /// <summary>Feeds the pointer to the navigator, repaints if the view changed, and says whether a drop now would run.</summary>
    private bool Track(int dx, int dy)
    {
        var nav = _navigator!;
        var now = Stopwatch.GetElapsedTime(_shownTimestamp);
        var before = nav.Active;
        nav.Move(dx, dy, now);
        if (nav.Active != before) _log.Event("menu.open", new { group = nav.Active!.Label, msSinceShown = Math.Round(now.TotalMilliseconds, 1) });
        _overlay.SetView(nav.View(now));
        return nav.Armed(now) is not null;
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
            nav.Move(dx, dy, now);
            chosen = nav.Armed(now);
            hot = nav.Hot;
            openMs = Math.Round(nav.ActiveFor(now).TotalMilliseconds, 1);
        }
        var execute = _session.OnDrop(chosen is null ? -1 : 0, 1);
        _window.StopTimer(SessionTimer);
        _overlay.Hide();
        if (execute is null || chosen is null)
        {
            var reason = !copyAllowed ? "copyNotAllowed" : hot is not null ? "notArmed" : "noCommand";
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
