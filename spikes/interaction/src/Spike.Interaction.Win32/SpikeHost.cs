using System.Diagnostics;
using Spike.Interaction.Core.Input;
using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Session;
using Spike.Interaction.Core.Telemetry;

namespace Spike.Interaction.Win32;

/// <summary>
/// Wires hooks → detector → session → overlay on the UI thread. Hook callbacks only post messages;
/// all window work happens in the UI message loop. This is the "application" of the spike.
/// </summary>
public sealed class SpikeHost : IDisposable
{
    private const uint WM_SPIKE_DECISION = WM.USER + 1;
    private const uint WM_SPIKE_MOUSEMOVE = WM.USER + 2;
    private const nuint STATS_TIMER = 1;
    private const nuint GRACE_TIMER = 2;
    private const uint GRACE_MS = 300;     // release with no drag inside the overlay: short wait, then hide
    private const uint WATCHDOG_MS = 5000; // release with a drag inside: OLE owes Drop/DragLeave; this only guards a dead source

    private readonly SpikeOptions _opt;
    private readonly SpikeLog _log;
    private readonly OverlayWindow _overlay;
    private readonly LowLevelHooks _hooks;
    private readonly TriggerDetector _detector;
    private readonly InteractionSession _session;
    private readonly MessageWindow _msgWindow;
    private ProcessSnapshot _lastStats;
    private int _decisionsPosted;

    public SpikeHost(SpikeOptions opt, SpikeLog log)
    {
        _opt = opt;
        _log = log;
        _detector = new TriggerDetector(opt.Trigger);
        _session = new InteractionSession(opt.RequireDrag);
        _overlay = new OverlayWindow(log, opt.Radius96, opt.Sectors);
        _msgWindow = new MessageWindow(OnMessage);
        _hooks = new LowLevelHooks { OnKey = HookKey, OnMouse = HookMouse };
        _overlay.Dropped += (_, _) => { User32.KillTimer(_msgWindow.Handle, GRACE_TIMER); _session.NotifyDropHandled(); };
        _overlay.DragEntered += _session.NotifyDragEntered;
        _overlay.DragLeft += () =>
        {
            if (_session.NotifyDragLeft() == SessionCommand.HideOverlay)
            {
                User32.KillTimer(_msgWindow.Handle, GRACE_TIMER);
                _overlay.Hide("dragLeaveAfterRelease");
            }
        };
        _lastStats = ProcessSnapshot.Take();
    }

    public void Run()
    {
        _log.Event("host.start", new
        {
            trigger = _opt.Trigger.Name,
            swallow = _opt.SwallowTrigger,
            requireDrag = _opt.RequireDrag,
            pid = Environment.ProcessId,
            os = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(),
            monitors = User32.GetSystemMetrics(User32.SM_CMONITORS),
            stats = _lastStats.ToLogData(),
        });
        _hooks.Start();
        _log.Event("hooks.installed");
        if (_opt.StatsIntervalSeconds > 0) User32.SetTimer(_msgWindow.Handle, STATS_TIMER, (uint)(_opt.StatsIntervalSeconds * 1000), 0);

        Console.WriteLine($"Spike overlay running. Trigger: {_opt.Trigger} (swallow={_opt.SwallowTrigger}, requireDrag={_opt.RequireDrag}). Log: {_log.Path}");
        Console.WriteLine("Drag a file from Explorer, press the trigger while dragging, drop on a sector. Ctrl+C to exit.");

        while (User32.GetMessageW(out var msg, 0, 0, 0))
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessageW(ref msg);
        }
    }

    public void RequestStop() => User32.PostMessageW(_msgWindow.Handle, WM.CLOSE, 0, 0);

    // ---- hook thread ----
    private bool HookKey(KeyEvent e)
    {
        var decision = _detector.OnKey(e);
        var swallow = _opt.SwallowTrigger && _detector.IsTriggerKey(e);
        if (decision != TriggerDetector.Decision.None) PostDecision(decision, e.Timestamp);
        return swallow;
    }

    private void HookMouse(MouseEvent e)
    {
        var decision = _detector.OnMouse(e);
        if (decision != TriggerDetector.Decision.None) PostDecision(decision, e.Timestamp);
        else if (e.IsMove && _session.State == SessionState.OverlayShown) User32.PostMessageW(_msgWindow.Handle, WM_SPIKE_MOUSEMOVE, 0, 0);
    }

    private void PostDecision(TriggerDetector.Decision d, long ts)
    {
        // Pack the detector's view of left/drag state into wParam; timestamp (64-bit) into lParam.
        var packed = (nuint)(int)d | ((nuint)(_detector.LeftButtonDown ? 1 : 0) << 8) | ((nuint)(_detector.IsDragging ? 1 : 0) << 9);
        User32.PostMessageW(_msgWindow.Handle, WM_SPIKE_DECISION, packed, (nint)ts);
        _decisionsPosted++;
    }

    // ---- UI thread ----
    private nint OnMessage(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_SPIKE_DECISION:
            {
                var d = (TriggerDetector.Decision)(wParam & 0xFF);
                var leftDown = (wParam & 0x100) != 0;
                var dragging = (wParam & 0x200) != 0;
                var ts = (long)lParam;
                var cmd = _session.Apply(d, leftDown, dragging);
                if (d is TriggerDetector.Decision.Activate or TriggerDetector.Decision.Escape || cmd != SessionCommand.None)
                    _log.Event("decision", new { d = d.ToString(), leftDown, dragging, cmd = cmd.ToString(), state = _session.State.ToString(), dragInside = _session.DragInside, hookToUiMs = Math.Round(SpikeLog.MsBetween(ts, Stopwatch.GetTimestamp()), 3) }, ts);
                switch (cmd)
                {
                    case SessionCommand.ShowOverlay: _overlay.ShowAtCursor(ts); break;
                    case SessionCommand.HideOverlay: User32.KillTimer(_msgWindow.Handle, GRACE_TIMER); _overlay.Hide(d.ToString()); break;
                    case SessionCommand.AwaitDrop:
                    case SessionCommand.AwaitDropGrace:
                        if (_opt.HideOnRelease) { _session.NotifyGraceExpired(); _overlay.Hide("LeftButtonReleased(hideOnRelease)"); break; } // negative control
                        User32.SetTimer(_msgWindow.Handle, GRACE_TIMER, cmd == SessionCommand.AwaitDrop ? WATCHDOG_MS : GRACE_MS, 0);
                        break;
                }
                return 0;
            }
            case WM.TIMER when wParam == GRACE_TIMER:
            {
                User32.KillTimer(_msgWindow.Handle, GRACE_TIMER);
                var inside = _session.DragInside;
                if (_session.NotifyGraceExpired() == SessionCommand.HideOverlay) _overlay.Hide(inside ? "watchdogExpired" : "releaseGraceExpired");
                return 0;
            }
            case WM_SPIKE_MOUSEMOVE:
                _overlay.NoteMouseMove();
                return 0;
            case WM.TIMER when wParam == STATS_TIMER:
            {
                var now = ProcessSnapshot.Take();
                var hs = _hooks.Stats;
                _log.Event("stats", new
                {
                    cpuPct = Math.Round(ProcessSnapshot.CpuPercentBetween(_lastStats, now), 3),
                    proc = now.ToLogData(),
                    hookKbMaxMs = Math.Round(hs.KbMaxMs, 3),
                    hookMouseMaxMs = Math.Round(hs.MouseMaxMs, 3),
                    hookKbCalls = hs.KbCalls,
                    hookMouseCalls = hs.MouseCalls,
                    overlayVisible = _overlay.IsVisible,
                    sessionState = _session.State.ToString(),
                });
                _lastStats = now;
                return 0;
            }
            case WM.CLOSE:
                User32.PostQuitMessage(0);
                return 0;
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _hooks.Dispose();
        _overlay.Dispose();
        _msgWindow.Dispose();
        _log.Event("host.stop", new { stats = ProcessSnapshot.Take().ToLogData(), decisionsPosted = _decisionsPosted });
    }
}
