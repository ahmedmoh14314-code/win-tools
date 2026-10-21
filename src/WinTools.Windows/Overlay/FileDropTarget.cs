using System.Runtime.InteropServices.ComTypes;
using WinTools.Windows.Native;

namespace WinTools.Windows.Overlay;

/// <summary>
/// The overlay's OLE drop target. Accepts only CF_HDROP, from sources that allow COPY. The effect is DROPEFFECT_COPY over a valid command and
/// DROPEFFECT_NONE everywhere else; it is never DROPEFFECT_MOVE, which would let a shell source delete the originals.
/// Callbacks run on the overlay's (STA) thread.
/// </summary>
public sealed class FileDropTarget(RadialOverlay overlay) : IDropTarget
{
    private bool _hasFiles, _copyAllowed;

    // COPY over a command, and only if the source allows COPY; NONE everywhere else.
    private uint Effect(POINTL pt) => _hasFiles && _copyAllowed && overlay.IsCommandAt(pt.X, pt.Y) ? Ole32.DROPEFFECT_COPY : Ole32.DROPEFFECT_NONE;

    public int DragEnter(IDataObject dataObject, uint keyState, POINTL pt, ref uint effect)
    {
        var paths = HDrop.TryRead(dataObject);
        _hasFiles = paths.Count > 0;
        _copyAllowed = (effect & Ole32.DROPEFFECT_COPY) != 0;
        overlay.OnDragEnter(_copyAllowed ? paths : [], pt.X, pt.Y);
        effect = Effect(pt);
        return 0;
    }

    public int DragOver(uint keyState, POINTL pt, ref uint effect)
    {
        overlay.OnDragOver(pt.X, pt.Y);
        _copyAllowed = (effect & Ole32.DROPEFFECT_COPY) != 0;
        effect = Effect(pt);
        return 0;
    }

    public int DragLeave()
    {
        _hasFiles = false;
        overlay.OnDragLeave();
        return 0;
    }

    public int Drop(IDataObject dataObject, uint keyState, POINTL pt, ref uint effect)
    {
        _hasFiles = false;
        var copyAllowed = (effect & Ole32.DROPEFFECT_COPY) != 0;
        var accepted = overlay.OnDrop(copyAllowed ? HDrop.TryRead(dataObject) : [], pt.X, pt.Y, copyAllowed);
        effect = accepted ? Ole32.DROPEFFECT_COPY : Ole32.DROPEFFECT_NONE;
        return 0;
    }
}

/// <summary>Reads file paths from a data object's CF_HDROP format; empty when there is none.</summary>
internal static class HDrop
{
    public static IReadOnlyList<string> TryRead(IDataObject data)
    {
        var format = new FORMATETC { cfFormat = Ole32.CF_HDROP, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        STGMEDIUM medium = default;
        try
        {
            if (data.QueryGetData(ref format) != 0) return [];
            data.GetData(ref format, out medium);
            return medium.unionmember == 0 ? [] : Parse(medium.unionmember);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return [];
        }
        finally
        {
            if (medium.unionmember != 0) Ole32.ReleaseStgMedium(ref medium);
        }
    }

    private static string[] Parse(nint hDrop)
    {
        var count = Shell32.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        if (count > 10_000) return [];
        var result = new string[count];
        for (uint i = 0; i < count; i++)
        {
            var len = Shell32.DragQueryFileW(hDrop, i, null, 0);
            var buf = new char[len + 1];
            var got = Shell32.DragQueryFileW(hDrop, i, buf, (uint)buf.Length);
            result[i] = new string(buf, 0, (int)got);
        }
        return result;
    }
}
