using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;
using WinTools.Host;
using WinTools.Host.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 7.2: Batch rename as the host applies it. Every file goes to a temporary name first and then to its new one, so
/// swaps and case-only changes work; nothing is ever overwritten; a failure puts every name back. Written before the code.
/// </summary>
public sealed class BatchRenameHostTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private string File(string name, string content = "x")
    {
        var path = Path.Combine(_dir.Path, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static JobRequest Request(RenameRule rule, params string[] paths) =>
        new(Guid.NewGuid().ToString("N"), FilesCapabilities.RenameId, paths, null, new Dictionary<string, string> { [DialogParameters.Name] = rule.Serialize() });

    private string[] Names() => [.. Directory.EnumerateFileSystemEntries(_dir.Path).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    [Fact]
    public void Files_are_renamed_their_content_kept_and_the_host_believes_it()
    {
        var a = File("a.txt", "one");
        var b = File("b.txt", "two");
        var request = Request(new RenameRule(Prefix: "2026 "), a, b);
        var r = BatchRenamer.Apply(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal(["2026 a.txt", "2026 b.txt"], Names());
        Assert.Equal("two", System.IO.File.ReadAllText(Path.Combine(_dir.Path, "2026 b.txt")));
        Assert.True(FilesJobModule.Instance.IsBelievable(request, r));
        Assert.Contains(r.Facts, f => f is { Label: "Renamed", Notice: true } && f.Value == "2 files");
    }

    [Fact]
    public void A_swap_and_a_change_of_case_alone_both_work()
    {
        var one = File("a.txt", "first");
        var two = File("aa.txt", "second");
        // "a" -> "aa" and "aa" -> "aaaa": the first takes a name the batch itself frees.
        Assert.True(BatchRenamer.Apply(Request(new RenameRule(Find: "a", Replace: "aa"), one, two)).Success);
        Assert.Equal("first", System.IO.File.ReadAllText(Path.Combine(_dir.Path, "aa.txt")));
        Assert.Equal("second", System.IO.File.ReadAllText(Path.Combine(_dir.Path, "aaaa.txt")));
        var lower = File("README.md");
        var r = BatchRenamer.Apply(Request(new RenameRule(Case: RenameCase.Lower), lower));
        Assert.True(r.Success, r.Message);
        Assert.Contains("readme.md", Names());
        Assert.DoesNotContain("README.md", Names());
    }

    [Fact]
    public void An_existing_file_is_never_overwritten_and_every_name_is_put_back()
    {
        var a = File("a.txt", "mine a");
        var b = File("b.txt", "mine b");
        File("new b.txt", "someone else's");
        var r = BatchRenamer.Apply(Request(new RenameRule(Prefix: "new "), a, b));
        Assert.False(r.Success);
        Assert.Equal(ErrorCode.NoFreeOutputName, r.Error);
        Assert.Equal(["a.txt", "b.txt", "new b.txt"], Names());
        Assert.Equal("someone else's", System.IO.File.ReadAllText(Path.Combine(_dir.Path, "new b.txt")));
        Assert.Equal("mine a", System.IO.File.ReadAllText(a));
    }

    [Fact]
    public void A_rename_with_no_chain_goes_straight_to_the_new_names()
    {
        // Review, PR 7.2: a pass through temporary names makes OneDrive see each file leave and come back.
        var a = File("a.txt");
        var b = File("b.txt");
        var seen = new List<string>();
        using var watcher = new FileSystemWatcher(_dir.Path) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
        watcher.Renamed += (_, e) => { lock (seen) seen.Add(e.Name ?? ""); };
        Assert.True(BatchRenamer.Apply(Request(new RenameRule(Prefix: "new "), a, b)).Success);
        Thread.Sleep(300);
        lock (seen) Assert.DoesNotContain(seen, n => n.Contains("wintools", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_failed_rename_says_what_happened_in_rename_words()
    {
        var words = FilesCapabilities.Catalog.Single(c => c.Id == FilesCapabilities.RenameId).Words;
        string Detail(ErrorCode code) => FeedbackText.For(words, [new JobItem("3 items", JobResult.Fail("r", code, "x"), Inputs: 3)]).Detail;
        Assert.Contains("already", Detail(ErrorCode.NoFreeOutputName), StringComparison.Ordinal);
        Assert.Contains("not allowed", Detail(ErrorCode.OutputNameInvalid), StringComparison.Ordinal);
        Assert.Contains("put back", Detail(ErrorCode.SourceNotReadable), StringComparison.Ordinal);
        Assert.Contains("wintools-renaming", Detail(ErrorCode.Incomplete), StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was saved", Detail(ErrorCode.OutputValidationFailed), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_cannot_be_moved_puts_the_others_back()
    {
        var a = File("a.txt");
        var b = File("b.txt");
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None)) // open in another program, no sharing
        {
            var r = BatchRenamer.Apply(Request(new RenameRule(Suffix: " v2"), a, b));
            Assert.False(r.Success);
        }
        Assert.Equal(["a.txt", "b.txt"], Names());
    }

    [Fact]
    public void A_plan_that_cannot_apply_is_refused_before_anything_moves()
    {
        var a = File("a1.txt");
        var b = File("a2.txt");
        Assert.False(BatchRenamer.Apply(Request(new RenameRule(Find: "1", Replace: "2"), a, b)).Success); // a clash
        Assert.Equal(ErrorCode.NothingToDo, BatchRenamer.Apply(Request(new RenameRule(Find: "zzz"), a)).Error);
        var other = Path.Combine(_dir.Path, "sub");
        Directory.CreateDirectory(other);
        var c = Path.Combine(other, "c.txt");
        System.IO.File.WriteAllText(c, "x");
        Assert.Equal(ErrorCode.InvalidRequest, BatchRenamer.Apply(Request(new RenameRule(Prefix: "x"), a, c)).Error); // two folders
        Assert.Equal(["a1.txt", "a2.txt", "sub"], Names());
    }

    private sealed class Quiet : IJobFeedback
    {
        public void Started(CapabilityDescriptor capability, IReadOnlyList<string> paths) { }
        public void Progress(CapabilityDescriptor capability, int done, int total) { }
        public void Finished(CapabilityDescriptor capability, IReadOnlyList<JobItem> items) { }
        public void Crashed(CapabilityDescriptor capability, int files) { }
        public void SecretNeeded(CapabilityDescriptor capability) { }
    }

    [Fact]
    public void A_command_with_a_dialog_runs_only_with_the_dialogs_answer()
    {
        using var log = HostLog.Open(Path.Combine(_dir.Path, "host.jsonl"));
        var never = new FilesExecutor(null!, null!, _ => Task.FromResult(true));
        var scheduler = new JobScheduler([new JobModuleRunner(FilesJobModule.Instance, never, _ => null)], log, () => new AppSettings(), new Quiet());
        var rename = FilesCapabilities.Catalog.Single(c => c.Id == FilesCapabilities.RenameId);
        var a = File("a.txt");
        Assert.Null(scheduler.Enqueue(rename, [a]));
        Assert.Null(scheduler.Enqueue(rename, [a], new Dictionary<string, string> { ["other"] = "x" }));
        Assert.NotNull(scheduler.Enqueue(rename, [a], new Dictionary<string, string> { [DialogParameters.Name] = new RenameRule(Prefix: "x").Serialize() }));
        var info = FilesCapabilities.Catalog.Single(c => c.Id == FilesCapabilities.InfoId);
        Assert.Null(scheduler.Enqueue(info, [a], new Dictionary<string, string> { [DialogParameters.Name] = "{}" }));
    }
}
