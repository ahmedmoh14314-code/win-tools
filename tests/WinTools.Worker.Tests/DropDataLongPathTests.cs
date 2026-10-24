using System.Runtime.InteropServices;

namespace WinTools.Worker.Tests;

/// <summary>
/// Found by the release matrix (PR 0.4, path style "long"): Explorer drops a file whose full path is longer than
/// MAX_PATH as its 8.3 short path, so legacy programs can open it. PathPolicy refuses short names (they are not
/// canonical), so the ring said "No actions" for every long path. The drop target must hand on the real, long path.
/// </summary>
public sealed partial class DropDataTests
{
    [Fact]
    public void A_file_beyond_max_path_dropped_as_its_short_name_reaches_the_menu_as_its_long_path()
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

            var (target, sink, _) = Create();
            Enter(target, new FakeData(_ => Reply.Files([shortPath!])));

            Assert.Equal([file], sink.Entered);
            Assert.False(sink.Pending);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_name_that_only_contains_a_tilde_is_left_as_it_is()
    {
        string[] paths = [@"C:\photos\my~holiday.png", @"C:\photos\~draft.jpg"];
        var (target, sink, _) = Create();
        Enter(target, new FakeData(_ => Reply.Files(paths)));
        Assert.Equal(paths, sink.Entered);
    }

    private static string? ShortPath(string longPath)
    {
        var buffer = new char[32768];
        var n = GetShortPathNameW(@"\\?\" + longPath, buffer, (uint)buffer.Length);
        if (n == 0 || n >= buffer.Length) return null;
        var s = new string(buffer, 0, (int)n);
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint length);
}
