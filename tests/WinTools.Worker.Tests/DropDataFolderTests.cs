using WinTools.Core.Jobs;
using WinTools.Windows.Overlay;

namespace WinTools.Worker.Tests;

/// <summary>
/// UX-009, V2 PR 1.3: the drop target finds dropped folders off the UI thread and marks them with a trailing separator;
/// a folder named like a file is refused by the worker with its own reason, never silently.
/// </summary>
public sealed partial class DropDataTests
{
    [Fact]
    public async Task A_dropped_folder_reaches_the_menu_marked_as_a_folder()
    {
        using var folder = new TempFolder();
        var dir = Path.Combine(folder.Path, "holiday photos");
        Directory.CreateDirectory(dir);
        var plain = folder.CopySample("quadrants-64x48.png", "a.png");
        var extensionless = folder.Write("README", [1, 2, 3]);       // a file without an extension stays a file

        var (target, sink, _) = Create();
        Enter(target, new FakeData(_ => Reply.Files([dir, plain, extensionless])));
        Assert.True(sink.Pending);                                   // looked up on a task, never in DragEnter
        await target.Expansion!.WaitAsync(TimeSpan.FromSeconds(10));
        Over(target);
        Assert.Equal([dir + Path.DirectorySeparatorChar, plain, extensionless], sink.Resolved);
    }

    [Fact]
    public void Names_with_an_extension_are_not_looked_up()
    {
        var asked = new List<string>();
        string[] paths = [@"C:\p\a.png", @"C:\p\holiday.png", @"C:\p\notes"];
        var marked = FolderMarks.Mark(paths, p => { asked.Add(p); return true; }, () => 0, CancellationToken.None);
        Assert.Equal([@"C:\p\notes"], asked);
        Assert.Equal([@"C:\p\a.png", @"C:\p\holiday.png", @"C:\p\notes\"], marked);
    }

    [Fact]
    public void A_drop_of_only_files_with_extensions_needs_no_wait()
    {
        var (target, sink, _) = Create();
        string[] paths = [@"C:\p\a.png", @"C:\p\b.jpg"];
        Enter(target, new FakeData(_ => Reply.Files(paths)));
        Assert.False(sink.Pending);
        Assert.Null(target.Expansion);
        Assert.Equal(paths, sink.Entered);
    }

    [Fact]
    public void A_folder_named_like_an_image_is_refused_with_its_own_reason()
    {
        using var folder = new TempFolder();
        var dir = Path.Combine(folder.Path, "holiday.png");
        Directory.CreateDirectory(dir);
        var r = ConversionJob.Run(new ConversionRequest("f1", "convert.to-jpeg", dir));
        Assert.Equal(ErrorCode.IsAFolder, r.Error);
        Assert.False(r.Success);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }
}
