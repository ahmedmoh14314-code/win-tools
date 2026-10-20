using System.Runtime.InteropServices;
using Spike.Interaction.Core.Input;
using Spike.Interaction.Core.Native;

namespace Spike.Interaction.DragSource;

/// <summary>
/// Safety check before injecting input: the window we intend to drag from must actually be the one under the
/// cursor and in the foreground. Otherwise SendInput would drive whatever the user has open (a game, a document).
///
/// A background process may not call SetForegroundWindow successfully (foreground-lock rules), so activation is
/// done the only legitimate way: the target window is made temporarily topmost so it is physically under the
/// point, then a single click is injected on it. If any other (topmost) window still covers the point, we refuse.
/// </summary>
public static class ForegroundGuard
{
    private const uint GA_ROOT = 2;
    public static readonly nint HWND_NOTOPMOST = new(-2);

    /// <summary>Cheap per-cycle check: only re-runs the full activation when the foreground was lost.</summary>
    public static bool StillOurs(Core.Telemetry.SpikeLog log, nint ourHwnd, int x, int y, MouseButton activateWith, int cycle)
    {
        if (User32.GetForegroundWindow() == ourHwnd && RootUnder(x, y) == ourHwnd) return true;
        log.Event("harness.foregroundLost", new { cycle, foreground = Describe(User32.GetForegroundWindow()) });
        return EnsureOurs(log, ourHwnd, x, y, activateWith);
    }

    /// <param name="activateWith">Button used for the activation click. The drag source window ignores right clicks;
    /// an Explorer list area tolerates a left click (selects/deselects an item).</param>
    public static bool EnsureOurs(Core.Telemetry.SpikeLog log, nint ourHwnd, int x, int y, MouseButton activateWith)
    {
        User32.SetWindowPos(ourHwnd, SWP.HWND_TOPMOST, 0, 0, 0, 0, SWP.NOMOVE | SWP.NOSIZE | SWP.NOACTIVATE);
        Thread.Sleep(100);
        var under = RootUnder(x, y);
        if (under != ourHwnd)
        {
            Report(log, false, under, User32.GetForegroundWindow(), "covered");
            return false;
        }

        if (User32.GetForegroundWindow() != ourHwnd)
        {
            InputSimulator.MoveTo(x, y);
            Thread.Sleep(40);
            InputSimulator.ButtonDown(activateWith);
            Thread.Sleep(30);
            InputSimulator.ButtonUp(activateWith);
            Thread.Sleep(200);
            log.Event("harness.activateClick", new { button = activateWith.ToString(), x, y });
        }

        // The window stays topmost for the run so the drag start point keeps hitting it even if the user
        // activates something else meanwhile; callers re-run this check before every cycle.
        under = RootUnder(x, y);
        var fg = User32.GetForegroundWindow();
        var ok = under == ourHwnd && fg == ourHwnd;
        Report(log, ok, under, fg, ok ? "ok" : "notForeground");
        return ok;
    }

    private static nint RootUnder(int x, int y) => GetAncestor(User32.WindowFromPoint(new POINT(x, y)), GA_ROOT);

    private static void Report(Core.Telemetry.SpikeLog log, bool ok, nint under, nint fg, string reason)
    {
        log.Event("harness.foregroundCheck", new { ok, reason, underCursor = Describe(under), foreground = Describe(fg) });
        if (!ok)
        {
            Console.Error.WriteLine($"Refusing to inject input ({reason}): window under the drag point is '{Describe(under)}' and foreground is '{Describe(fg)}'. Close or move that window and retry.");
        }
    }

    private static string Describe(nint h)
    {
        User32.GetWindowThreadProcessId(h, out var pid);
        string proc = "?";
        try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
        return $"{User32.ClassNameOf(h)} '{User32.WindowTextOf(h)}' ({proc})";
    }

    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint gaFlags);
}
