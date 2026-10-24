using System.Runtime.InteropServices;

namespace WinTools.Windows.Shell;

/// <summary>
/// Puts plain text on the clipboard (Copy path, V2 PR 1.4), with the Win32 calls documented for it: OpenClipboard with
/// an owner window (with none, EmptyClipboard leaves no owner and SetClipboardData fails), EmptyClipboard, a movable
/// global block of UTF-16 text with its terminator for CF_UNICODETEXT, then CloseClipboard. After a successful
/// SetClipboardData the system owns the block; after a failed one it is freed here. Call it on the owner window's thread.
/// </summary>
public static partial class ClipboardText
{
    private const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;

    /// <summary>False when another program holds the clipboard or memory is short; the caller may try again.</summary>
    public static bool TrySet(nint owner, string text)
    {
        if (!OpenClipboard(owner)) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var block = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (block == 0) return false;
            var target = GlobalLock(block);
            if (target == 0) { GlobalFree(block); return false; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally { GlobalUnlock(block); }
            if (SetClipboardData(CF_UNICODETEXT, block) != 0) return true;
            GlobalFree(block);
            return false;
        }
        finally { CloseClipboard(); }
    }

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalFree(nint memory);
}
