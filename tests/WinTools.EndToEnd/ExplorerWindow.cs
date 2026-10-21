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
        Desktop.SetWindowPos(Hwnd, Desktop.HwndTopmost, 0, 0, 0, 0, Desktop.SwpNoMove | Desktop.SwpNoSize | Desktop.SwpNoActivate);
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

    private static bool SameFolder(string url, string folder) =>
        string.Equals(url.TrimEnd('/'), new Uri(Path.GetFullPath(folder).TrimEnd('\\')).AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

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
