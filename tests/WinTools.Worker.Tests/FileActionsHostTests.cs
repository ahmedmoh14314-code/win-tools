using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>
/// ADR-0032: Copy to, Move to, Show in folder and Open in Terminal as the host runs them. Real files in a temporary folder;
/// a move across volumes is simulated on one (the copy, check and removal path of <see cref="FileTransfer"/>). Nothing is
/// ever overwritten, an original is never lost, a failure leaves no copy, and nothing opens on the desktop (a recorder
/// stands in for Explorer and the terminal).
/// </summary>
public sealed partial class FileActionsHostTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal); // read-only test files must not stop the cleanup
        _dir.Dispose();
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] Names(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    /// <summary>Every file of a tree, relative, with its content's hash.</summary>
    private static string[] Tree(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => $"{Path.GetRelativePath(folder, f)}={Hash(f)}").Order(StringComparer.Ordinal)];

    private static JobRequest Request(string capability, string item, string destination) =>
        new(Guid.NewGuid().ToString("N"), capability, [item], null, new Dictionary<string, string> { [DialogParameters.Name] = destination });

    private static async Task<JobResult> Run(JobRequest request, TransferHooks? hooks = null, CancellationToken token = default)
    {
        var result = await FileTransfer.RunAsync(request, token, hooks);
        // The host believes its own success from the disk, and a failure carries no outputs or facts.
        Assert.True(FilesJobModule.Instance.IsBelievable(request, result), result.Message);
        return result;
    }

    [Fact]
    public async Task A_copy_lands_in_the_destination_and_the_original_is_untouched()
    {
        var src = Write(Path.Combine(Dir("src"), "report.pdf"), "the report");
        var before = (Hash(src), File.GetLastWriteTimeUtc(src));
        var dest = Dir("dest");
        var r = await Run(Request(FilesCapabilities.CopyToId, src, dest));
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(dest, "report.pdf"), r.OutputPath);
        Assert.Equal((Hash(src), File.GetLastWriteTimeUtc(src)), before);
        Assert.Equal(before.Item1, Hash(r.OutputPath!));
        Assert.Equal(File.GetLastWriteTimeUtc(src), File.GetLastWriteTimeUtc(r.OutputPath!)); // CopyFileExW keeps the times
        Assert.Contains(r.Facts, f => f is { Label: "Destination", Notice: true } && f.Value == "to dest");
        Assert.DoesNotContain(r.Facts, f => f.Label == "Saved as");
        Assert.Equal(["report.pdf"], Names(dest)); // no temporary file left
    }

    [Fact]
    public async Task A_taken_name_is_never_overwritten_the_copy_takes_the_next_number()
    {
        var src = Write(Path.Combine(Dir("src"), "report.pdf"), "mine");
        var dest = Dir("dest");
        Write(Path.Combine(dest, "report.pdf"), "theirs");
        Write(Path.Combine(dest, "report (1).pdf"), "theirs too");
        var r = await Run(Request(FilesCapabilities.CopyToId, src, dest));
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(dest, "report (2).pdf"), r.OutputPath);
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(dest, "report.pdf")));
        Assert.Equal("theirs too", File.ReadAllText(Path.Combine(dest, "report (1).pdf")));
        Assert.Equal("mine", File.ReadAllText(r.OutputPath!));
        Assert.Contains(r.Facts, f => f is { Label: "Saved as", Notice: true } && f.Value == "as report (2).pdf");
    }

    [Fact]
    public async Task A_copy_into_its_own_folder_is_a_numbered_duplicate()
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "x");
        var r = await Run(Request(FilesCapabilities.CopyToId, src, Path.GetDirectoryName(src)!));
        Assert.True(r.Success, r.Message);
        Assert.Equal(["a (1).txt", "a.txt"], Names(Path.GetDirectoryName(src)!));
    }

    [Fact]
    public async Task A_move_on_one_volume_is_a_rename_and_never_overwrites()
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "mine");
        var dest = Dir("dest");
        Write(Path.Combine(dest, "a.txt"), "theirs");
        var r = await Run(Request(FilesCapabilities.MoveToId, src, dest));
        Assert.True(r.Success, r.Message);
        Assert.False(File.Exists(src));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(dest, "a (1).txt")));
        Assert.Equal(["a (1).txt", "a.txt"], Names(dest));
    }

    [Fact]
    public async Task A_move_to_the_folder_it_is_in_has_nothing_to_do()
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "x");
        var r = await Run(Request(FilesCapabilities.MoveToId, src, Path.GetDirectoryName(src)!));
        Assert.Equal(ErrorCode.NothingToDo, r.Error);
        Assert.Equal(["a.txt"], Names(Path.GetDirectoryName(src)!));
    }

    [Fact]
    public async Task A_move_across_volumes_copies_checks_then_removes_the_original()
    {
        var src = Write(Path.Combine(Dir("src"), "video.bin"), new string('v', 300_000));
        File.SetAttributes(src, FileAttributes.ReadOnly); // a read-only original is moved too, as Explorer does
        var hash = Hash(src);
        var dest = Dir("dest");
        var r = await Run(Request(FilesCapabilities.MoveToId, src, dest), new TransferHooks(CrossVolume: true));
        Assert.True(r.Success, r.Message);
        Assert.False(File.Exists(src));
        Assert.Equal(hash, Hash(Path.Combine(dest, "video.bin")));
        Assert.Equal(hash, r.Outputs[0].Sha256); // the hash the check compared
        Assert.Equal(["video.bin"], Names(dest));
    }

    [Fact]
    public async Task A_copy_that_does_not_match_is_removed_and_the_original_kept()
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "original content");
        var hash = Hash(src);
        var dest = Dir("dest");
        // The copy is damaged after it was written (same size, other bytes): the SHA-256 check must catch it.
        var r = await Run(Request(FilesCapabilities.MoveToId, src, dest), new TransferHooks(CrossVolume: true,
            AfterCopy: temp => { File.SetAttributes(temp, FileAttributes.Normal); File.WriteAllText(temp, "ORIGINAL CONTENT"); }));
        Assert.Equal(ErrorCode.OutputValidationFailed, r.Error);
        Assert.Equal(hash, Hash(src));
        Assert.Empty(Names(dest)); // neither the temporary copy nor a named one
    }

    [Fact]
    public async Task An_original_that_cannot_be_removed_takes_its_copy_back()
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "busy");
        var dest = Dir("dest");
        FileStream? held = null;
        try
        {
            // Another program opens the original between the check and its removal.
            var r = await Run(Request(FilesCapabilities.MoveToId, src, dest), new TransferHooks(CrossVolume: true,
                BeforeSourceRemoved: s => held = new FileStream(s, FileMode.Open, FileAccess.Read, FileShare.Read)));
            Assert.False(r.Success);
            Assert.Equal(ErrorCode.SourceNotReadable, r.Error);
        }
        finally { held?.Dispose(); }
        Assert.Equal("busy", File.ReadAllText(src));
        Assert.Empty(Names(dest));
    }

    [Fact]
    public async Task An_original_open_in_another_program_is_not_moved()
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "open");
        var dest = Dir("dest");
        using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var same = await Run(Request(FilesCapabilities.MoveToId, src, dest));
            Assert.Equal(ErrorCode.SourceNotReadable, same.Error);
            var across = await Run(Request(FilesCapabilities.MoveToId, src, dest), new TransferHooks(CrossVolume: true));
            Assert.Equal(ErrorCode.SourceNotReadable, across.Error);
        }
        Assert.Equal("open", File.ReadAllText(src));
        Assert.Empty(Names(dest));
    }

    /// <summary>A tree with a nested folder, an empty folder, a hidden and a read-only file and names in several scripts.</summary>
    private static string MakeTree(string root)
    {
        Write(Path.Combine(root, "a.txt"), "a");
        Write(Path.Combine(root, "sub", "b.txt"), "b");
        Write(Path.Combine(root, "sub", "deeper", "تقرير 😀.txt"), "unicode");
        var hidden = Write(Path.Combine(root, "h.txt"), "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var locked = Write(Path.Combine(root, "sub", "ro.txt"), "read-only");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        return root + Path.DirectorySeparatorChar;
    }

    [Fact]
    public async Task A_folder_is_copied_whole_and_the_original_kept()
    {
        var item = MakeTree(Path.Combine(Dir("src"), "photos"));
        var original = Tree(item);
        var dest = Dir("dest");
        Directory.CreateDirectory(Path.Combine(dest, "photos")); // taken: the copy becomes "photos (1)"
        var r = await Run(Request(FilesCapabilities.CopyToId, item, dest));
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(dest, "photos (1)"), r.OutputPath);
        Assert.Equal(original, Tree(r.OutputPath!));
        Assert.Equal(original, Tree(item));
        Assert.True(Directory.Exists(Path.Combine(r.OutputPath!, "empty")));
        Assert.True(File.GetAttributes(Path.Combine(r.OutputPath!, "h.txt")).HasFlag(FileAttributes.Hidden));
        Assert.Equal(["photos", "photos (1)"], Names(dest));
    }

    [Fact]
    public async Task A_folder_moves_on_one_volume_and_across_volumes()
    {
        var one = MakeTree(Path.Combine(Dir("src"), "one"));
        var two = MakeTree(Path.Combine(Dir("src"), "two"));
        var expected = Tree(one);
        var dest = Dir("dest");
        var renamed = await Run(Request(FilesCapabilities.MoveToId, one, dest));
        Assert.True(renamed.Success, renamed.Message);
        var copied = await Run(Request(FilesCapabilities.MoveToId, two, dest), new TransferHooks(CrossVolume: true));
        Assert.True(copied.Success, copied.Message);
        Assert.False(Directory.Exists(one) || Directory.Exists(two));
        Assert.Equal(expected, Tree(Path.Combine(dest, "one")));
        Assert.Equal(expected, Tree(Path.Combine(dest, "two")));
        Assert.Equal(["one", "two"], Names(dest));
        Assert.Empty(Names(Path.Combine(_dir.Path, "src"))); // nothing left behind, not even the aside name
    }

    [Fact]
    public async Task A_folder_in_use_is_not_moved_across_volumes_and_its_copy_is_removed()
    {
        var item = MakeTree(Path.Combine(Dir("src"), "busy"));
        var expected = Tree(item);
        var dest = Dir("dest");
        using (new FileStream(Path.Combine(item, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = await Run(Request(FilesCapabilities.MoveToId, item, dest), new TransferHooks(CrossVolume: true));
            Assert.False(r.Success);
        }
        Assert.Equal(expected, Tree(item));
        Assert.Empty(Names(dest));
    }

    [Fact]
    public async Task A_folder_copy_that_does_not_match_is_removed()
    {
        var item = MakeTree(Path.Combine(Dir("src"), "photos"));
        var expected = Tree(item);
        var dest = Dir("dest");
        var r = await Run(Request(FilesCapabilities.MoveToId, item, dest), new TransferHooks(CrossVolume: true,
            AfterCopy: temp => File.WriteAllText(Path.Combine(temp, "a.txt"), "A")));
        Assert.Equal(ErrorCode.OutputValidationFailed, r.Error);
        Assert.Equal(expected, Tree(item));
        Assert.Empty(Names(dest));
    }

    [Theory]
    [InlineData(FilesCapabilities.CopyToId, "")]
    [InlineData(FilesCapabilities.MoveToId, "")]
    [InlineData(FilesCapabilities.CopyToId, "sub")]
    [InlineData(FilesCapabilities.MoveToId, "sub")]
    public async Task A_folder_into_itself_is_refused(string capability, string under)
    {
        var item = MakeTree(Path.Combine(Dir("src"), "photos"));
        var expected = Tree(item);
        var r = await Run(Request(capability, item, Path.TrimEndingDirectorySeparator(Path.Combine(Plain(item), under))));
        Assert.Equal(ErrorCode.UnsupportedOperation, r.Error);
        Assert.Equal(expected, Tree(item));
        Assert.Equal(["a.txt", "empty", "h.txt", "sub"], Names(item));
    }

    private static string Plain(string item) => WinTools.Core.Files.TransferPlan.Plain(item);

    [Fact]
    public async Task Links_inside_a_folder_are_skipped_by_a_copy_and_refuse_a_move_across_volumes()
    {
        var outside = Dir("outside");
        Write(Path.Combine(outside, "secret.txt"), "not part of the folder");
        var root = Path.Combine(Dir("src"), "photos");
        Write(Path.Combine(root, "a.txt"), "a");
        Junction.Make(Path.Combine(root, "link"), outside);
        var item = root + Path.DirectorySeparatorChar;
        var dest = Dir("dest");
        var copy = await Run(Request(FilesCapabilities.CopyToId, item, dest));
        Assert.True(copy.Success, copy.Message);
        Assert.Equal(["a.txt"], Names(Path.Combine(dest, "photos"))); // never followed
        Assert.Contains(copy.Facts, f => f is { Label: "Links skipped", Notice: true });
        var move = await Run(Request(FilesCapabilities.MoveToId, item, Dir("dest2")), new TransferHooks(CrossVolume: true));
        Assert.Equal(ErrorCode.LimitExceeded, move.Error);
        Assert.True(Directory.Exists(Path.Combine(root, "link")));
        Assert.Equal("not part of the folder", File.ReadAllText(Path.Combine(outside, "secret.txt")));
        Assert.Empty(Names(Path.Combine(_dir.Path, "dest2")));
    }

    [Fact]
    public async Task A_destination_that_is_a_link_or_gone_is_refused()
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "x");
        var real = Dir("real");
        var link = Path.Combine(_dir.Path, "link");
        Junction.Make(link, real);
        Assert.Equal(ErrorCode.OutputDirectoryIsLink, (await Run(Request(FilesCapabilities.CopyToId, src, link))).Error);
        Assert.Equal(ErrorCode.SourceNotFound, (await Run(Request(FilesCapabilities.CopyToId, src, Path.Combine(_dir.Path, "gone")))).Error);
        Assert.Empty(Names(real));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task Long_and_unicode_paths_are_copied_and_moved()
    {
        // Well beyond MAX_PATH (260), with Arabic, an emoji and spaces in every part.
        var deep = Path.Combine(_dir.Path, "src");
        while (deep.Length < 300) deep = Path.Combine(deep, "مجلد طويل جدًا 😀 folder");
        var src = Write(Path.Combine(deep, "تقرير نهائي 😀.txt"), "long");
        var dest = Path.Combine(_dir.Path, "dest");
        while (dest.Length < 300) dest = Path.Combine(dest, "وجهة بعيدة 😀 destination");
        Directory.CreateDirectory(dest);
        Assert.True(src.Length > 300 && dest.Length > 300);
        var copied = await Run(Request(FilesCapabilities.CopyToId, src, dest));
        Assert.True(copied.Success, copied.Message);
        var moved = await Run(Request(FilesCapabilities.MoveToId, src, dest), new TransferHooks(CrossVolume: true));
        Assert.True(moved.Success, moved.Message);
        Assert.False(File.Exists(src));
        Assert.Equal(["تقرير نهائي 😀 (1).txt", "تقرير نهائي 😀.txt"], Names(dest));
        Assert.All(Directory.EnumerateFiles(dest), f => Assert.Equal("long", File.ReadAllText(f)));
    }

    [Fact]
    public async Task A_cancelled_transfer_leaves_nothing_and_keeps_the_original()
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "x");
        var dest = Dir("dest");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var r = await Run(Request(FilesCapabilities.MoveToId, src, dest), new TransferHooks(CrossVolume: true), cancel.Token);
        Assert.Equal(ErrorCode.Cancelled, r.Error);
        Assert.True(File.Exists(src));
        Assert.Empty(Names(dest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative")]
    [InlineData(@"\\?\C:\Windows")]
    public async Task A_request_without_a_usable_destination_is_refused(string? destination)
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "x");
        var request = new JobRequest("r1", FilesCapabilities.CopyToId, [src], null,
            destination is null ? null : new Dictionary<string, string> { [DialogParameters.Name] = destination });
        Assert.Equal(ErrorCode.InvalidRequest, (await Run(request)).Error);
    }

    [Fact]
    public void A_claimed_copy_or_move_is_believed_only_from_the_disk()
    {
        var src = Write(Path.Combine(Dir("src"), "a.txt"), "x");
        var dest = Dir("dest");
        var request = Request(FilesCapabilities.CopyToId, src, dest);
        JobResult Claim(string path) => new(request.Id, true, ErrorCode.None, null, [new JobOutput(path, "")], [new JobFact("Destination", "to dest", Notice: true)]);
        Assert.False(FilesJobModule.Instance.IsBelievable(request, Claim(Path.Combine(dest, "a.txt")))); // not there
        Write(Path.Combine(dest, "elsewhere.txt"), "x");
        Assert.False(FilesJobModule.Instance.IsBelievable(request, Claim(Path.Combine(dest, "elsewhere.txt")))); // not a name it may take
        Write(Path.Combine(dest, "a (3).txt"), "x");
        Assert.True(FilesJobModule.Instance.IsBelievable(request, Claim(Path.Combine(dest, "a (3).txt"))));
        // A move whose original is still there did not happen.
        var move = request with { CapabilityId = FilesCapabilities.MoveToId };
        Assert.False(FilesJobModule.Instance.IsBelievable(move, Claim(Path.Combine(dest, "a (3).txt"))));
    }

    [Fact]
    public async Task The_executor_runs_each_item_and_one_failure_stays_that_items()
    {
        var a = Write(Path.Combine(Dir("src"), "a.txt"), "a");
        var gone = Path.Combine(_dir.Path, "src", "gone.txt");
        var dest = Dir("dest");
        var executor = new FilesExecutor(new FakeExecutor(), new FakeExecutor(), _ => Task.FromResult(true));
        var done = new List<int>();
        var results = await executor.RunAsync([Request(FilesCapabilities.MoveToId, a, dest), Request(FilesCapabilities.MoveToId, gone, dest)], CancellationToken.None, done.Add);
        Assert.True(results[0].Success, results[0].Message);
        Assert.Equal(ErrorCode.SourceNotFound, results[1].Error);
        Assert.Equal([1, 2], done);
        Assert.Equal(["a.txt"], Names(dest));
    }

    /// <summary>Records what would open, and opens nothing.</summary>
    private sealed class Recorder(bool succeed = true) : IWindowOpener
    {
        public List<(string Folder, string[] Items)> Shown { get; } = [];
        public List<ProcessStartInfo> Started { get; } = [];
        public bool ShowInFolder(string folder, IReadOnlyList<string> items) { Shown.Add((folder, [.. items])); return succeed; }
        public bool Start(ProcessStartInfo start) { Started.Add(start); return succeed; }
    }

    private static JobRequest Selection(string capability, params string[] items) => new(Guid.NewGuid().ToString("N"), capability, items);

    [Fact]
    public void Show_in_folder_selects_the_items_of_each_folder_once()
    {
        var src = Dir("src");
        var a = Write(Path.Combine(src, "a.txt"), "a");
        var b = Write(Path.Combine(src, "b.txt"), "b");
        var sub = MakeTree(Path.Combine(src, "sub"));
        var other = Write(Path.Combine(Dir("other"), "c.txt"), "c");
        var recorder = new Recorder();
        var r = FileWindows.Run(Selection(FilesCapabilities.ShowId, a, b, sub, other), recorder);
        Assert.True(r.Success, r.Message);
        Assert.True(FilesJobModule.Instance.IsBelievable(Selection(FilesCapabilities.ShowId, a), r));
        Assert.Equal(2, recorder.Shown.Count);
        Assert.Equal(src, recorder.Shown[0].Folder);
        Assert.Equal([a, b, Plain(sub)], recorder.Shown[0].Items);
        Assert.Equal(Path.GetDirectoryName(other), recorder.Shown[1].Folder);
        Assert.Equal([other], recorder.Shown[1].Items);
        Assert.Empty(recorder.Started); // no explorer.exe command line
    }

    [Fact]
    public void Window_commands_refuse_too_many_folders_a_missing_item_and_say_when_windows_failed()
    {
        var items = Enumerable.Range(0, FilesCapabilities.MaxWindows + 1).Select(i => Write(Path.Combine(Dir($"f{i}"), "x.txt"), "x")).ToArray();
        var recorder = new Recorder();
        Assert.Equal(ErrorCode.LimitExceeded, FileWindows.Run(Selection(FilesCapabilities.ShowId, items), recorder).Error);
        Assert.Equal(ErrorCode.LimitExceeded, FileWindows.Run(Selection(FilesCapabilities.TerminalId, items), recorder).Error);
        Assert.True(FileWindows.Run(Selection(FilesCapabilities.TerminalId, items[..FilesCapabilities.MaxWindows]), recorder).Success);
        Assert.Equal(ErrorCode.SourceNotFound, FileWindows.Run(Selection(FilesCapabilities.ShowId, items[0] + ".gone"), recorder).Error);
        Assert.Equal(ErrorCode.Internal, FileWindows.Run(Selection(FilesCapabilities.ShowId, items[0]), new Recorder(succeed: false)).Error);
        Assert.Empty(recorder.Shown);
    }

    [Fact]
    public void Open_in_terminal_starts_in_the_folder_with_no_path_in_its_arguments()
    {
        var folder = Dir("a;b & c %d%");
        var file = Write(Path.Combine(folder, "x.txt"), "x");
        var recorder = new Recorder();
        Assert.True(FileWindows.Run(Selection(FilesCapabilities.TerminalId, file, folder + Path.DirectorySeparatorChar), recorder).Success);
        var start = Assert.Single(recorder.Started); // a file's folder and that folder are the same one
        Assert.Equal(folder, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.DoesNotContain(start.ArgumentList, a => a.Contains(folder, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("cmd", Path.GetFileName(start.FileName), StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathFullyQualified(start.FileName));
    }

    [Fact]
    public void Windows_terminal_when_installed_otherwise_powershell()
    {
        var wt = FileWindows.TerminalStart(@"C:\work", @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        Assert.Equal((@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", @"C:\work"), (wt.FileName, wt.WorkingDirectory));
        Assert.Equal(["-d", "."], wt.ArgumentList.ToArray());
        var ps = FileWindows.TerminalStart(@"C:\work", null, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ps.FileName);
        Assert.Equal(["-NoLogo"], ps.ArgumentList.ToArray());
        Assert.False(wt.UseShellExecute || ps.UseShellExecute);
        Assert.True(File.Exists(FileWindows.PowerShell()));
    }

    [Fact]
    public async Task The_executor_opens_windows_beside_the_queue_and_believes_them()
    {
        var a = Write(Path.Combine(Dir("src"), "a.txt"), "a");
        var recorder = new Recorder();
        var executor = new FilesExecutor(new FakeExecutor(), new FakeExecutor(), _ => Task.FromResult(true), recorder);
        var r = Assert.Single(await executor.RunAsync([Selection(FilesCapabilities.ShowId, a)], CancellationToken.None));
        Assert.True(r.Success, r.Message);
        Assert.Single(recorder.Shown);
    }

    /// <summary>An executor that must not be reached: the file actions run in the host.</summary>
    private sealed class FakeExecutor : IJobExecutor
    {
        public bool JobObjectAssigned => true;
        public int LastLaneCount => 0;
        public Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken, Action<int>? onFileDone = null) =>
            throw new InvalidOperationException("The file actions never reach a worker.");
    }

    /// <summary>
    /// A directory junction, made as Windows documents it (FSCTL_SET_REPARSE_POINT with a mount-point buffer), which needs
    /// no privilege, unlike a symbolic link.
    /// </summary>
    private static partial class Junction
    {
        public static void Make(string link, string target)
        {
            Directory.CreateDirectory(link);
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target) + "\0");
            var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target) + "\0");
            var data = new byte[8 + substitute.Length + print.Length];
            BitConverter.GetBytes((ushort)0).CopyTo(data, 0);
            BitConverter.GetBytes((ushort)(substitute.Length - 2)).CopyTo(data, 2);
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 4);
            BitConverter.GetBytes((ushort)(print.Length - 2)).CopyTo(data, 6);
            substitute.CopyTo(data, 8);
            print.CopyTo(data, 8 + substitute.Length);
            var buffer = new byte[8 + data.Length];
            BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0); // IO_REPARSE_TAG_MOUNT_POINT
            BitConverter.GetBytes((ushort)data.Length).CopyTo(buffer, 4);
            data.CopyTo(buffer, 8);
            using var handle = CreateFileW(link, 0x40000000 /* GENERIC_WRITE */, 0, 0, 3 /* OPEN_EXISTING */, 0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, 0);
            if (handle.IsInvalid) throw new IOException("Could not open the folder for the junction.", Marshal.GetLastPInvokeError());
            if (!DeviceIoControl(handle, 0x000900A4 /* FSCTL_SET_REPARSE_POINT */, buffer, buffer.Length, 0, 0, out _, 0))
                throw new IOException("Could not make the junction.", Marshal.GetLastPInvokeError());
        }

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, byte[] input, int inputSize, nint output, int outputSize, out int returned, nint overlapped);
    }
}
