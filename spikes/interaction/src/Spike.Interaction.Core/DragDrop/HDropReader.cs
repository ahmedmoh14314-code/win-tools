using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Spike.Interaction.Core.Native;

namespace Spike.Interaction.Core.DragDrop;

/// <summary>Extracts file paths from a data object's CF_HDROP format.</summary>
public static class HDropReader
{
    public static bool HasFileDrop(IDataObject data)
    {
        var fmt = HDropFormat();
        try { return data.QueryGetData(ref fmt) == HRESULTS.S_OK; }
        catch { return false; }
    }

    public static IReadOnlyList<string> ReadPaths(IDataObject data)
    {
        var fmt = HDropFormat();
        STGMEDIUM medium = default;
        try
        {
            data.GetData(ref fmt, out medium);
            if (medium.unionmember == 0) return Array.Empty<string>();
            return ReadPathsFromHDrop(medium.unionmember);
        }
        finally
        {
            if (medium.unionmember != 0) Ole32.ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>Parses an HDROP (HGLOBAL holding DROPFILES + double-null-terminated path list) via DragQueryFileW.</summary>
    public static IReadOnlyList<string> ReadPathsFromHDrop(nint hDrop)
    {
        var count = Shell32.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        var result = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var len = Shell32.DragQueryFileW(hDrop, i, null, 0);
            var buf = new char[len + 1];
            var got = Shell32.DragQueryFileW(hDrop, i, buf, (uint)buf.Length);
            result.Add(new string(buf, 0, (int)got));
        }
        return result;
    }

    private static FORMATETC HDropFormat() => new()
    {
        cfFormat = (short)CF.HDROP,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = TYMED.TYMED_HGLOBAL,
    };

    /// <summary>Builds an HGLOBAL-style DROPFILES block in unmanaged memory (for tests and the drag-source harness).</summary>
    public static nint BuildDropFiles(IEnumerable<string> paths)
    {
        const int dropFilesSize = 20; // DROPFILES: pFiles(4) pt(8) fNC(4) fWide(4)
        var joined = string.Join('\0', paths) + "\0\0";
        var bytes = joined.Length * 2;
        var hMem = Marshal.AllocHGlobal(dropFilesSize + bytes);
        unsafe
        {
            var p = (byte*)hMem;
            new Span<byte>(p, dropFilesSize + bytes).Clear();
            *(int*)p = dropFilesSize;      // pFiles
            *(int*)(p + 16) = 1;           // fWide
            fixed (char* src = joined) Buffer.MemoryCopy(src, p + dropFilesSize, bytes, bytes);
        }
        return hMem;
    }
}
