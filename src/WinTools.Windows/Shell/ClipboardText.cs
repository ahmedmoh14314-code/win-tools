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

    /// <summary>
    /// False when another program holds the clipboard or memory is short; the caller may try again. The memory is
    /// prepared before the clipboard is emptied, so a failure never wipes what the user had copied. The paths carry the
    /// user's folder names, so the text is marked not to be uploaded to the cloud clipboard ("CanUploadToCloudClipboard"
    /// = 0, Microsoft's documented clipboard format); local clipboard history keeps it as for any copy.
    /// </summary>
    public static bool TrySet(nint owner, string text)
    {
        var block = Block(text);
        if (block == 0) return false;
        var noUpload = Dword(0);
        if (!OpenClipboard(owner)) { GlobalFree(block); Free(noUpload); return false; }
        try
        {
            if (!EmptyClipboard()) { GlobalFree(block); Free(noUpload); return false; }
            if (SetClipboardData(CF_UNICODETEXT, block) == 0) { GlobalFree(block); Free(noUpload); return false; }
            var format = RegisterClipboardFormatW("CanUploadToCloudClipboard");
            if (noUpload != 0 && (format == 0 || SetClipboardData(format, noUpload) == 0)) GlobalFree(noUpload);
            return true;
        }
        finally { CloseClipboard(); }
    }

    private static void Free(nint block) { if (block != 0) GlobalFree(block); }

    private static nint Block(string text)
    {
        var block = GlobalAlloc(GMEM_MOVEABLE, (nuint)((text.Length + 1) * sizeof(char)));
        if (block == 0) return 0;
        var target = GlobalLock(block);
        if (target == 0) { GlobalFree(block); return 0; }
        try
        {
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
        }
        finally { GlobalUnlock(block); }
        return block;
    }

    private static nint Dword(int value)
    {
        var block = GlobalAlloc(GMEM_MOVEABLE, 4);
        if (block == 0) return 0;
        var target = GlobalLock(block);
        if (target == 0) { GlobalFree(block); return 0; }
        Marshal.WriteInt32(target, value);
        GlobalUnlock(block);
        return block;
    }

    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalFree(nint memory);
}
