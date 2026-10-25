using System.Runtime.InteropServices;
using System.Text;

namespace WinTools.Worker.Archive;

/// <summary>
/// The bytes a typed ZIP password may have been encrypted with (V2 PR 5.3, ADR-0027). ZIP never said how to encode one:
/// WinZip AES and Info-ZIP use UTF-8; WinRAR's ZipCrypto used the ANSI code page, with "?" for what it cannot encode
/// (measured, 2026-10-25); DOS-era tools the OEM one. The candidates are tried in that order, without duplicates, so a
/// password of plain ASCII is one candidate. The code-page forms come from Windows' own conversion with its default
/// best-fit mapping, which is what those tools call.
/// </summary>
internal static class ZipPasswords
{
    public static IReadOnlyList<byte[]> Candidates(string typed, string? normalized) => Candidates(typed, normalized, (int)GetACP(), (int)GetOEMCP());

    public static IReadOnlyList<byte[]> Candidates(string typed, string? normalized, int ansiCodePage, int oemCodePage)
    {
        var list = new List<byte[]>(4);
        void Add(byte[]? bytes)
        {
            if (bytes is { Length: > 0 } && !list.Any(b => b.AsSpan().SequenceEqual(bytes))) list.Add(bytes);
        }
        Add(Encoding.UTF8.GetBytes(typed));
        if (normalized is not null) Add(Encoding.UTF8.GetBytes(normalized));
        Add(InCodePage(typed, ansiCodePage));
        Add(InCodePage(typed, oemCodePage));
        return list;
    }

    private const int CodePageUtf8 = 65001;

    /// <summary>The text in a Windows code page, best fit and "?" for the rest; null when Windows cannot convert it.</summary>
    private static byte[]? InCodePage(string text, int codePage)
    {
        if (codePage == CodePageUtf8) return Encoding.UTF8.GetBytes(text); // a duplicate of the first candidate
        var size = WideCharToMultiByte((uint)codePage, 0, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero);
        if (size <= 0) return null;
        var bytes = new byte[size];
        return WideCharToMultiByte((uint)codePage, 0, text, text.Length, bytes, size, IntPtr.Zero, IntPtr.Zero) == size ? bytes : null;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WideCharToMultiByte(uint codePage, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string text, int chars,
        byte[]? bytes, int size, IntPtr defaultChar, IntPtr usedDefaultChar);
}
