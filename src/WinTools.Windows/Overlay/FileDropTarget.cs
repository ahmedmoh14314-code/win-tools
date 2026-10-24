using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using WinTools.Windows.Native;

namespace WinTools.Windows.Overlay;

/// <summary>
/// The overlay's OLE drop target. Accepts only CF_HDROP, from sources that allow COPY. The effect is DROPEFFECT_COPY over
/// a command and DROPEFFECT_NONE everywhere else; it is never DROPEFFECT_MOVE, which would let a shell source delete the
/// originals. Whether a drop on a command runs it is decided at the drop (the arm delay). Callbacks run on the
/// overlay's (STA) thread.
/// <para>
/// A read that fails (the source is busy, has not rendered CF_HDROP yet, or sent nothing usable) does not leave the drag
/// dead: the target keeps the drag's data object, which OLE allows between DragEnter and DragLeave/Drop, and reads it
/// again from DragOver. Reads are throttled (<see cref="RetryInterval"/> apart, counted from the end of the last one),
/// bounded per drag (<see cref="MaxReads"/>), and stop early when the source is slow (<see cref="SlowRead"/> for one
/// read, <see cref="ReadBudget"/> in all), because each read is a synchronous call into the source on this UI thread.
/// Nothing waits or loops: every read happens inside a callback OLE makes anyway.
/// </para>
/// <para>
/// Files read once belong to the drag until <see cref="Reset"/> (the ring shown or hidden, or the button released
/// outside the ring: a later DragEnter then needs a new press, so it is another drag). A later failed read, at
/// re-entry or at the drop, falls back to them.
/// </para>
/// <para>
/// COM lifetime: each data object arrives through COM interop as a runtime-callable wrapper whose count went up by one
/// for that call. The target gives exactly that one count back (<see cref="Marshal.ReleaseComObject"/>) when it is done
/// with the object: at the end of the call, or, for a data object kept for a retry, when the read resolves or on
/// DragLeave, Drop and Reset. It never uses FinalReleaseComObject. Without this the source's object would live until a
/// garbage collection, which in an idle resident host can be hours.
/// </para>
/// </summary>
public sealed class FileDropTarget : IDropTarget
{
    /// <summary>Reads per drag, the first included: about two seconds of retries at <see cref="RetryInterval"/>.</summary>
    internal const int MaxReads = 20;
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    /// <summary>One read slower than this, or all reads together slower than <see cref="ReadBudget"/>, ends the retries.</summary>
    internal static readonly TimeSpan SlowRead = TimeSpan.FromMilliseconds(200), ReadBudget = TimeSpan.FromMilliseconds(500);

    private readonly IDropSink _sink;
    private readonly TimeProvider _time;
    private IReadOnlyList<string> _dragPaths = []; // the last good read of this drag
    private IDataObject? _pending;                  // kept only while a read is owed
    private int _reads;
    private long _lastReadEnd;
    private TimeSpan _readTime;
    private bool _gaveUp, _copyAllowed;

    internal FileDropTarget(IDropSink sink, TimeProvider? time = null)
    {
        _sink = sink;
        _time = time ?? TimeProvider.System;
    }

    // COPY over a command, and only if the source allows COPY; NONE everywhere else (and while a read is owed).
    private uint Effect(bool overCommand) => _dragPaths.Count > 0 && _pending is null && _copyAllowed && overCommand ? Ole32.DROPEFFECT_COPY : Ole32.DROPEFFECT_NONE;

    public int DragEnter(IDataObject dataObject, uint keyState, POINTL pt, ref uint effect)
    {
        Let(ref _pending);
        _copyAllowed = (effect & Ole32.DROPEFFECT_COPY) != 0;
        var pending = false;
        if (_copyAllowed && _dragPaths.Count == 0 && !_gaveUp)
        {
            if (CanRead())
            {
                var read = Read(dataObject);
                if (read.Status == DropReadStatus.Files) _dragPaths = read.Paths;
                else if (EndOfRetries()) GiveUp(read);
                else { pending = true; Report(DropDataStage.Unavailable, read); }
            }
            else pending = true; // re-entered within the retry interval: the next DragOver reads
        }
        if (pending) _pending = dataObject; // keeps this call's count until the read resolves or the drag ends
        else Release(dataObject);
        effect = Effect(_sink.OnDragEnter(_copyAllowed && !pending ? _dragPaths : [], pending, pt.X, pt.Y));
        return 0;
    }

    public int DragOver(uint keyState, POINTL pt, ref uint effect)
    {
        _copyAllowed = (effect & Ole32.DROPEFFECT_COPY) != 0;
        if (_pending is { } data && CanRead())
        {
            var read = Read(data);
            if (read.Status == DropReadStatus.Files || EndOfRetries())
            {
                Let(ref _pending);
                if (read.Status == DropReadStatus.Files)
                {
                    _dragPaths = read.Paths;
                    Report(DropDataStage.Recovered, read);
                }
                else GiveUp(read);
                effect = Effect(_sink.OnFilesResolved(_dragPaths, pt.X, pt.Y));
                return 0;
            }
            Report(DropDataStage.Retry, read);
        }
        effect = Effect(_sink.OnDragOver(pt.X, pt.Y));
        return 0;
    }

    public int DragLeave()
    {
        Let(ref _pending); // the drag's files stay: a drag that comes back is the same drag until Reset
        _sink.OnDragLeave();
        return 0;
    }

    public int Drop(IDataObject dataObject, uint keyState, POINTL pt, ref uint effect)
    {
        Let(ref _pending);
        var copyAllowed = (effect & Ole32.DROPEFFECT_COPY) != 0;
        IReadOnlyList<string> paths = [];
        if (copyAllowed)
        {
            var read = HDrop.Read(dataObject);
            if (read.Status == DropReadStatus.Files) paths = read.Paths;
            else
            {
                paths = _dragPaths;
                Report(paths.Count > 0 ? DropDataStage.SessionFallback : DropDataStage.DropUnavailable, read);
            }
        }
        Release(dataObject);
        var accepted = _sink.OnDrop(paths, pt.X, pt.Y, copyAllowed);
        effect = accepted ? Ole32.DROPEFFECT_COPY : Ole32.DROPEFFECT_NONE;
        return 0;
    }

    /// <summary>The drag is over for the overlay (the ring was shown or hidden, or the button was released outside it):
    /// forget its files and give back any data object still held.</summary>
    internal void Reset()
    {
        Let(ref _pending);
        _dragPaths = [];
        _reads = 0;
        _readTime = TimeSpan.Zero;
        _gaveUp = false;
    }

    private bool CanRead() => _reads == 0 || _time.GetElapsedTime(_lastReadEnd) >= RetryInterval;

    private bool EndOfRetries() => _reads >= MaxReads || _readTime >= ReadBudget;

    private DropRead Read(IDataObject data)
    {
        var start = _time.GetTimestamp();
        var read = HDrop.Read(data);
        _lastReadEnd = _time.GetTimestamp();
        var took = _time.GetElapsedTime(start, _lastReadEnd);
        _reads++;
        _readTime += took >= SlowRead ? ReadBudget : took; // one slow read is enough to stop retrying
        return read;
    }

    private void GiveUp(DropRead read)
    {
        _gaveUp = true;
        Report(DropDataStage.GaveUp, read);
    }

    private void Report(DropDataStage stage, DropRead read) =>
        _sink.OnDropData(new DropDataEvent(stage, read.Status, read.HResult, _reads, stage is DropDataStage.SessionFallback or DropDataStage.Recovered ? _dragPaths.Count : 0));

    private static void Let(ref IDataObject? held)
    {
        if (held is null) return;
        Release(held);
        held = null;
    }

    /// <summary>Gives back the one count this call's marshalling added (a managed object, as in tests, has none).</summary>
    private static void Release(object data)
    {
        if (Marshal.IsComObject(data)) Marshal.ReleaseComObject(data);
    }
}

/// <summary>Reads file paths from a data object's CF_HDROP format, with the reason when there are none.</summary>
internal static class HDrop
{
    public static DropRead Read(IDataObject data)
    {
        var format = new FORMATETC { cfFormat = Ole32.CF_HDROP, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        STGMEDIUM medium = default;
        try
        {
            var hr = data.QueryGetData(ref format);
            if (hr != 0) return DropRead.Of(DropReadStatus.NotOffered, hr);
            data.GetData(ref format, out medium);
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == 0) return DropRead.Of(DropReadStatus.Failed);
            return Parse(medium.unionmember);
        }
        catch (Exception ex) // whatever the source's HRESULT maps to (COMException, ArgumentException, OutOfMemoryException...)
        {
            return DropRead.Of(DropReadStatus.Failed, ex.HResult);
        }
        finally
        {
            var releaser = medium.pUnkForRelease;
            if (medium.unionmember != 0 || releaser is not null) Ole32.ReleaseStgMedium(ref medium);
            if (releaser is not null && Marshal.IsComObject(releaser)) Marshal.ReleaseComObject(releaser); // the wrapper's own count
        }
    }

    /// <summary>Parses the block inside the bounds GlobalSize reports: a hostile source cannot make it read past them.</summary>
    private static unsafe DropRead Parse(nint hGlobal)
    {
        var size = Kernel32.GlobalSize(hGlobal);
        if (size == 0) return DropRead.Of(DropReadStatus.Failed, Marshal.GetHRForLastWin32Error());
        if (size > DropFiles.MaxBlockBytes) return DropRead.Of(DropReadStatus.Malformed);
        var p = Kernel32.GlobalLock(hGlobal);
        if (p == 0) return DropRead.Of(DropReadStatus.Failed, Marshal.GetHRForLastWin32Error());
        try
        {
            var status = DropFiles.Parse(new ReadOnlySpan<byte>((void*)p, (int)size), out var paths);
            return new DropRead(status, Array.ConvertAll(paths, ShortNames.Expand)); // Explorer's 8.3 names for long paths
        }
        finally { Kernel32.GlobalUnlock(hGlobal); }
    }
}

/// <summary>The DROPFILES block (shlobj_core.h) behind CF_HDROP: a 20-byte header, then a list of null-terminated paths
/// ending with an empty one, as UTF-16 (fWide) or in the system ANSI code page.</summary>
internal static class DropFiles
{
    public const int MaxFiles = 10_000;
    public const int HeaderBytes = 20;
    public const int MaxPathChars = 32_767;
    public const nuint MaxBlockBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Every path must be terminated inside the block. The empty path that ends the list may be missing when the block
    /// ends right after a terminated path (some sources omit it; DragQueryFileW read past the end to find one).
    /// </summary>
    public static DropReadStatus Parse(ReadOnlySpan<byte> block, out string[] paths)
    {
        paths = [];
        if (block.Length < HeaderBytes) return DropReadStatus.Malformed;
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(block);
        var wide = BinaryPrimitives.ReadInt32LittleEndian(block[16..]) != 0;
        if (offset < HeaderBytes || offset >= (uint)block.Length) return DropReadStatus.Malformed;
        var list = block[(int)offset..];
        var unit = wide ? 2 : 1;
        var found = new List<string>();
        while (list.Length >= unit)
        {
            var end = wide ? IndexOfWideNull(list) : list.IndexOf((byte)0);
            if (end < 0) return DropReadStatus.Malformed;                // a path runs off the end of the block
            if (end == 0) break;                                         // the empty path that ends the list
            if (end / unit > MaxPathChars || found.Count == MaxFiles) return DropReadStatus.Malformed;
            var path = wide ? new string(MemoryMarshal.Cast<byte, char>(list[..end])) : Ansi(list[..end]);
            if (path.Length == 0) return DropReadStatus.Malformed;
            found.Add(path);
            list = list[(end + unit)..];
        }
        paths = [.. found];
        return paths.Length == 0 ? DropReadStatus.Empty : DropReadStatus.Files;
    }

    /// <summary>Byte offset of the first UTF-16 null at an even offset, or -1.</summary>
    private static int IndexOfWideNull(ReadOnlySpan<byte> list)
    {
        for (var i = 0; i + 1 < list.Length; i += 2)
            if (list[i] == 0 && list[i + 1] == 0) return i;
        return -1;
    }

    /// <summary>The system ANSI code page's text as UTF-16; empty when it does not convert.</summary>
    private static unsafe string Ansi(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* b = bytes)
        {
            var n = Kernel32.MultiByteToWideChar(Kernel32.CP_ACP, 0, b, bytes.Length, null, 0);
            if (n <= 0) return "";
            var chars = new char[n];
            fixed (char* c = chars) n = Kernel32.MultiByteToWideChar(Kernel32.CP_ACP, 0, b, bytes.Length, c, n);
            return n <= 0 ? "" : new string(chars, 0, n);
        }
    }
}
