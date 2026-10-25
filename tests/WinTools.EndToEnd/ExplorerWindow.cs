using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WinTools.EndToEnd;

/// <summary>
/// One Explorer window on the harness's own folder. Identified by its shell location (IShellWindows LocationURL),
/// never by title; re-verified before every injection; only this window is closed at the end (Spike 001 rules).
/// </summary>
internal sealed class ExplorerWindow : IDisposable
{
    private readonly string _folder;
    private readonly AutomationElement _root;

    public nint Hwnd { get; }

    /// <summary>Diagnostics of the last attempt to raise the window.</summary>
    public string LastRaise { get; private set; } = "";

    private ExplorerWindow(string folder, nint hwnd)
    {
        _folder = folder;
        Hwnd = hwnd;
        _root = AutomationElement.FromHandle(hwnd);
    }

    public static ExplorerWindow Open(string folder)
    {
        var before = Entries().Select(e => e.Hwnd).ToHashSet();
        using (Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })) { }
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(250);
            var e = Entries().FirstOrDefault(x => !before.Contains(x.Hwnd) && SameFolder(x.Url, folder));
            if (e.Hwnd != 0)
            {
                Desktop.SetWindowPos(e.Hwnd, 0, 40, 40, 900, 620, Desktop.SwpNoZOrder);
                Thread.Sleep(800);
                return new ExplorerWindow(folder, e.Hwnd);
            }
        }
        throw new InvalidOperationException("The Explorer window for the harness folder did not appear.");
    }

    /// <summary>True only if this window still shows the harness folder (re-queried every time).</summary>
    public bool StillShowsFolder() => Entries().Any(e => e.Hwnd == Hwnd && SameFolder(e.Url, _folder));

    /// <summary>
    /// Makes the window topmost and, if needed, activates it with one click on a known point inside it. Refuses
    /// (returns false) if anything else covers the point.
    /// </summary>
    public bool EnsureForeground(int x, int y)
    {
        var before = Desktop.ClassOf(Desktop.GetForegroundWindow());
        var raised = Desktop.SetWindowPos(Hwnd, Desktop.HwndTopmost, 0, 0, 0, 0, Desktop.SwpNoMove | Desktop.SwpNoSize | Desktop.SwpNoActivate);
        LastRaise = $"foregroundBefore={before} setWindowPos={raised} error={(raised ? 0 : Marshal.GetLastPInvokeError())} {Desktop.StateOf(Hwnd)}";
        Thread.Sleep(100);
        if (Desktop.RootAt(x, y) != Hwnd || !StillShowsFolder()) return false;
        if (Desktop.GetForegroundWindow() != Hwnd)
        {
            Desktop.MoveTo(x, y); Thread.Sleep(40);
            Desktop.LeftDown(); Thread.Sleep(30); Desktop.LeftUp();
            Thread.Sleep((int)Desktop.GetDoubleClickTime() + 200); // the next button-down must not complete a double-click
        }
        return Desktop.GetForegroundWindow() == Hwnd && Desktop.RootAt(x, y) == Hwnd;
    }

    /// <summary>Centre of the item named <paramref name="fileName"/> (with or without its extension shown).</summary>
    public (int X, int Y)? ItemCentre(string fileName)
    {
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new OrCondition(new PropertyCondition(AutomationElement.NameProperty, fileName),
                            new PropertyCondition(AutomationElement.NameProperty, Path.GetFileNameWithoutExtension(fileName))));
        for (var i = 0; i < 32; i++)
        {
            try
            {
                if (_root.FindFirst(TreeScope.Descendants, cond) is { } item)
                {
                    var r = item.Current.BoundingRectangle;
                    return ((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                }
            }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(250);
        }
        return null;
    }

    /// <summary>
    /// Centres of the realised items among <paramref name="fileNames"/>, in one pass. Explorer realises only the items
    /// near the view, so asking for each of 100 files with <see cref="ItemCentre"/> waited out its retries for every
    /// unrealised one (minutes).
    /// </summary>
    public List<(int X, int Y)> RealisedItemCentres(IEnumerable<string> fileNames)
    {
        var wanted = new HashSet<string>(fileNames.SelectMany(f => new[] { f, Path.GetFileNameWithoutExtension(f) }), StringComparer.Ordinal);
        var found = new List<(int X, int Y)>();
        try
        {
            foreach (AutomationElement item in _root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
            {
                if (!wanted.Contains(item.Current.Name)) continue;
                var r = item.Current.BoundingRectangle;
                if (!r.IsEmpty) found.Add(((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)));
            }
        }
        catch (ElementNotAvailableException) { }
        return found;
    }

    /// <summary>
    /// Whether the screen point hits one of these items itself. A realised item may lie below the visible part of the
    /// list (under the status bar or past the window), where a press starts no drag.
    /// </summary>
    public bool HitsItem(int x, int y, IEnumerable<string> fileNames)
    {
        var wanted = new HashSet<string>(fileNames.SelectMany(f => new[] { f, Path.GetFileNameWithoutExtension(f) }), StringComparer.Ordinal);
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            for (var e = AutomationElement.FromPoint(new System.Windows.Point(x, y)); e is not null && e != _root; e = walker.GetParent(e))
                if (e.Current.ControlType == ControlType.ListItem) return wanted.Contains(e.Current.Name);
        }
        catch (ElementNotAvailableException) { }
        return false;
    }

    /// <summary>
    /// SPIKE (typing interference, ADR-0021): what a key could have changed in this window. The selected items' names
    /// (type-ahead), the focused element (a rename box, the search box, the address bar, another pane) and the title.
    /// </summary>
    public string ProbeState()
    {
        try
        {
            var selected = _root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>()
                .Where(i => i.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected)
                .Select(i => i.Current.Name).Order(StringComparer.Ordinal);
            var focus = AutomationElement.FocusedElement;
            var focusText = focus is null ? "none" : $"{focus.Current.ControlType.ProgrammaticName}/{focus.Current.ClassName}/{focus.Current.AutomationId}";
            Desktop.GetWindowRect(Hwnd, out var r); // full screen (F11) or a resized window changes it
            return $"selected=[{string.Join(",", selected)}] focus={focusText} title={_root.Current.Name} rect={r.Left},{r.Top},{r.Right},{r.Bottom}";
        }
        catch (ElementNotAvailableException) { return "unavailable"; }
    }

    /// <summary>Diagnostics: the status bar's texts ("100 items", "20 items selected").</summary>
    public string StatusText()
    {
        try
        {
            // Only the status bar: the list's own texts (dates, sizes, numbered names) carry digits too (evaluator, PR #14).
            var bar = _root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar));
            if (bar is null) return "?";
            var texts = bar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            // Status-bar texts carry counts ("100 items", "20 items selected"); matching digits works in any display language.
            return string.Join(" | ", texts.Cast<AutomationElement>().Select(t => t.Current.Name).Where(n => n.Any(char.IsDigit) && n.Length < 80));
        }
        catch (ElementNotAvailableException) { return "?"; }
    }

    /// <summary>Centre of the first or last item in the view's own order, whatever the view mode.</summary>
    public (int X, int Y)? EndItemCentre(bool last) => EndItem(last)?.Centre;

    /// <summary>
    /// The first or last realised item and its name. Explorer realises only the items near the view, so the last one
    /// realised is the list's last item only once the view has been scrolled to the end: compare the name.
    /// </summary>
    public (string Name, (int X, int Y) Centre)? EndItem(bool last)
    {
        try
        {
            var items = _root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            if (items.Count == 0) return null;
            var item = items[last ? items.Count - 1 : 0];
            var r = item.Current.BoundingRectangle;
            return (item.Current.Name, ((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)));
        }
        catch (ElementNotAvailableException) { return null; }
    }

    /// <summary>Diagnostics: the control types and names of the first few items in the view.</summary>
    public string DescribeItems()
    {
        try
        {
            var items = _root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
            return string.Join(" | ", items.Cast<AutomationElement>().Take(6).Select(e => $"{e.Current.ControlType.ProgrammaticName}:{e.Current.Name}")) + $" (count {items.Count})";
        }
        catch (ElementNotAvailableException) { return "unavailable"; }
    }

    /// <summary>
    /// Whether releasing at (x, y) is safe and what is there: our overlay, or this window's folder background (a drop on
    /// the folder that holds the file is a no-op for Explorer). Anything else, including an item, is refused (null).
    /// </summary>
    public string? ReleaseTarget(int x, int y)
    {
        var root = Desktop.RootAt(x, y);
        if (Desktop.ClassOf(root) == "WinTools.RadialOverlay") return "overlay";
        if (root != Hwnd || !StillShowsFolder()) return null;
        try
        {
            var ct = AutomationElement.FromPoint(new System.Windows.Point(x, y)).Current.ControlType;
            if (ct == ControlType.ListItem || ct == ControlType.TreeItem || ct == ControlType.Button || ct == ControlType.Edit || ct == ControlType.MenuItem || ct == ControlType.TabItem) return null;
        }
        catch (ElementNotAvailableException) { return null; }
        return "explorer";
    }

    public void Dispose()
    {
        Desktop.SetWindowPos(Hwnd, Desktop.HwndNoTopmost, 0, 0, 0, 0, Desktop.SwpNoMove | Desktop.SwpNoSize | Desktop.SwpNoActivate);
        foreach (var e in Entries().Where(e => e.Hwnd == Hwnd && SameFolder(e.Url, _folder)))
            try { ((dynamic)e.Browser).Quit(); } catch (COMException) { }
    }

    // Compared as local paths: Explorer's LocationURL and .NET's AbsoluteUri escape non-ASCII names (Arabic, emoji)
    // differently, so comparing the URL strings never matched such a folder.
    private static bool SameFolder(string url, string folder) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile
        && string.Equals(Path.TrimEndingDirectorySeparator(u.LocalPath), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);

    private readonly record struct Entry(nint Hwnd, string Url, object Browser);

    private static List<Entry> Entries()
    {
        var list = new List<Entry>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic windows = shell.Windows();
        int count = windows.Count;
        for (var i = 0; i < count; i++)
        {
            try
            {
                dynamic? w = windows.Item(i);
                if (w is null) continue;
                list.Add(new Entry((nint)(long)w.HWND, (string?)w.LocationURL ?? "", (object)w));
            }
            catch (COMException) { }
        }
        return list;
    }
}
