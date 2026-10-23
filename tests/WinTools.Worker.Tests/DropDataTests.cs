using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using WinTools.Windows.Native;
using WinTools.Windows.Overlay;

namespace WinTools.Worker.Tests;

/// <summary>
/// The overlay's drop target against fake data objects (V2 plan, Stage 0 blocker 2B): a CF_HDROP read that fails at
/// DragEnter must not leave the drag dead, the drop must not lose files the session already read, a hostile DROPFILES
/// block must be rejected inside its bounds, and the drag's data object must be let go when the drag ends.
/// </summary>
public sealed partial class DropDataTests
{
    private const uint Copy = Ole32.DROPEFFECT_COPY, CopyOrMove = 3, MoveOnly = 2;
    private static readonly string[] Two = [@"C:\images\a.png", @"C:\images\b صورة.jpg"];

    [Fact]
    public void Files_are_read_at_once_on_drag_enter()
    {
        var (target, sink, _) = Create();
        var data = new FakeData(_ => Reply.Files(Two));
        Assert.Equal(Copy, Enter(target, data));
        Assert.Equal(Two, sink.Entered);
        Assert.False(sink.Pending);
        Assert.Empty(sink.Reports);
    }

    [Fact]
    public void A_failed_first_read_shows_waiting_then_recovers_on_drag_over()
    {
        var (target, sink, time) = Create();
        var data = new FakeData(read => read == 1 ? Reply.NotOffered(unchecked((int)0x8000FFFF)) : Reply.Files(Two));

        Assert.Equal(Ole32.DROPEFFECT_NONE, Enter(target, data));
        Assert.True(sink.Pending);                 // the waiting ring, not "No actions"
        Assert.Empty(sink.Entered!);

        Assert.Equal(Ole32.DROPEFFECT_NONE, Over(target)); // too soon: throttled, no read
        Assert.Equal(1, data.Reads);

        time.Advance(FileDropTarget.RetryInterval);
        Assert.Equal(Copy, Over(target));
        Assert.Equal(2, data.Reads);
        Assert.Equal(Two, sink.Resolved);
        Assert.Equal([DropDataStage.Unavailable, DropDataStage.Recovered], sink.Reports.Select(r => r.Stage));
        Assert.Equal(unchecked((int)0x8000FFFF), sink.Reports[0].HResult);
        Assert.Equal(2, sink.Reports[1].Files);
    }

    [Fact]
    public void A_busy_source_and_an_empty_list_are_retried_too()
    {
        var (target, sink, time) = Create();
        var data = new FakeData(read => read switch
        {
            1 => Reply.Throws(unchecked((int)0x8001010A)), // RPC_E_SERVERCALL_RETRYLATER
            2 => Reply.Files([]),
            _ => Reply.Files(Two),
        });
        Enter(target, data);
        time.Advance(FileDropTarget.RetryInterval); Over(target);
        time.Advance(FileDropTarget.RetryInterval); Assert.Equal(Copy, Over(target));
        Assert.Equal(Two, sink.Resolved);
        Assert.Equal([DropReadStatus.Failed, DropReadStatus.Empty, DropReadStatus.Files], sink.Reports.Select(r => r.Status));
    }

    [Fact]
    public void A_permanent_failure_stops_after_a_bounded_number_of_reads_and_stays_safe()
    {
        var (target, sink, time) = Create();
        var data = new FakeData(_ => Reply.Throws(unchecked((int)0x80070057))); // E_INVALIDARG: an ArgumentException, not a COMException
        Enter(target, data);
        for (var i = 0; i < 200; i++)
        {
            time.Advance(FileDropTarget.RetryInterval);
            Assert.Equal(Ole32.DROPEFFECT_NONE, Over(target));
        }
        Assert.Equal(FileDropTarget.MaxReads, data.Reads);
        Assert.Equal(1, sink.ResolvedCalls);       // "nothing available" is said once
        Assert.Empty(sink.Resolved!);
        Assert.Equal(DropDataStage.GaveUp, sink.Reports[^1].Stage);
        Assert.True(sink.Reports.Count <= FileDropTarget.MaxReads + 1);
    }

    [Fact]
    public void Drag_over_never_reads_more_often_than_the_retry_interval()
    {
        var (target, _, time) = Create();
        var data = new FakeData(_ => Reply.NotOffered(unchecked((int)0x80040064)));
        Enter(target, data);
        for (var i = 0; i < 100; i++) { time.Advance(TimeSpan.FromMilliseconds(10)); Over(target); } // OLE calls DragOver often
        Assert.InRange(data.Reads, 1, 1 + (int)(TimeSpan.FromMilliseconds(1000) / FileDropTarget.RetryInterval));
    }

    [Fact]
    public void One_slow_failing_read_ends_the_retries_so_the_ui_thread_is_not_held_again()
    {
        var (target, sink, time) = Create();
        var data = new FakeData(_ => Reply.Throws(unchecked((int)0x8001010A)), () => time.Advance(TimeSpan.FromMilliseconds(250)));
        Enter(target, data);
        for (var i = 0; i < 50; i++) { time.Advance(FileDropTarget.RetryInterval); Over(target); }
        Assert.Equal(1, data.Reads);
        Assert.False(sink.Pending);                // said at once: nothing is available
        Assert.Equal(DropDataStage.GaveUp, sink.Reports.Single().Stage);
    }

    [Fact]
    public void Reads_stop_when_their_total_time_reaches_the_budget()
    {
        var (target, _, time) = Create();
        var data = new FakeData(_ => Reply.Throws(unchecked((int)0x8001010A)), () => time.Advance(TimeSpan.FromMilliseconds(150)));
        Enter(target, data);
        for (var i = 0; i < 50; i++) { time.Advance(FileDropTarget.RetryInterval); Over(target); }
        Assert.Equal(4, data.Reads);               // 150 ms each: the fourth reaches 500 ms
    }

    [Fact]
    public void Crossing_the_ring_edge_again_and_again_does_not_reset_the_read_limit()
    {
        var (target, sink, time) = Create();
        var data = new FakeData(_ => Reply.NotOffered(unchecked((int)0x80040064)));
        for (var i = 0; i < 200; i++)
        {
            Enter(target, data);
            Over(target);
            target.DragLeave();
            time.Advance(TimeSpan.FromMilliseconds(30));
        }
        Assert.InRange(data.Reads, 1, FileDropTarget.MaxReads);
        Assert.Equal(DropDataStage.GaveUp, sink.Reports[^1].Stage);
        Assert.True(sink.Reports.Count <= FileDropTarget.MaxReads);
    }

    public static TheoryData<string, byte[]> MalformedBlocks => new()
    {
        { "shorter than the header", new byte[12] },
        { "file list offset past the end", Header(pFiles: 4096, wide: true, size: 64) },
        { "file list offset inside the header", Header(pFiles: 8, wide: true, size: 64) },
        { "unterminated list", Unterminated() },
    };

    [Theory]
    [MemberData(nameof(MalformedBlocks))]
    public void A_malformed_dropfiles_block_is_rejected_within_its_bounds(string _, byte[] block)
    {
        var (target, sink, _) = Create();
        Enter(target, new FakeData(__ => Reply.Raw(block)));
        Assert.True(sink.Pending);
        Assert.Empty(sink.Entered!);
        Assert.Equal(DropReadStatus.Malformed, sink.Reports.Single().Status);
    }

    [Fact]
    public void More_than_the_file_limit_is_malformed_not_truncated()
    {
        var many = Enumerable.Range(0, DropFiles.MaxFiles + 1).Select(i => $@"C:\x\{i}.png").ToArray();
        var (target, sink, _) = Create();
        Enter(target, new FakeData(_ => Reply.Files(many)));
        Assert.Empty(sink.Entered!);
        Assert.Equal(DropReadStatus.Malformed, sink.Reports.Single().Status);
    }

    [Fact]
    public void The_drop_falls_back_to_the_sessions_files_when_its_own_read_fails()
    {
        var (target, sink, _) = Create();
        Enter(target, new FakeData(_ => Reply.Files(Two)));
        Assert.Equal(Copy, Drop(target, new FakeData(_ => Reply.Throws(unchecked((int)0x8001010A)))));
        Assert.Equal(Two, sink.Dropped);
        Assert.Equal(DropDataStage.SessionFallback, sink.Reports.Single().Stage);
    }

    [Fact]
    public void The_drop_uses_its_own_files_when_it_can_read_them()
    {
        var (target, sink, _) = Create();
        Enter(target, new FakeData(_ => Reply.Files(Two)));
        Drop(target, new FakeData(_ => Reply.Files([@"C:\images\c.webp"])));
        Assert.Equal([@"C:\images\c.webp"], sink.Dropped);
    }

    [Fact]
    public void A_drop_with_no_readable_files_anywhere_runs_nothing()
    {
        var (target, sink, _) = Create();
        Enter(target, new FakeData(_ => Reply.NotOffered(unchecked((int)0x80040064))));
        Drop(target, new FakeData(_ => Reply.NotOffered(unchecked((int)0x80040064))));
        Assert.Empty(sink.Dropped!);
        Assert.Equal(DropDataStage.DropUnavailable, sink.Reports[^1].Stage);
    }

    [Fact]
    public void Re_entering_in_the_same_session_keeps_the_files_and_a_new_session_does_not()
    {
        var (target, sink, _) = Create();
        Enter(target, new FakeData(_ => Reply.Files(Two)));
        target.DragLeave();
        Assert.Equal(Copy, Enter(target, new FakeData(_ => Reply.Throws(unchecked((int)0x8001010A)))));
        Assert.Equal(Two, sink.Entered);
        Assert.False(sink.Pending);

        target.Reset();                            // the ring was hidden: the next drag is another drag
        Enter(target, new FakeData(_ => Reply.Throws(unchecked((int)0x8001010A))));
        Assert.True(sink.Pending);
        Assert.Empty(sink.Entered!);
    }

    [Fact]
    public void A_source_that_does_not_allow_copy_is_never_read_and_never_retried()
    {
        var (target, sink, time) = Create();
        var data = new FakeData(_ => Reply.Throws(unchecked((int)0x80004005)));
        Assert.Equal(Ole32.DROPEFFECT_NONE, Enter(target, data, MoveOnly));
        time.Advance(FileDropTarget.RetryInterval);
        Over(target, MoveOnly);
        Assert.Equal(0, data.Reads);
        Assert.False(sink.Pending);
        Assert.Empty(sink.Entered!);
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("drop")]
    [InlineData("reset")]
    public void The_drags_data_object_is_let_go_when_the_drag_ends(string end)
    {
        var (target, _, time) = Create();
        var (weak, reads) = EnterWithPendingData(target);
        switch (end)
        {
            case "leave": target.DragLeave(); break;
            case "drop": Drop(target, new FakeData(_ => Reply.NotOffered(unchecked((int)0x80040064)))); break;
            default: target.Reset(); break;
        }
        time.Advance(FileDropTarget.RetryInterval);
        Over(target);
        Assert.Equal(1, reads());                  // nothing read from it after the end
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Weak, Func<int> Reads) EnterWithPendingData(FileDropTarget target)
    {
        var data = new FakeData(_ => Reply.Throws(unchecked((int)0x8001010A)));
        Enter(target, data);
        var counter = data.Counter;
        return (new WeakReference(data), () => counter.Reads);
    }

    // --- DROPFILES parsing on its own -------------------------------------------------------------------------

    [Fact]
    public void Wide_dropfiles_with_unicode_spaces_and_long_paths_parse_exactly()
    {
        string[] paths = [@"C:\a b\صورة 😀.png", @"\\?\C:\" + new string('x', 300) + ".jpg"];
        Assert.Equal(DropReadStatus.Files, DropFiles.Parse(Block(paths), out var parsed));
        Assert.Equal(paths, parsed);
    }

    [Theory]
    [MemberData(nameof(MalformedBlocks))]
    public void Parse_rejects_malformed_blocks(string _, byte[] block) =>
        Assert.Equal(DropReadStatus.Malformed, DropFiles.Parse(block, out var __));

    [Fact]
    public void Ansi_dropfiles_from_old_sources_still_parse()
    {
        var list = Encoding.ASCII.GetBytes("C:\\old app\\a.png\0C:\\b.jpg\0\0");
        var block = Header(pFiles: 20, wide: false, size: 20 + list.Length);
        list.CopyTo(block, 20);
        Assert.Equal(DropReadStatus.Files, DropFiles.Parse(block, out var parsed));
        Assert.Equal([@"C:\old app\a.png", @"C:\b.jpg"], parsed);
    }

    [Fact]
    public void A_list_missing_its_final_empty_path_still_parses_when_every_path_is_terminated()
    {
        var list = Encoding.Unicode.GetBytes("C:\\a.png\0C:\\b.jpg\0");
        var block = Header(pFiles: 20, wide: true, size: 20 + list.Length);
        list.CopyTo(block, 20);
        Assert.Equal(DropReadStatus.Files, DropFiles.Parse(block, out var parsed));
        Assert.Equal([@"C:\a.png", @"C:\b.jpg"], parsed);
    }

    [Fact]
    public void Parse_reports_an_empty_list_as_empty()
    {
        Assert.Equal(DropReadStatus.Empty, DropFiles.Parse(Block([]), out var parsed));
        Assert.Empty(parsed);
    }

    // --- helpers ----------------------------------------------------------------------------------------------

    private static (FileDropTarget Target, Sink Sink, ManualTime Time) Create()
    {
        var sink = new Sink();
        var time = new ManualTime();
        return (new FileDropTarget(sink, time), sink, time);
    }

    private static uint Enter(FileDropTarget target, IDataObject data, uint allowed = CopyOrMove)
    {
        var effect = allowed;
        Assert.Equal(0, target.DragEnter(data, 1, new POINTL { X = 10, Y = 10 }, ref effect));
        return effect;
    }

    private static uint Over(FileDropTarget target, uint allowed = CopyOrMove)
    {
        var effect = allowed;
        Assert.Equal(0, target.DragOver(1, new POINTL { X = 11, Y = 11 }, ref effect));
        return effect;
    }

    private static uint Drop(FileDropTarget target, IDataObject data)
    {
        var effect = CopyOrMove;
        Assert.Equal(0, target.Drop(data, 0, new POINTL { X = 12, Y = 12 }, ref effect));
        return effect;
    }

    /// <summary>A DROPFILES block (shlobj_core.h): a 20-byte header, then the null-terminated paths and a final null.</summary>
    private static byte[] Block(IReadOnlyList<string> paths)
    {
        var list = Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => p + "\0")) + "\0");
        var block = Header(pFiles: 20, wide: true, size: 20 + list.Length);
        list.CopyTo(block, 20);
        return block;
    }

    private static byte[] Header(int pFiles, bool wide, int size)
    {
        var block = new byte[size];
        BitConverter.GetBytes(pFiles).CopyTo(block, 0);
        BitConverter.GetBytes(wide ? 1 : 0).CopyTo(block, 16);
        return block;
    }

    /// <summary>A path that runs to the very end of the block with no terminator. 64 bytes, so the allocation is not rounded up.</summary>
    private static byte[] Unterminated()
    {
        var block = Header(pFiles: 20, wide: true, size: 64);
        for (var i = 20; i < 64; i += 2) block[i] = (byte)'a';
        return block;
    }

    private sealed record Reply(int QueryHr, int ThrowHr, byte[]? Bytes)
    {
        public static Reply Files(IReadOnlyList<string> paths) => new(0, 0, Block(paths));
        public static Reply Raw(byte[] block) => new(0, 0, block);
        public static Reply NotOffered(int hr) => new(hr, 0, null);
        public static Reply Throws(int hr) => new(0, hr, null);
    }

    private sealed class ReadCounter { public int Reads; }

    /// <summary>A data object whose n-th CF_HDROP read (from 1) answers <paramref name="script"/>(n). Memory is a real
    /// moveable HGLOBAL, freed by the target through ReleaseStgMedium as with any source.</summary>
    private sealed class FakeData(Func<int, Reply> script, Action? onRead = null) : IDataObject
    {
        public ReadCounter Counter { get; } = new();
        public int Reads => Counter.Reads;
        private Reply? _current;

        public int QueryGetData(ref FORMATETC format)
        {
            if (format.cfFormat != Ole32.CF_HDROP) return unchecked((int)0x80040064); // DV_E_FORMATETC
            _current = script(++Counter.Reads);
            return _current.QueryHr;
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            var reply = _current ?? throw new InvalidOperationException("GetData without QueryGetData");
            onRead?.Invoke();
            if (reply.ThrowHr != 0) Marshal.ThrowExceptionForHR(reply.ThrowHr); // what an RCW throws for a failed call
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = Global(reply.Bytes!) };
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) => throw new NotSupportedException();
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) => throw new NotSupportedException();
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) => throw new NotSupportedException();
    }

    private static nint Global(byte[] bytes)
    {
        var h = GlobalAlloc(0x0042 /* GMEM_MOVEABLE | GMEM_ZEROINIT */, (nuint)bytes.Length);
        Assert.NotEqual(0, h);
        var p = GlobalLock(h);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(h);
        return h;
    }

    private sealed class Sink : IDropSink
    {
        public IReadOnlyList<string>? Entered, Resolved, Dropped;
        public bool? Pending;
        public int ResolvedCalls;
        public List<DropDataEvent> Reports { get; } = [];

        public bool OnDragEnter(IReadOnlyList<string> paths, bool pending, int x, int y) { (Entered, Pending) = (paths, pending); return true; }
        public bool OnDragOver(int x, int y) => true;
        public bool OnFilesResolved(IReadOnlyList<string> paths, int x, int y) { Resolved = paths; ResolvedCalls++; return true; }
        public void OnDragLeave() { }
        public bool OnDrop(IReadOnlyList<string> paths, int x, int y, bool copyAllowed) { Dropped = paths; return paths.Count > 0; }
        public void OnDropData(DropDataEvent report) => Reports.Add(report);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now;
        public void Advance(TimeSpan by) => _now += by.Ticks;
    }

    [LibraryImport("kernel32.dll")] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalLock(nint h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint h);
}
