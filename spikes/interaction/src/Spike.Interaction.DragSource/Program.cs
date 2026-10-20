using System.IO;
using System.Diagnostics;
using Spike.Interaction.Core.Input;
using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Telemetry;
using Spike.Interaction.DragSource;

// Automated drag harness for Spike 001. Drives real input with SendInput so the overlay process is exercised
// end to end without a human. Modes:
//   self      : this process starts a shell drag from its own window (shell data object, shell drop source).
//   explorer  : opens an Explorer window on the sample folder and drags the real items (UI Automation to locate them).
// The overlay host (Win32 or WPF) must already be running with the same --trigger.

internal static class Program
{
    [STAThread] // OleInitialize / SHDoDragDrop require a single-threaded apartment.
    private static int Main(string[] args)
    {
var o = HarnessOptions.Parse(args);
User32.SetProcessDpiAwarenessContext(DpiAwarenessContext.PER_MONITOR_AWARE_V2);
var hr = Ole32.OleInitialize(0);
if (hr < 0) { Console.Error.WriteLine($"OleInitialize failed 0x{hr:X8}"); return 2; }

using var log = new SpikeLog(o.LogPath, true);
var (dir, paths) = SampleFiles.Create(count: o.FileCount);
log.Event("harness.start", new { mode = o.Mode, trigger = o.Trigger.Name, cycles = o.Cycles, files = paths, dropOffset = o.DropOffset, cancel = o.Cancel, holdTrigger = o.HoldTriggerMs });

Console.WriteLine($"Harness: mode={o.Mode} cycles={o.Cycles} trigger={o.Trigger}. The mouse and keyboard will be driven for ~{o.Cycles * 1.2:0}s. Starting in {o.StartDelayMs} ms...");
Thread.Sleep(o.StartDelayMs);

try
{
    var code = o.Mode switch
    {
        "self" => SelfMode.Run(o, log, paths),
        "explorer" => ExplorerMode.Run(o, log, dir, paths),
        _ => throw new ArgumentException($"Unknown mode {o.Mode}"),
    };
    // Evidence that the run left the files alone: exact content and no extra entries in the sample folder.
    Thread.Sleep(500);
    var (ok, detail) = SampleFiles.Verify(dir, paths);
    log.Event("harness.sampleCheck", new { ok, detail });
    Console.WriteLine($"Sample folder check: {(ok ? "OK" : "FAILED")} - {detail}");
    return ok ? code : 5;
}
finally
{
    Ole32.OleUninitialize();
}
    }
}

public sealed record HarnessOptions(string Mode, TriggerSpec Trigger, int Cycles, int FileCount, int DropOffset, bool Cancel, int HoldTriggerMs, int StartDelayMs, int SettleMs, string LogPath,
    IReadOnlyList<POINT> TriggerAt, int HoldModifierVk, int Radius96, int ReleaseDelayMs, bool ReleaseWhileMoving, bool EarlyTrigger,
    int FastReleaseMs = -1, bool FastFlick = false)
{
    public static HarnessOptions Parse(string[] args)
    {
        string mode = "self", trigger = "space"; string? log = null;
        int cycles = 5, files = 3, offset = 70, hold = 40, startDelay = 1500, settle = 250, holdMod = 0, radius = 110, releaseDelay = 120;
        bool cancel = false, releaseWhileMoving = false, earlyTrigger = false, fastFlick = false; var fastRelease = -1;
        var triggerAt = new List<POINT>();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => args[++i];
            switch (args[i])
            {
                case "--mode": mode = Next(); break;
                case "--trigger": trigger = Next(); break;
                case "--cycles": cycles = int.Parse(Next()); break;
                case "--files": files = int.Parse(Next()); break;
                case "--drop-offset": offset = int.Parse(Next()); break;
                case "--cancel": cancel = true; break;
                case "--hold-trigger": hold = int.Parse(Next()); break;
                case "--start-delay": startDelay = int.Parse(Next()); break;
                case "--settle": settle = int.Parse(Next()); break;
                case "--log": log = Next(); break;
                // Screen points (physical px) where the trigger is pressed, cycled per run: "x,y;x,y". Used for edge/corner tests.
                case "--trigger-at":
                    foreach (var p in Next().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var xy = p.Split(',');
                        triggerAt.Add(new POINT(int.Parse(xy[0]), int.Parse(xy[1])));
                    }
                    break;
                // Hold a drag modifier for the whole drag (Explorer: Shift = move, Ctrl = copy, Alt = link).
                case "--hold-modifier":
                    holdMod = Next().ToLowerInvariant() switch { "shift" => VK.SHIFT, "ctrl" => VK.CONTROL, "alt" => VK.MENU, var m => throw new ArgumentException($"Unknown modifier {m}") };
                    break;
                case "--radius": radius = int.Parse(Next()); break; // must match the host's --radius for corner drop placement
                case "--release-delay": releaseDelay = int.Parse(Next()); break;   // ms between reaching the drop point and releasing (default 120)
                case "--release-while-moving": releaseWhileMoving = true; break;   // release in the middle of the glide to the drop point
                case "--fast-release": fastRelease = int.Parse(Next()); break;     // Spike 002C: release N ms after the trigger key-down (key triggers only)
                case "--fast-flick": fastFlick = true; break;                      // with --fast-release: jump to the sector right after the key-down
                case "--early-trigger": earlyTrigger = true; break;                // press the trigger right after button-down, before the drag threshold
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        log ??= Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results", $"harness-{mode}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        return new HarnessOptions(mode, TriggerSpec.Parse(trigger), cycles, files, offset, cancel, hold, startDelay, settle, Path.GetFullPath(log), triggerAt, holdMod, radius, releaseDelay, releaseWhileMoving, earlyTrigger, fastRelease, fastFlick);
    }
}

/// <summary>Shared drag choreography: button down at a point, glide, trigger, glide to the sector, release or cancel.</summary>
public static class DragScript
{
    public static void Perform(HarnessOptions o, SpikeLog log, int startX, int startY, int cycle, Func<int, int, string?>? releaseGuard = null)
    {
        InputSimulator.MoveTo(startX, startY);
        Thread.Sleep(60);
        if (o.HoldModifierVk != 0) InputSimulator.KeyDown(o.HoldModifierVk);
        var t0 = Stopwatch.GetTimestamp();
        InputSimulator.LeftDown();
        Thread.Sleep(80);
        if (o.EarlyTrigger) { InputSimulator.PressTrigger(o.Trigger, o.HoldTriggerMs); log.Event("script.earlyTrigger", new { cycle, note = "pressed before the drag threshold" }); Thread.Sleep(100); }
        InputSimulator.GlideTo(startX + 40, startY + 30);          // pass the drag threshold, OLE drag starts
        POINT? triggerAt = o.TriggerAt.Count > 0 ? o.TriggerAt[(cycle - 1) % o.TriggerAt.Count] : null;
        if (triggerAt is { } ta)
        {
            Thread.Sleep(100);
            InputSimulator.GlideTo(ta.X, ta.Y, steps: 16, stepDelayMs: 10);   // long glide to the edge/corner point
        }
        Thread.Sleep(o.SettleMs);
        if (o.FastReleaseMs >= 0) { FastRelease(o, log, cycle, t0, releaseGuard); return; }
        var tTrig = Stopwatch.GetTimestamp();
        InputSimulator.PressTrigger(o.Trigger, o.HoldTriggerMs);    // overlay should appear under the cursor
        User32.GetCursorPos(out var c);
        log.Event("script.trigger", new { cycle, sinceLeftDownMs = Math.Round(SpikeLog.MsBetween(t0, tTrig), 1), at = $"({c.X},{c.Y})" }, tTrig);
        Thread.Sleep(o.SettleMs);
        // Aim at the middle of sector 1 (which spans 3 → 6 o'clock when sector 0 starts at 12), i.e. the 4:30 direction.
        // Dropping exactly at 3 o'clock sits on the 0/1 boundary and rounds differently in px (Win32) vs DIP (WPF).
        var diag = (int)Math.Round(o.DropOffset * 0.7071);
        int dropX = c.X + diag, dropY = c.Y + diag;
        if (triggerAt is not null)
        {
            // Near edges the overlay is clamped, so aim at the clamped ring's centre + offset (same math as the host).
            var (work, _, dpi, _) = MonitorInfo.ForPoint(c);
            var size = Spike.Interaction.Core.Overlay.OverlayPlacement.Scale(o.Radius96 * 2, dpi);
            var rect = Spike.Interaction.Core.Overlay.OverlayPlacement.Place(c, work, size, size);
            var (cx, cy) = (rect.Left + size / 2, rect.Top + size / 2);
            var cursorInsideRing = Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy)) <= size / 2.0;
            dropX = cx + diag; dropY = cy + diag;
            log.Event("script.cornerPlan", new { cycle, cursor = $"({c.X},{c.Y})", expectedRect = $"[{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}]", cursorInsideRing, dropAt = $"({dropX},{dropY})" });
        }
        if (o.ReleaseWhileMoving)
        {
            InputSimulator.GlideTo((c.X + dropX) / 2, (c.Y + dropY) / 2);
            var reasonMoving = releaseGuard?.Invoke((c.X + dropX) / 2, (c.Y + dropY) / 2);
            if (reasonMoving is null) { InputSimulator.LeftUp(); log.Event("script.release", new { cycle, whileMoving = true }); InputSimulator.GlideTo(dropX, dropY, 3, 5); }
            else { log.Event("script.releaseRefused", new { cycle, reason = reasonMoving }); InputSimulator.Tap(VK.ESCAPE); Thread.Sleep(150); InputSimulator.LeftUp(); Thread.Sleep((int)User32.GetDoubleClickTime() + 200); }
            if (o.HoldModifierVk != 0) { Thread.Sleep(50); InputSimulator.KeyUp(o.HoldModifierVk); }
            return;
        }
        InputSimulator.GlideTo(dropX, dropY);
        Thread.Sleep(o.ReleaseDelayMs);
        var refuse = o.Cancel ? null : releaseGuard?.Invoke(dropX, dropY);
        if (refuse is not null) { log.Event("script.releaseRefused", new { cycle, reason = refuse }); }
        if (o.Cancel || refuse is not null)
        {
            InputSimulator.Tap(VK.ESCAPE);
            log.Event("script.escape", new { cycle });
            Thread.Sleep(150);
            InputSimulator.LeftUp();
            Thread.Sleep((int)User32.GetDoubleClickTime() + 200); // see FastRelease: no accidental double-click on the file
        }
        else
        {
            InputSimulator.LeftUp();
            log.Event("script.release", new { cycle });
        }
        if (o.HoldModifierVk != 0) { Thread.Sleep(50); InputSimulator.KeyUp(o.HoldModifierVk); }
    }

    /// <summary>Spike 002C: trigger key-down, optionally jump to the sector, then release the button N ms after the key-down
    /// (busy-wait; Sleep's default resolution is ~15.6 ms). Models a user who presses the trigger and lets go immediately.</summary>
    private static void FastRelease(HarnessOptions o, SpikeLog log, int cycle, long t0, Func<int, int, string?>? releaseGuard)
    {
        var vk = o.Trigger.VirtualKey ?? throw new ArgumentException("--fast-release needs a key trigger");
        User32.GetCursorPos(out var c);
        var diag = (int)Math.Round(o.DropOffset * 0.7071);
        var (rx, ry) = o.FastFlick ? (c.X + diag, c.Y + diag) : (c.X, c.Y);
        var tTrig = Stopwatch.GetTimestamp();
        InputSimulator.KeyDown(vk);
        log.Event("script.trigger", new { cycle, sinceLeftDownMs = Math.Round(SpikeLog.MsBetween(t0, tTrig), 1), at = $"({c.X},{c.Y})", fastReleaseMs = o.FastReleaseMs, flick = o.FastFlick }, tTrig);
        if (o.FastFlick) InputSimulator.MoveTo(rx, ry);
        while (SpikeLog.MsBetween(tTrig, Stopwatch.GetTimestamp()) < o.FastReleaseMs) Thread.SpinWait(50);
        var refuse = releaseGuard?.Invoke(rx, ry);   // Explorer mode: decided at the release instant; never release over Explorer itself
        long tRel;
        if (refuse is null) { tRel = Stopwatch.GetTimestamp(); InputSimulator.LeftUp(); }
        else { tRel = Stopwatch.GetTimestamp(); InputSimulator.Tap(VK.ESCAPE); Thread.Sleep(150); InputSimulator.LeftUp(); }
        log.Event(refuse is null ? "script.release" : "script.releaseRefused", new { cycle, sinceTriggerMs = Math.Round(SpikeLog.MsBetween(tTrig, tRel), 2), at = $"({rx},{ry})", reason = refuse }, tRel);
        Thread.Sleep(o.HoldTriggerMs);
        InputSimulator.KeyUp(vk);
        // A cancelled drag ends with a plain button-up on the file; the next cycle's button-down on the same file must not
        // complete a double-click (that opened the sample file once, 2026-10-20).
        if (refuse is not null) Thread.Sleep((int)User32.GetDoubleClickTime() + 200);
    }
}

public static class SelfMode
{
    public static int Run(HarnessOptions o, SpikeLog log, IReadOnlyList<string> paths)
    {
        var (work, _, _, _) = MonitorInfo.ForPoint(new POINT(0, 0));
        var rect = new RECT(work.Left + 40, work.Top + 40, work.Left + 340, work.Top + 240);
        using var src = new SourceWindow(log, rect);
        src.SetPaths(paths);
        if (!ForegroundGuard.EnsureOurs(log, src.Handle, (rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2, MouseButton.Right)) return 4;
        var outcomes = new List<SourceWindow.DragOutcome>();
        var done = new AutoResetEvent(false);
        src.DragFinished += r => { outcomes.Add(r); done.Set(); };

        var script = new Thread(() =>
        {
            for (var i = 1; i <= o.Cycles; i++)
            {
                if (!ForegroundGuard.StillOurs(log, src.Handle, (rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2, MouseButton.Right, i))
                {
                    log.Event("harness.abort", new { cycle = i, reason = "foreground lost and could not be regained" });
                    break;
                }
                log.Event("cycle.start", new { cycle = i });
                DragScript.Perform(o, log, (rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2, i);
                if (!done.WaitOne(4000)) log.Event("cycle.timeout", new { cycle = i, note = "SHDoDragDrop did not return within 4 s" });
                Thread.Sleep(300);
            }
            User32.PostMessageW(src.Handle, WM.CLOSE, 0, 0);
        }) { IsBackground = true, Name = "script" };
        script.Start();

        while (User32.GetMessageW(out var msg, 0, 0, 0)) { User32.TranslateMessage(ref msg); User32.DispatchMessageW(ref msg); }

        var drops = outcomes.Count(x => x.Hr == HRESULTS.DRAGDROP_S_DROP);
        var cancels = outcomes.Count(x => x.Hr == HRESULTS.DRAGDROP_S_CANCEL);
        var moves = outcomes.Count(x => x.Effect == DROPEFFECT.MOVE);
        log.Event("harness.summary", new { cycles = o.Cycles, completed = outcomes.Count, drops, cancels, moveEffects = moves });
        Console.WriteLine($"Summary: cycles={o.Cycles} completed={outcomes.Count} drops={drops} cancels={cancels} MOVE-effects={moves} (must be 0)");
        return 0;
    }
}
