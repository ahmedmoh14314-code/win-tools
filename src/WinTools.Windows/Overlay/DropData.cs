namespace WinTools.Windows.Overlay;

/// <summary>What the drop target tells its owner (the overlay), on the overlay's STA thread. A seam: tests drive the
/// target with fake data objects.</summary>
internal interface IDropSink
{
    /// <summary>A drag entered. <paramref name="pending"/>: the files could not be read yet and are being retried, so
    /// the owner keeps the waiting ring instead of showing "No actions".</summary>
    bool OnDragEnter(IReadOnlyList<string> paths, bool pending, int x, int y);
    bool OnDragOver(int x, int y);
    /// <summary>A pending read ended: the files were recovered, or the retries ran out (<paramref name="paths"/> empty).</summary>
    bool OnFilesResolved(IReadOnlyList<string> paths, int x, int y);
    void OnDragLeave();
    bool OnDrop(IReadOnlyList<string> paths, int x, int y, bool copyAllowed);
    /// <summary>Diagnostics for the log: what happened to the drag's data, never the paths themselves.</summary>
    void OnDropData(DropDataEvent report);
}

/// <summary>The outcome of one CF_HDROP read.</summary>
public enum DropReadStatus
{
    Files,
    /// <summary>QueryGetData did not offer CF_HDROP as an HGLOBAL (the HRESULT says why).</summary>
    NotOffered,
    /// <summary>GetData failed, or returned no memory.</summary>
    Failed,
    /// <summary>A well-formed list with no files in it.</summary>
    Empty,
    /// <summary>The DROPFILES block is out of bounds, unterminated, or lists more than <see cref="DropFiles.MaxFiles"/>.</summary>
    Malformed,
}

/// <summary>What happened to the drag's data: logged as <c>drag.data.&lt;stage&gt;</c>.</summary>
public enum DropDataStage
{
    /// <summary>DragEnter could not read the files; retries follow on DragOver.</summary>
    Unavailable,
    Retry,
    Recovered,
    /// <summary>The retries ran out: the ring shows that nothing is available.</summary>
    GaveUp,
    /// <summary>A read failed, but this overlay session already has the drag's files (read earlier): those are used.</summary>
    SessionFallback,
    /// <summary>The drop's read failed and the session has no files: nothing runs.</summary>
    DropUnavailable,
}

/// <summary>One diagnostic about the drag's data: no paths, only the status, the HRESULT, the read number and a count.</summary>
public readonly record struct DropDataEvent(DropDataStage Stage, DropReadStatus Status, int HResult, int Read, int Files);

/// <summary>One CF_HDROP read.</summary>
internal readonly record struct DropRead(DropReadStatus Status, IReadOnlyList<string> Paths, int HResult = 0)
{
    public static DropRead Of(DropReadStatus status, int hr = 0) => new(status, [], hr);
}
