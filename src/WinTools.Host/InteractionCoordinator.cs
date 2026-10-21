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
    private readonly CapabilityResolver _resolver = CapabilityResolver.Default;
    private readonly InteractionSession _session = new();
    private readonly MessageWindow _window;
    private readonly RadialOverlay _overlay;
    private readonly LowLevelHooks _hooks;
    private IReadOnlyList<Operation> _commands = [];
    private long _triggerTimestamp;
    private (int X, int Y) _anchor;

    public InteractionCoordinator(HostLog log, JobRunner jobs)
    {
        _log = log;
        _jobs = jobs;
        _window = new MessageWindow(OnMessage);
        _overlay = new RadialOverlay(this);
        _hooks = new LowLevelHooks(_window.Handle);
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
                _commands = [];
                var centre = _overlay.ShowAt(_anchor.X, _anchor.Y);
                _log.Event("overlay.show", new { hookToShownMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3), anchor = $"{_anchor.X},{_anchor.Y}", centre = $"{centre.X},{centre.Y}" });
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

    void IOverlayEvents.DragEntered(IReadOnlyList<string> paths)
    {
        var action = _session.OnDragEntered();
        _commands = Shown(_resolver.Resolve(paths));
        _overlay.SetCommands(_commands.Select(c => c.Label).ToArray());
        Apply(action, "dragEnterAfterRelease");
        _log.Event("drag.enter", new { files = paths.Count, commands = _commands.Select(c => c.Id), hookToEnterMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3) });
    }

    void IOverlayEvents.DragLeft()
    {
        _log.Event("drag.leave");
        Apply(_session.OnDragLeft(), "dragLeave");
    }

    bool IOverlayEvents.Dropped(IReadOnlyList<string> paths, int sector, int dropX, int dropY)
    {
        var shown = _commands;
        var execute = _session.OnDrop(sector, shown.Count);
        _window.StopTimer(SessionTimer);
        _overlay.Hide();
        if (execute is not int index)
        {
            _log.Event("drop", new { sector, accepted = false, files = paths.Count, at = $"{dropX},{dropY}" });
            return false;
        }
        // Re-resolve from the drop's own data object; run only if it still offers the command that was shown.
        var now = Shown(_resolver.Resolve(paths));
        if (index >= now.Length || now[index] != shown[index])
        {
            _log.Event("drop", new { sector, accepted = false, files = paths.Count, reason = "commandsChanged" });
            return false;
        }
        _log.Event("drop", new { sector, accepted = true, op = now[index].Id, files = paths.Count });
        _ = _jobs.Start(now[index], paths);
        return true;
    }

    /// <summary>
    /// The flat ring shows conversions only. Resize, compress and rotate are complete in the worker, and reach the
    /// ring through the hierarchical menu (next slice): fourteen sectors on one ring are not usable.
    /// </summary>
    private static Operation[] Shown(IReadOnlyList<Operation> ops) => ops.Where(o => o.Kind == OperationKind.Convert).ToArray();

    public void Dispose()
    {
        _hooks.Dispose();
        _overlay.Dispose();
        _window.Dispose();
    }
}
