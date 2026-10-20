using System.IO;
using System.Diagnostics;
using System.Windows.Automation;
using Spike.Interaction.Core.Native;
using Spike.Interaction.Core.Telemetry;

namespace Spike.Interaction.DragSource;

/// <summary>
/// Drives a real Windows Explorer window on the harness's own sample folder. Safety rules (after incident 22051A):
///  - the window is identified by its shell location (IShellWindows LocationURL == sample folder), never by title;
///  - every injection (activation click, Ctrl+A, drag start, release) is preceded by a re-check that the window
///    still shows the sample folder and is the root window under the point;
///  - a release is refused (Esc instead) when the window under the release point is not what the scenario expects;
///  - only the Explorer view the harness opened is closed (IWebBrowser2.Quit on that entry);
///  - the caller asserts the sample folder's content after the run.
/// </summary>
public static class ExplorerMode
{
    public static int Run(HarnessOptions o, SpikeLog log, string dir, IReadOnlyList<string> paths)
    {
        var preExisting = ShellWindowsHelper.List().Select(e => e.Hwnd).ToHashSet();
        using var launcher = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false });

        ShellWindowsHelper.Entry? entry = null;
        for (var attempt = 0; attempt < 40 && entry is null; attempt++)
        {
            Thread.Sleep(250);
            entry = ShellWindowsHelper.FindByLocation(dir, preExisting);
        }
        if (entry is null) { log.Event("explorer.notFound", new { dir, preExisting = preExisting.Count }); Console.Error.WriteLine("Explorer window for the sample folder not found."); return 3; }
        var hwnd = entry.Hwnd;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        log.Event("explorer.found", new { hwnd = hwnd.ToString("X"), pid, location = entry.LocationUrl, title = User32.WindowTextOf(hwnd) });

        try
        {
            // Stable place/size on the primary work area.
            var (work, _, _, _) = MonitorInfo.ForPoint(new POINT(0, 0));
            User32.SetWindowPos(hwnd, 0, work.Left + 20, work.Top + 20, Math.Min(900, work.Width - 40), Math.Min(600, work.Height - 40), SWP.NOZORDER);
            Thread.Sleep(800);
            var window = AutomationElement.FromHandle(hwnd);
            var targetName = Path.GetFileName(paths[0]);

            // Activation point: the dragged item itself (verified below), so the click lands on a known element.
            var item0 = FindItemWithRetry(window, targetName);
            if (item0 is null) { log.Event("explorer.itemNotFound", new { cycle = 0, targetName }); return 3; }
            var (ax, ay) = Centre(item0);
            if (!ShellWindowsHelper.StillShows(hwnd, dir)) { log.Event("harness.abort", new { reason = "window no longer shows the sample folder before activation" }); return 4; }
            if (!ForegroundGuard.EnsureOurs(log, hwnd, ax, ay, Spike.Interaction.Core.Input.MouseButton.Left)) return 4;

            var completed = 0;
            for (var i = 1; i <= o.Cycles; i++)
            {
                if (!ShellWindowsHelper.StillShows(hwnd, dir)) { log.Event("harness.abort", new { cycle = i, reason = "window no longer shows the sample folder" }); break; }
                var item = FindItemWithRetry(window, targetName);
                if (item is null) { log.Event("explorer.itemNotFound", new { cycle = i, targetName }); break; }
                var (cx, cy) = Centre(item);
                if (!ForegroundGuard.StillOurs(log, hwnd, cx, cy, Spike.Interaction.Core.Input.MouseButton.Left, i))
                {
                    log.Event("harness.abort", new { cycle = i, reason = "foreground lost and could not be regained" });
                    break;
                }
                log.Event("cycle.start", new { cycle = i, item = targetName, at = $"({cx},{cy})" });
                InputSimulator.MoveTo(cx, cy);
                Thread.Sleep(50);
                if (o.FileCount > 1 && !SelectAll(log, hwnd, dir, cx, cy)) break;

                DragScript.Perform(o, log, cx, cy, i, (x, y) => ReleaseGuard(hwnd, dir, x, y, o.DropOffset > o.Radius96 * 2));
                Thread.Sleep(600);
                log.Event("explorer.selectionAfter", new { cycle = i, selected = IsSelected(window, targetName) });
                completed++;
            }

            log.Event("harness.summary", new { cycles = o.Cycles, completed });
            Console.WriteLine($"Explorer mode done: cycles={o.Cycles} completed={completed}. Check the overlay log for drop events.");
            return 0;
        }
        finally
        {
            User32.SetWindowPos(hwnd, ForegroundGuard.HWND_NOTOPMOST, 0, 0, 0, 0, SWP.NOMOVE | SWP.NOSIZE | SWP.NOACTIVATE);
            // Close only the view we opened, found again by hwnd + location.
            var mine = ShellWindowsHelper.List().FirstOrDefault(e => e.Hwnd == hwnd && string.Equals(e.LocationUrl, ShellWindowsHelper.FolderUrl(dir), StringComparison.OrdinalIgnoreCase));
            if (mine is not null) ShellWindowsHelper.Quit(mine);
            log.Event("explorer.closed", new { hwnd = hwnd.ToString("X"), found = mine is not null });
        }
    }

    /// <summary>
    /// Decides whether the left button may be released at (x, y). Normal drops expect the overlay (not Explorer)
    /// under the point. "Release outside" runs expect the sample-folder window itself, over a non-item area
    /// (dropping on the folder that owns the file is a no-op for Explorer; dropping on a list item or tree node is not).
    /// Returns null to allow, or a reason to refuse (the script then presses Esc instead of releasing).
    /// </summary>
    private static string? ReleaseGuard(nint explorerHwnd, string dir, int x, int y, bool expectExplorerUnderneath)
    {
        var root = GetAncestor(User32.WindowFromPoint(new POINT(x, y)), 2);
        var cls = User32.ClassNameOf(root);
        if (!expectExplorerUnderneath)
        {
            return cls == "CabinetWClass" ? $"Explorer ({root:X}) is under the release point; the overlay did not appear" : null;
        }
        if (root != explorerHwnd) return $"expected the sample-folder window under the release point, found {cls} {root:X}";
        if (!ShellWindowsHelper.StillShows(explorerHwnd, dir)) return "window no longer shows the sample folder";
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            var ct = el.Current.ControlType;
            if (ct == ControlType.ListItem || ct == ControlType.TreeItem || ct == ControlType.Button || ct == ControlType.Edit || ct == ControlType.MenuItem || ct == ControlType.TabItem)
                return $"release point is over a {ct.ProgrammaticName} ('{el.Current.Name}')";
        }
        catch (ElementNotAvailableException) { return "UIA element under the release point not available"; }
        return null;
    }

    private static bool SelectAll(SpikeLog log, nint hwnd, string dir, int x, int y)
    {
        if (User32.GetForegroundWindow() != hwnd || GetAncestor(User32.WindowFromPoint(new POINT(x, y)), 2) != hwnd || !ShellWindowsHelper.StillShows(hwnd, dir))
        {
            log.Event("harness.abort", new { reason = "refusing Ctrl+A: sample-folder window is not foreground/under the cursor" });
            return false;
        }
        InputSimulator.KeyDown(VK.CONTROL); InputSimulator.Tap('A'); InputSimulator.KeyUp(VK.CONTROL);
        Thread.Sleep(150);
        return true;
    }

    private static (int X, int Y) Centre(AutomationElement item)
    {
        var r = item.Current.BoundingRectangle;
        return ((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
    }

    private static AutomationElement? FindItemWithRetry(AutomationElement window, string name)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            if (attempt > 0) Thread.Sleep(250);
            try { var it = FindItem(window, name); if (it is not null) return it; } catch (ElementNotAvailableException) { }
        }
        return null;
    }

    private static AutomationElement? FindItem(AutomationElement window, string name)
    {
        // With "Hide extensions for known file types" (the default) the item's UIA name has no extension.
        var noExt = Path.GetFileNameWithoutExtension(name);
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new OrCondition(
                new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.NameProperty, noExt)));
        return window.FindFirst(TreeScope.Descendants, cond);
    }

    private static bool? IsSelected(AutomationElement window, string name)
    {
        try
        {
            var it = FindItem(window, name);
            if (it is null) return null;
            if (it.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p)) return ((SelectionItemPattern)p).Current.IsSelected;
        }
        catch (ElementNotAvailableException) { }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint gaFlags);
}
