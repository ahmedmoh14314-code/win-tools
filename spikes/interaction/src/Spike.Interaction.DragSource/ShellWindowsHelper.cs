using System.IO;
using System.Runtime.InteropServices;

namespace Spike.Interaction.DragSource;

/// <summary>
/// Thin wrapper over Shell.Application.Windows() (IShellWindows). Every open Explorer view (window or tab) is an
/// entry with its HWND and its LocationURL (file:///C:/...). The harness identifies its own window by location,
/// never by title text, and closes only that entry.
/// </summary>
public static class ShellWindowsHelper
{
    public sealed record Entry(nint Hwnd, string LocationUrl, object Browser);

    public static string FolderUrl(string dir) => new Uri(Path.GetFullPath(dir).TrimEnd('\\')).AbsoluteUri.TrimEnd('/');

    public static List<Entry> List()
    {
        var list = new List<Entry>();
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application not available");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic windows = shell.Windows();
        int count = windows.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic? w = null;
            try { w = windows.Item(i); } catch (COMException) { }
            if (w is null) continue;
            string url; long hwnd;
            try { url = (string?)w.LocationURL ?? ""; hwnd = (long)w.HWND; } catch (COMException) { continue; }
            list.Add(new Entry((nint)hwnd, url.TrimEnd('/'), (object)w));
        }
        return list;
    }

    public static Entry? FindByLocation(string dir, ISet<nint>? exclude = null)
    {
        var want = FolderUrl(dir);
        foreach (var e in List())
        {
            if (exclude is not null && exclude.Contains(e.Hwnd)) continue;
            if (string.Equals(e.LocationUrl, want, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    /// <summary>True only if the given window still shows the given folder (re-queried every time).</summary>
    public static bool StillShows(nint hwnd, string dir)
    {
        var want = FolderUrl(dir);
        return List().Any(e => e.Hwnd == hwnd && string.Equals(e.LocationUrl, want, StringComparison.OrdinalIgnoreCase));
    }

    public static void Quit(Entry e)
    {
        try { ((dynamic)e.Browser).Quit(); } catch (COMException) { }
    }
}
