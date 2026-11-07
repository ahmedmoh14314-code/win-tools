using System.Runtime.InteropServices;

namespace WinTools.Windows.Shell;

/// <summary>
/// Show in folder (ADR-0032): an Explorer window on the folder with the items selected, through
/// <c>SHOpenFolderAndSelectItems</c> as Microsoft documents it (the folder's and the items' ID lists from
/// <c>SHParseDisplayName</c>; Chromium's "show in folder" does the same). No command line is built: explorer.exe's
/// <c>/select,</c> takes its quote after the comma, which an argument list cannot express: a path with a space would quote
/// the whole argument, the form reported to open Documents instead. The call runs on a thread of its own with COM
/// initialised, so a busy Explorer never blocks the host; the host stops waiting after a timeout.
/// </summary>
public static unsafe partial class ShowInFolder
{
    private const uint COINIT_APARTMENTTHREADED = 0x2;

    /// <summary>Opens <paramref name="folder"/> with <paramref name="items"/> (its children) selected. False when the shell
    /// refused or did not answer within <paramref name="timeout"/>.</summary>
    public static bool Show(string folder, IReadOnlyList<string> items, TimeSpan timeout)
    {
        var shown = 0;
        var thread = new Thread(() => { if (ShowHere(folder, items)) Volatile.Write(ref shown, 1); }) { IsBackground = true, Name = "WinTools show in folder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(timeout) && Volatile.Read(ref shown) == 1;
    }

    private static bool ShowHere(string folder, IReadOnlyList<string> items)
    {
        var init = CoInitializeEx(0, COINIT_APARTMENTTHREADED);
        var lists = new List<nint>();
        try
        {
            if (SHParseDisplayName(folder, 0, out var folderList, 0, out _) < 0 || folderList == 0) return false;
            lists.Add(folderList);
            var selected = new nint[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                if (SHParseDisplayName(items[i], 0, out var item, 0, out _) < 0 || item == 0) return false;
                lists.Add(item);
                selected[i] = item;
            }
            fixed (nint* array = selected)
                return SHOpenFolderAndSelectItems(folderList, (uint)selected.Length, array, 0) >= 0;
        }
        finally
        {
            foreach (var list in lists) CoTaskMemFree(list);
            if (init >= 0) CoUninitialize();
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint idList, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(nint folder, uint count, nint* items, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint memory);
}
