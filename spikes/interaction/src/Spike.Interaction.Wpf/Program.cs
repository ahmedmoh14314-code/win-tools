using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Spike.Interaction.Core.Input;
using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Session;
using Spike.Interaction.Core.Telemetry;
using Spike.Interaction.Win32;
using Spike.Interaction.Wpf;

// WPF host for Spike 001. Same hooks/detector/session as the Win32 host; only the overlay differs.
// WPF initialises OLE itself (STAThread + Application), so no OleInitialize call is needed here.

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var opt = SpikeOptions.Parse(args, "wpf");
        using var log = new SpikeLog(opt.LogPath, opt.Echo);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var detector = new TriggerDetector(opt.Trigger);
        var session = new InteractionSession(opt.RequireDrag);
        var overlay = new WpfOverlayWindow(log, opt.Radius96, opt.Sectors);
        var dispatcher = Dispatcher.CurrentDispatcher;
        // After the hook's button-up the overlay stays visible until OLE Drop/DragLeave or this grace period (see InteractionSession).
        // Release with a drag inside: wait for OLE Drop/DragLeave (5 s watchdog only guards a dead source). Release with
        // no drag inside: 300 ms grace. See InteractionSession.
        var grace = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Send, (_, _) => { }, dispatcher);
        grace.Tick += (_, _) => { grace.Stop(); var inside = session.DragInside; if (session.NotifyGraceExpired() == SessionCommand.HideOverlay) overlay.HideOverlay(inside ? "watchdogExpired" : "releaseGraceExpired"); };
        grace.Stop();
        overlay.Dropped += (_, _) => { grace.Stop(); session.NotifyDropHandled(); };
        overlay.DragEntered += session.NotifyDragEntered;
        overlay.DragLeft += () => { if (session.NotifyDragLeft() == SessionCommand.HideOverlay) { grace.Stop(); overlay.HideOverlay("dragLeaveAfterRelease"); } };
        var last = ProcessSnapshot.Take();

        using var hooks = new LowLevelHooks();
        hooks.OnKey = e =>
        {
            var d = detector.OnKey(e);
            var swallow = opt.SwallowTrigger && detector.IsTriggerKey(e);
            if (d != TriggerDetector.Decision.None) Post(d, detector.LeftButtonDown, detector.IsDragging, e.Timestamp);
            return swallow;
        };
        hooks.OnMouse = e =>
        {
            var d = detector.OnMouse(e);
            if (d != TriggerDetector.Decision.None) Post(d, detector.LeftButtonDown, detector.IsDragging, e.Timestamp);
            else if (e.IsMove && session.State == SessionState.OverlayShown) dispatcher.BeginInvoke(overlay.NoteMouseMove);
        };

        void Post(TriggerDetector.Decision d, bool leftDown, bool dragging, long ts) => dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            var cmd = session.Apply(d, leftDown, dragging);
            if (d is TriggerDetector.Decision.Activate or TriggerDetector.Decision.Escape)
                log.Event("decision", new { d = d.ToString(), leftDown, dragging, cmd = cmd.ToString(), state = session.State.ToString(), hookToUiMs = Math.Round(SpikeLog.MsBetween(ts, Stopwatch.GetTimestamp()), 3) }, ts);
            if (cmd == SessionCommand.ShowOverlay) overlay.ShowAtCursor(ts);
            else if (cmd == SessionCommand.HideOverlay) { grace.Stop(); overlay.HideOverlay(d.ToString()); }
            else if (cmd is SessionCommand.AwaitDrop or SessionCommand.AwaitDropGrace)
            {
                if (opt.HideOnRelease) { session.NotifyGraceExpired(); overlay.HideOverlay("LeftButtonReleased(hideOnRelease)"); }
                else { grace.Interval = TimeSpan.FromMilliseconds(cmd == SessionCommand.AwaitDrop ? 5000 : 300); grace.Start(); }
            }
        });

        // Pre-create the HWND so first show does not pay window creation (measured separately as "overlay.created").
        overlay.Show(); overlay.Hide();

        log.Event("host.start", new { host = "wpf", trigger = opt.Trigger.Name, swallow = opt.SwallowTrigger, requireDrag = opt.RequireDrag, pid = Environment.ProcessId, runtime = Environment.Version.ToString(), stats = last.ToLogData() });
        hooks.Start();
        log.Event("hooks.installed");

        var timer = new DispatcherTimer(TimeSpan.FromSeconds(opt.StatsIntervalSeconds), DispatcherPriority.Background, (_, _) =>
        {
            var now = ProcessSnapshot.Take();
            var hs = hooks.Stats;
            log.Event("stats", new { cpuPct = Math.Round(ProcessSnapshot.CpuPercentBetween(last, now), 3), proc = now.ToLogData(), hookKbMaxMs = Math.Round(hs.KbMaxMs, 3), hookMouseMaxMs = Math.Round(hs.MouseMaxMs, 3), hookKbCalls = hs.KbCalls, hookMouseCalls = hs.MouseCalls, overlayVisible = overlay.IsVisible, sessionState = session.State.ToString() });
            last = now;
        }, dispatcher);
        timer.Start();

        Console.WriteLine($"WPF spike overlay running. Trigger: {opt.Trigger} (swallow={opt.SwallowTrigger}, requireDrag={opt.RequireDrag}). Log: {log.Path}");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; dispatcher.BeginInvoke(app.Shutdown); };
        app.Run();
        log.Event("host.stop", new { stats = ProcessSnapshot.Take().ToLogData() });
        return 0;
    }
}
