using System.Runtime.InteropServices;
using WinTools.Windows.Native;
using WinTools.Windows.Overlay;

namespace WinTools.Worker.Tests;

/// <summary>
/// Found by the release matrix (PR 0.4, path style "long"): Explorer drops a file whose full path is longer than
/// MAX_PATH as its 8.3 short path, so legacy programs can open it. PathPolicy refuses short names (they are not
/// canonical), so the ring said "No actions" for every long path. The drop target must hand on the real, long path.
/// <para>
/// Expanding asks the file system, which can block for many seconds (a symlink on C: to an offline share measured
/// 21 s; evaluator, PR #13). The drop target runs on the UI thread, so the expansion runs elsewhere: the ring waits, as
/// it does for a source that has not rendered its data yet, and the files arrive on a later DragOver.
/// </para>
/// </summary>
public sealed partial class DropDataTests
{
    [Fact]
    public async Task A_file_beyond_max_path_dropped_as_its_short_name_reaches_the_menu_as_its_long_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "wintools-tests", "longpath-" + Guid.NewGuid().ToString("N")[..8]);
        var folder = root;
        while (folder.Length < 230) folder = Path.Combine(folder, "long path segment 0123456789");
        var file = Path.Combine(folder, "a long file name for the 260 character test photo.png");
        Assert.True(file.Length > 260);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            var shortPath = ShortPath(file);
            Assert.True(shortPath is not null && shortPath.Length < 260 && shortPath.Contains('~'),
                $"this volume gives no 8.3 short name for the test file ({shortPath ?? "none"}), so the case Explorer creates cannot be built here");
            var expected = LongPath(shortPath!); // the canonical long form, even if %TEMP% itself is an 8.3 path

            var (target, sink, _) = Create();
            Enter(target, new FakeData(_ => Reply.Files([shortPath!])));
            Assert.True(sink.Pending);                         // waiting, not "No actions"
            await target.Expansion!.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(Copy, Over(target));
            Assert.Equal([expected], sink.Resolved);
            Assert.Equal(Copy, Drop(target, new FakeData(_ => Reply.Files([shortPath!]))));
            Assert.Equal([expected], sink.Dropped);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task The_ui_thread_never_waits_for_a_slow_expansion()
    {
        using var gate = new ManualResetEventSlim();
        var (target, sink, _) = CreateExpanding((paths, ct) => { gate.Wait(TimeSpan.FromSeconds(30), ct); return [.. paths.Select(p => p.Replace("LONGPA~1", "long path"))]; });
        string[] dropped = [@"C:\p\LONGPA~1\a.png"];

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(Ole32.DROPEFFECT_NONE, Enter(target, new FakeData(_ => Reply.Files(dropped))));
        Assert.Equal(Ole32.DROPEFFECT_NONE, Over(target));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"DragEnter and DragOver took {watch.ElapsedMilliseconds} ms while the expansion was blocked");
        Assert.True(sink.Pending);
        Assert.Null(sink.Resolved);

        gate.Set();
        await target.Expansion!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Copy, Over(target));
        Assert.Equal([@"C:\p\long path\a.png"], sink.Resolved);
    }

    [Fact]
    public void A_drop_before_the_expansion_has_finished_runs_nothing_and_does_not_wait()
    {
        using var gate = new ManualResetEventSlim();
        var (target, sink, _) = CreateExpanding((paths, ct) => { gate.Wait(TimeSpan.FromSeconds(30), ct); return paths; });
        string[] dropped = [@"C:\p\LONGPA~1\a.png"];
        Enter(target, new FakeData(_ => Reply.Files(dropped)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(Ole32.DROPEFFECT_NONE, Drop(target, new FakeData(_ => Reply.Files(dropped))));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Drop took {watch.ElapsedMilliseconds} ms");
        Assert.Empty(sink.Dropped!);
        gate.Set();
    }

    [Fact]
    public async Task A_drop_of_other_short_names_than_the_drag_had_runs_nothing()
    {
        var (target, sink, _) = CreateExpanding((paths, _) => [.. paths.Select(p => p.Replace("~1", "-long"))]);
        Enter(target, new FakeData(_ => Reply.Files([@"C:\p\LONGPA~1\a.png"])));
        await target.Expansion!.WaitAsync(TimeSpan.FromSeconds(10));
        Over(target);
        Drop(target, new FakeData(_ => Reply.Files([@"C:\p\OTHERF~1\b.png"])));
        Assert.Empty(sink.Dropped!);
    }

    [Fact]
    public void Paths_without_an_alias_shape_are_never_expanded_and_need_no_wait()
    {
        var asked = 0;
        var (target, sink, _) = CreateExpanding((paths, _) => { asked++; return paths; });
        string[] paths = [@"C:\photos\my~holiday.png", @"C:\photos\~draft.jpg", @"C:\a b\c.png"];
        Assert.Equal(Copy, Enter(target, new FakeData(_ => Reply.Files(paths))));
        Assert.Equal(paths, sink.Entered);
        Assert.False(sink.Pending);
        Assert.Null(target.Expansion);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task A_lower_case_alias_is_expanded_too()
    {
        // Old or non-Explorer sources may send aliases in lower case; PathPolicy cannot tell one from a chosen name.
        var (target, sink, _) = CreateExpanding((paths, _) => [.. paths.Select(p => p.Replace("cafaul~1.png", "café au lait.png"))]);
        Enter(target, new FakeData(_ => Reply.Files([@"C:\p\cafaul~1.png"])));
        Assert.True(sink.Pending);
        await target.Expansion!.WaitAsync(TimeSpan.FromSeconds(10));
        Over(target);
        Assert.Equal([@"C:\p\café au lait.png"], sink.Resolved);
    }

    [Fact]
    public void A_selection_larger_than_the_menu_accepts_is_not_expanded()
    {
        var asked = 0;
        var (target, sink, _) = CreateExpanding((paths, _) => { asked++; return paths; });
        var many = Enumerable.Range(0, WinTools.Core.Capabilities.CapabilityResolver.MaxFiles + 1).Select(i => $@"C:\p\PHOTO~{i}.PNG").ToArray();
        Enter(target, new FakeData(_ => Reply.Files(many)));
        Assert.False(sink.Pending);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Expansion_stops_at_its_deadline_and_what_is_left_stays_as_dropped()
    {
        long ticks = 0;
        var step = (long)(ShortNames.Deadline.TotalSeconds * System.Diagnostics.Stopwatch.Frequency * 0.6);
        string[] five = [.. Enumerable.Range(1, 5).Select(i => $@"C:\photos\LONGPA~{i}\a.png")];
        var result = ShortNames.ExpandAll(five, p => { ticks += step; return p.Replace("~", "-long-"); }, () => ticks, CancellationToken.None);
        Assert.DoesNotContain('~', result[0]);
        Assert.DoesNotContain('~', result[1]);
        Assert.Equal(five[2..], result[2..]);
    }

    [Fact]
    public void A_name_that_only_contains_a_tilde_is_left_as_it_is()
    {
        string[] paths = [@"C:\photos\my~holiday.png", @"C:\photos\~draft.jpg"];
        var (target, sink, _) = Create();
        Enter(target, new FakeData(_ => Reply.Files(paths)));
        Assert.Equal(paths, sink.Entered);
    }

    private static (FileDropTarget Target, Sink Sink, ManualTime Time) CreateExpanding(Func<string[], CancellationToken, string[]> expand)
    {
        var sink = new Sink();
        var time = new ManualTime();
        return (new FileDropTarget(sink, time, expand), sink, time);
    }

    private static string? ShortPath(string longPath)
    {
        var buffer = new char[32768];
        var n = GetShortPathNameW(@"\\?\" + longPath, buffer, (uint)buffer.Length);
        if (n == 0 || n >= buffer.Length) return null;
        var s = new string(buffer, 0, (int)n);
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    private static string LongPath(string path)
    {
        var buffer = new char[32768];
        var n = GetLongPathNameW(@"\\?\" + path, buffer, (uint)buffer.Length);
        Assert.True(n > 0 && n < buffer.Length);
        var s = new string(buffer, 0, (int)n);
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint length);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint length);
}
