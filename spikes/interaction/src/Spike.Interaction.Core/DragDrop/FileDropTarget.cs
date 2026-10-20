using System.Diagnostics;
using System.Runtime.InteropServices.ComTypes;
using Spike.Interaction.Core.Native;

namespace Spike.Interaction.Core.DragDrop;

public readonly record struct DragPosition(int ScreenX, int ScreenY, uint KeyState, long Timestamp);
public sealed record DropResult(IReadOnlyList<string> Paths, int ScreenX, int ScreenY, uint KeyState, long Timestamp);

/// <summary>
/// Managed IDropTarget that accepts CF_HDROP and always reports DROPEFFECT_COPY.
/// It never returns DROPEFFECT_MOVE: for shell data objects a MOVE effect tells the source it may delete
/// the originals, which would violate the file-safety rule. The callbacks run on the window's thread.
/// </summary>
public sealed class FileDropTarget : IDropTarget
{
    private bool _hasFiles;

    public event Action<DragPosition>? Entered;
    public event Action<DragPosition>? Moved;
    public event Action? Left;
    public event Action<DropResult>? Dropped;

    public int DragEnter(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
    {
        _hasFiles = HDropReader.HasFileDrop(pDataObj);
        pdwEffect = _hasFiles ? DROPEFFECT.COPY : DROPEFFECT.NONE;
        Entered?.Invoke(new DragPosition(pt.X, pt.Y, grfKeyState, Stopwatch.GetTimestamp()));
        return HRESULTS.S_OK;
    }

    public int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect)
    {
        pdwEffect = _hasFiles ? DROPEFFECT.COPY : DROPEFFECT.NONE;
        Moved?.Invoke(new DragPosition(pt.X, pt.Y, grfKeyState, Stopwatch.GetTimestamp()));
        return HRESULTS.S_OK;
    }

    public int DragLeave()
    {
        _hasFiles = false;
        Left?.Invoke();
        return HRESULTS.S_OK;
    }

    public int Drop(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
    {
        IReadOnlyList<string> paths = Array.Empty<string>();
        try { paths = HDropReader.ReadPaths(pDataObj); } catch { }
        pdwEffect = paths.Count > 0 ? DROPEFFECT.COPY : DROPEFFECT.NONE;
        _hasFiles = false;
        Dropped?.Invoke(new DropResult(paths, pt.X, pt.Y, grfKeyState, Stopwatch.GetTimestamp()));
        return HRESULTS.S_OK;
    }
}
