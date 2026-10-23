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
/// A read that fails at DragEnter (the source is busy, has not rendered CF_HDROP yet, or sent nothing usable) does not
/// leave the drag dead: the target keeps the drag's data object, which OLE allows between DragEnter and
/// DragLeave/Drop, and reads it again from DragOver, at most every <see cref="RetryInterval"/> and
/// <see cref="MaxReads"/> times in all. Nothing waits or loops: each read happens inside a callback OLE makes anyway.
/// Files read once belong to the overlay session (until <see cref="Reset"/>), so a later failed read, at re-entry or at
/// the drop, falls back to them instead of losing the drag.
/// </para>
/// <para>
/// COM lifetime: the data object arrives as a runtime-callable wrapper. The target only drops its reference (DragLeave,
/// Drop, Reset); it never calls <see cref="Marshal.ReleaseComObject"/>, because the runtime hands the same wrapper to
/// later calls of the same drag (Drop gets it again) and releasing it early would break them. The wrapper releases the
/// source's object when it is collected.
/// </para>
/// </summary>
public sealed class FileDropTarget : IDropTarget
{
    /// <summary>Reads per drag, the first included: about two seconds of retries at <see cref="RetryInterval"/>.</summary>
    internal const int MaxReads = 20;
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    private readonly IDropSink _sink;
    private readonly TimeProvider _time;
    private IReadOnlyList<string> _sessionPaths = []; // the last good read of this overlay session
    private IDataObject? _pending;                     // kept only while a read is owed, released at the end of the drag
    private int _reads;
    private long _lastRead;
    private bool _copyAllowed;

    internal FileDropTarget(IDropSink sink, TimeProvider? time = null)
    {
        _sink = sink;
        _time = time ?? TimeProvider.System;
    }

    // COPY over a command, and only if the source allows COPY; NONE everywhere else.
    private uint Effect(bool overCommand) => _sessionPaths.Count > 0 && _pending is null && _copyAllowed && overCommand ? Ole32.DROPEFFECT_COPY : Ole32.DROPEFFECT_NONE;

    public int DragEnter(IDataObject dataObject, uint keyState, POINTL pt, ref uint effect)
    {
        _pending = null;
        _reads = 0;
        _copyAllowed = (effect & Ole32.DROPEFFECT_COPY) != 0;
        if (!_copyAllowed)
        {
            effect = Effect(_sink.OnDragEnter([], false, pt.X, pt.Y));
            return 0;
        }
        var read = Read(dataObject);
        if (read.Status == DropReadStatus.Files) _sessionPaths = read.Paths;
        else if (_sessionPaths.Count > 0) Report(DropDataStage.SessionFallback, read);
        else
        {
            _pending = dataObject;
            Report(DropDataStage.Unavailable, read);
        }
        effect = Effect(_sink.OnDragEnter(_pending is null ? _sessionPaths : [], _pending is not null, pt.X, pt.Y));
        return 0;
    }

    public int DragOver(uint keyState, POINTL pt, ref uint effect)
    {
        _copyAllowed = (effect & Ole32.DROPEFFECT_COPY) != 0;
        if (_pending is { } data && _time.GetElapsedTime(_lastRead) >= RetryInterval)
        {
            var read = Read(data);
            if (read.Status == DropReadStatus.Files || _reads >= MaxReads)
            {
                _pending = null;
                if (read.Status == DropReadStatus.Files) _sessionPaths = read.Paths;
                Report(read.Status == DropReadStatus.Files ? DropDataStage.Recovered : DropDataStage.GaveUp, read);
                effect = Effect(_sink.OnFilesResolved(_sessionPaths, pt.X, pt.Y));
                return 0;
            }
            Report(DropDataStage.Retry, read);
        }
        effect = Effect(_sink.OnDragOver(pt.X, pt.Y));
        return 0;
    }

    public int DragLeave()
    {
        _pending = null; // the session's files stay: a drag that comes back is the same drag until Reset
        _sink.OnDragLeave();
        return 0;
    }

    public int Drop(IDataObject dataObject, uint keyState, POINTL pt, ref uint effect)
    {
        _pending = null;
        var copyAllowed = (effect & Ole32.DROPEFFECT_COPY) != 0;
        IReadOnlyList<string> paths = [];
        if (copyAllowed)
        {
            var read = HDrop.Read(dataObject);
            if (read.Status == DropReadStatus.Files) paths = read.Paths;
            else
            {
                paths = _sessionPaths;
                Report(paths.Count > 0 ? DropDataStage.SessionFallback : DropDataStage.DropUnavailable, read);
            }
        }
        var accepted = _sink.OnDrop(paths, pt.X, pt.Y, copyAllowed);
        effect = accepted ? Ole32.DROPEFFECT_COPY : Ole32.DROPEFFECT_NONE;
        return 0;
    }

    /// <summary>The overlay session ended (the ring was shown or hidden): forget its files and any data object.</summary>
    internal void Reset()
    {
        _pending = null;
        _sessionPaths = [];
        _reads = 0;
    }

    private DropRead Read(IDataObject data)
    {
        _reads++;
        _lastRead = _time.GetTimestamp();
        return HDrop.Read(data);
    }

    private void Report(DropDataStage stage, DropRead read) =>
        _sink.OnDropData(new DropDataEvent(stage, read.Status, read.HResult, _reads, stage is DropDataStage.SessionFallback or DropDataStage.Recovered ? _sessionPaths.Count : 0));
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
            if (medium.unionmember != 0 || medium.pUnkForRelease is not null) Ole32.ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>Parses the block inside the bounds GlobalSize reports: a hostile source cannot make it read past them.</summary>
    private static unsafe DropRead Parse(nint hGlobal)
    {
        var size = Kernel32.GlobalSize(hGlobal);
        if (size is 0 or > DropFiles.MaxBlockBytes) return DropRead.Of(DropReadStatus.Malformed);
        var p = Kernel32.GlobalLock(hGlobal);
        if (p == 0) return DropRead.Of(DropReadStatus.Failed, Marshal.GetLastPInvokeError());
        try
        {
            var status = DropFiles.Parse(new ReadOnlySpan<byte>((void*)p, (int)size), out var paths);
            return new DropRead(status, paths);
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

    public static DropReadStatus Parse(ReadOnlySpan<byte> block, out string[] paths)
    {
        paths = [];
        if (block.Length < HeaderBytes) return DropReadStatus.Malformed;
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(block);
        var wide = BinaryPrimitives.ReadInt32LittleEndian(block[16..]) != 0;
        if (offset < HeaderBytes || offset >= (uint)block.Length) return DropReadStatus.Malformed;
        var list = block[(int)offset..];
        var found = new List<string>();
        while (true)
        {
            var end = wide ? IndexOfWideNull(list) : list.IndexOf((byte)0);
            if (end < 0) return DropReadStatus.Malformed;                // runs off the end of the block
            if (end == 0) break;                                         // the empty path that ends the list
            var chars = wide ? end / 2 : end;
            if (chars > MaxPathChars || found.Count == MaxFiles) return DropReadStatus.Malformed;
            found.Add(wide ? new string(MemoryMarshal.Cast<byte, char>(list[..end])) : Ansi(list[..end]));
            list = list[(end + (wide ? 2 : 1))..];
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

    private static unsafe string Ansi(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* b = bytes)
        {
            var n = Kernel32.MultiByteToWideChar(Kernel32.CP_ACP, 0, b, bytes.Length, null, 0);
            if (n <= 0) return "";
            var chars = new char[n];
            fixed (char* c = chars) n = Kernel32.MultiByteToWideChar(Kernel32.CP_ACP, 0, b, bytes.Length, c, n);
            return new string(chars, 0, Math.Max(n, 0));
        }
    }
}
