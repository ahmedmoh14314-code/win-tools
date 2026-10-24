using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Settings;
using WinTools.Host.Jobs;
using WinTools.Windows;
using WinTools.Windows.Feedback;
using WinTools.Windows.Input;
using WinTools.Windows.Overlay;

namespace WinTools.Host;

/// <summary>
/// Connects input, session, overlay, capabilities and jobs on the UI thread. It translates events into calls on
/// <see cref="InteractionSession"/> and carries out the actions the session returns; the rules live in the session.
/// It also plays the navigation sounds (<see cref="NavigationSounds"/> decides which) and tells the feedback layer where
/// the user dropped.
/// </summary>
internal sealed class InteractionCoordinator : IOverlayEvents, IDisposable
{
    private const nuint SessionTimer = 1;
    private const nuint ArmTimer = 2; // repaints a hovered command as armed when the arm delay has passed

    private readonly HostLog _log;
    private readonly JobScheduler _jobs;
    private readonly FeedbackPresenter _feedback;
    private readonly SoundPlayer _sounds;
    private readonly NavigationSounds _navigationSounds;
    private CapabilityResolver _resolver;
    private CapabilityResolver _sessionResolver; // fixed for one overlay session, so a settings reload cannot change an armed menu
    private AppSettings _settings;
    private readonly InteractionSession _session = new();
    private readonly MessageWindow _window;
    private readonly RadialOverlay _overlay;
    private readonly LowLevelHooks _hooks;
    private MenuNavigator? _navigator; // built on the first DragEnter of each overlay session
    private long _triggerTimestamp, _shownTimestamp;
    private uint _dpi = 96;
    private (int X, int Y) _anchor, _centre;

    public InteractionCoordinator(HostLog log, MessageWindow window, JobScheduler jobs, FeedbackPresenter feedback, SoundPlayer sounds, AppSettings settings)
    {
        _log = log;
        _jobs = jobs;
        _feedback = feedback;
        _sounds = sounds;
        _settings = settings;
        _navigationSounds = new NavigationSounds(settings.UiSounds);
        _resolver = CapabilityResolver.For(settings.CustomResizeId());
        _sessionResolver = _resolver;
        _window = window;
        _overlay = new RadialOverlay(this);
        _feedback.AttachOverlay(() => _overlay.IsVisible, _overlay.BringToFront);
        _overlay.RenderFailed = hidden =>
        {
            _log.Event("overlay.renderFailed", new { hidden });
            if (hidden) _feedback.OverlayHidden();
        };
        _hooks = new LowLevelHooks(_window.Handle, settings.TriggerVirtualKey);
    }

    /// <summary>
    /// Applies changed settings at once (UI thread): the trigger key, the menu's custom size, the sounds and the motion. A session
    /// in progress keeps its menu; the next one uses the new commands.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _hooks.SetTriggerKey(settings.TriggerVirtualKey);
        _resolver = CapabilityResolver.For(settings.CustomResizeId());
        _navigationSounds.Enabled = settings.UiSounds;
    }

    /// <summary>The message window this coordinator runs on (owned by the composition root).</summary>
    public MessageWindow Window => _window;

    public void Start() => _hooks.Start();

    public void RequestClose() => _window.RequestClose();

    /// <summary>Messages of the message window (hook signals and timers); the composition root routes them here.</summary>
    internal bool OnMessage(uint msg, nuint wParam, nint lParam)
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
            case InputSignal.TriggerRefused:
                // Alt was held: Alt+Space is the window-menu shortcut. Say so instead of silently doing nothing.
                var refusedAge = Stopwatch.GetElapsedTime(hookTimestamp);
                var tell = _session.State == SessionState.Idle && refusedAge <= InteractionSession.MaxTriggerAge;
                if (tell) _feedback.TriggerRefused(_settings.TriggerKey, x, y);
                _log.Event("trigger.refused", new { modifier = "Alt", leftDown, dragging, overlay = _session.State.ToString(), told = tell, hookToUiMs = Math.Round(refusedAge.TotalMilliseconds, 3) });
                break;
        }
    }

    private void Apply(SessionAction action, string reason)
    {
        switch (action)
        {
            case SessionAction.ShowOverlay:
                _navigator = null;
                _sessionResolver = _resolver;
                _navigationSounds.Reset();
                _feedback.OverlayShowing();
                _overlay.ReducedMotion = _settings.ReducedMotion(SystemPreferences.ReducedMotion()); // Windows' "Animation effects" unless overridden
                var placed = _overlay.ShowAt(_anchor.X, _anchor.Y);
                _shownTimestamp = Stopwatch.GetTimestamp();
                _dpi = placed.Dpi;
                _centre = (placed.CentreX, placed.CentreY);
                _log.Event("overlay.show", new { hookToShownMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3), anchor = $"{_anchor.X},{_anchor.Y}", centre = $"{placed.CentreX},{placed.CentreY}", dpi = placed.Dpi });
                if (!_overlay.IsVisible) _log.Event("overlay.renderFailed"); // not shown: a ring with transparent holes would let the drag through
                break;
            case SessionAction.HideOverlay:
                _window.StopTimer(SessionTimer);
                _window.StopTimer(ArmTimer);
                _overlay.Hide();
                _feedback.OverlayHidden();
                _log.Event("overlay.hide", new { reason, sounds = SoundCounts(), frames = _overlay.Frames });
                break;
            case SessionAction.StartReleaseGrace:
                // Released outside the ring: the drop target forgets that drag's files, so a drag entering during the
                // grace never falls back to them. The menu stays: a late DragEnter of the same drag (a fast release) is
                // judged against the level on screen, and any drop is re-resolved from its own files.
                _overlay.ForgetDrag();
                _window.StartTimer(SessionTimer, InteractionSession.ReleaseGrace);
                _log.Event("release", new { wait = "grace", dragInside = false });
                break;
            case SessionAction.StartDropWatchdog:
                _window.StartTimer(SessionTimer, InteractionSession.DropWatchdog);
                _log.Event("release", new { wait = "drop", dragInside = true });
                break;
        }
    }

    bool IOverlayEvents.DragEntered(IReadOnlyList<string> paths, bool pending, int dx, int dy)
    {
        var action = _session.OnDragEntered();
        if (pending && (_navigator is null || _navigator.Groups.Count == 0))
        {
            // The files are not readable yet and the drop target is retrying: the ring stays in its waiting state (not
            // "No actions"), and nothing can be dropped on a command until FilesResolved builds the menu.
            _navigator = null;
            _overlay.ShowWaiting();
            Apply(action, "dragEnterAfterRelease");
            _log.Event("drag.enter", new { files = 0, pending = true, hookToEnterMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3) });
            return false;
        }
        // The first ring was centred on the pointer at the trigger (unless clamped at an edge): that is the start.
        var first = BuildMenu(paths, _anchor.X - _centre.X, _anchor.Y - _centre.Y, TimeSpan.Zero);
        var hot = Track(dx, dy);
        Apply(action, "dragEnterAfterRelease");
        if (first)
            _log.Event("drag.enter", new { files = paths.Count, groups = _navigator!.Groups.Select(g => $"{g.Label}:{g.Commands.Count}"), hookToEnterMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3), paths = PathDiagnostics(paths) });
        else
            _log.Event("drag.reenter");
        return hot;
    }

    bool IOverlayEvents.FilesResolved(IReadOnlyList<string> paths, int dx, int dy)
    {
        if (!_overlay.IsVisible || _session.State == SessionState.Idle) return false; // the ring has already gone
        // Built late: the ring starts from where the pointer is now (a pointer already out in a sector must come back to
        // the centre first) and the arm delay counts from now, so the commands are seen before a drop can run one.
        var built = BuildMenu(paths, dx, dy, Stopwatch.GetElapsedTime(_shownTimestamp));
        var hot = Track(dx, dy);
        _log.Event("drag.files", new { files = paths.Count, built, groups = _navigator?.Groups.Select(g => $"{g.Label}:{g.Commands.Count}"), hookToFilesMs = Math.Round(Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds, 3), paths = PathDiagnostics(paths) });
        return hot;
    }

    /// <summary>Why paths were refused, when any were, and the longest length: never the paths themselves.</summary>
    private static object? PathDiagnostics(IReadOnlyList<string> paths)
    {
        if (paths.Count > CapabilityResolver.MaxFiles) return null; // refused for its size anyway; not worth a pass on the UI thread
        var refused = paths.Select(PathPolicy.Explain).Where(v => v != PathVerdict.Supported).ToArray();
        return refused.Length == 0 ? null : new
        {
            refused = refused.GroupBy(v => v).Select(g => $"{g.Key}:{g.Count()}"),
            longest = paths.Max(p => p.Length),
        };
    }

    /// <summary>
    /// One menu per overlay session, so a drag that leaves and comes back finds the menu where it left it. A menu built
    /// from no readable files does not stay empty once files arrive. Returns whether a menu was built.
    /// </summary>
    private bool BuildMenu(IReadOnlyList<string> paths, int startDx, int startDy, TimeSpan shownAt)
    {
        if (_navigator is not null && (_navigator.Groups.Count > 0 || paths.Count == 0)) return false;
        _navigator = new MenuNavigator(RadialMenu.Build(_sessionResolver, paths), _dpi, RadialMenu.EmptyReason(paths));
        _navigator.Shown(startDx, startDy, shownAt, _dpi);
        return true;
    }

    /// <summary>The open category's place on the ring (the navigation sounds key on it).</summary>
    private static int IndexOf(IReadOnlyList<MenuGroup> groups, MenuGroup open)
    {
        for (var i = 0; i < groups.Count; i++) if (ReferenceEquals(groups[i], open)) return i;
        return -1;
    }

    void IOverlayEvents.DropData(DropDataEvent report) => _log.Event(report.Stage switch
    {
        DropDataStage.Unavailable => "drag.data.unavailable",
        DropDataStage.Retry => "drag.data.retry",
        DropDataStage.Recovered => "drag.data.recovered",
        DropDataStage.GaveUp => "drag.data.gaveUp",
        DropDataStage.SessionFallback => "drag.data.sessionFallback",
        DropDataStage.ShortNamesUnresolved => "drag.data.shortNamesUnresolved",
        _ => "drag.data.dropUnavailable",
    }, new { status = report.Status.ToString(), hr = $"0x{report.HResult:X8}", read = report.Read, files = report.Files });

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
        var view = nav.View(now);
        _overlay.SetView(view);
        if (_navigationSounds.OnView(nav.Open is { } open ? IndexOf(nav.Groups, open) : -1, view.Hot, now) is { } sound) _sounds.Play(sound);
        return nav.Hot is not null;
    }

    private object SoundCounts() => new
    {
        ticks = _navigationSounds.Ticks, levels = _navigationSounds.LevelSounds,
        sectorChanges = _navigationSounds.SectorChanges, levelChanges = _navigationSounds.LevelChanges,
    };

    void IOverlayEvents.DragLeft()
    {
        _log.Event("drag.leave");
        Apply(_session.OnDragLeft(), "dragLeave");
    }

    bool IOverlayEvents.Dropped(IReadOnlyList<string> paths, int dx, int dy, bool copyAllowed)
    {
        var now = Stopwatch.GetElapsedTime(_shownTimestamp);
        CapabilityDescriptor? chosen = null, hot = null;
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
        var frames = _overlay.Frames;
        _overlay.Hide();
        _feedback.DroppedAt(_centre.X + dx, _centre.Y + dy);
        _feedback.OverlayHidden();
        if (execute is null || chosen is null)
        {
            var reason = !copyAllowed ? "copyNotAllowed" : hot is not null ? "notArmed" : "noCommand";
            if (reason == "notArmed") _sounds.Play(UiSound.Nothing); // released on a command too early: say that nothing ran
            _log.Event("drop", new { accepted = false, reason, hot = hot?.Id, openMs, files = paths.Count, at = $"{dx},{dy}", sounds = SoundCounts(), frames });
            return false;
        }
        // Re-resolve from the drop's own data object; run only if it still offers the command that was armed (for
        // "More…", every command it holds).
        var offered = _sessionResolver.Resolve(paths).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (RadialMenu.IsMore(chosen) ? !RadialMenu.Overflow(chosen).All(o => offered.Contains(o.Id)) : !offered.Contains(chosen.Id))
        {
            _sounds.Play(UiSound.Nothing);
            _log.Event("drop", new { accepted = false, reason = "commandsChanged", files = paths.Count });
            return false;
        }
        _log.Event("drop", new { accepted = true, op = chosen.Id, openMs, files = paths.Count, sounds = SoundCounts(), frames });
        if (RadialMenu.IsMore(chosen)) _ = PickAndRunAsync(RadialMenu.Overflow(chosen), paths, _centre.X + dx, _centre.Y + dy);
        else Run(chosen, paths);
        return true;
    }

    private void Run(CapabilityDescriptor capability, IReadOnlyList<string> paths)
    {
        if (_jobs.Enqueue(capability, paths) is null) _log.Event("job.refused", new { op = capability.Id, module = capability.Module }); // no runner for that module
    }

    /// <summary>"More…": the commands that did not fit the ring, in a list (the dialogs process); the pick runs on the dropped files.</summary>
    private async Task PickAndRunAsync(IReadOnlyList<CapabilityDescriptor> commands, IReadOnlyList<string> paths, int x, int y)
    {
        var request = new PickRequest("More commands", [.. commands.Select(c => new PickItem(c.Id, c.Label))], x, y);
        var chosen = await DialogsClient.Default.PickAsync(request).ConfigureAwait(false);
        _log.Event("more.pick", new { offered = commands.Count, chosen });
        if (commands.FirstOrDefault(c => c.Id == chosen) is { } capability) Run(capability, paths);
    }

    public void Dispose()
    {
        _hooks.Dispose();
        _overlay.Dispose();
    }
}
