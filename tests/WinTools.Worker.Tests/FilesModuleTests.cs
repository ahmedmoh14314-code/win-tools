using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 1.4, the General Files module's host side: what it believes from its worker, and Copy path, which runs in the
/// host without a worker.
/// </summary>
public sealed class FilesModuleTests
{
    private static readonly JobRequest Hash = new("h1", FilesCapabilities.Sha256Id, [@"C:\p\a.txt"]);
    private static readonly JobRequest Info = new("i1", FilesCapabilities.InfoId, [@"C:\p\a.txt"]);
    private static bool Believed(JobRequest q, JobResult r) => FilesJobModule.Instance.IsBelievable(q, r);
    private static JobResult Facts(string id, params JobFact[] facts) => new(id, true, ErrorCode.None, null, [], facts);

    [Fact]
    public void A_hash_is_believed_only_as_64_lower_case_hex_digits()
    {
        Assert.True(Believed(Hash, Facts("h1", new JobFact("SHA-256", new string('a', 64)), new JobFact("Size", "3 bytes"))));
        Assert.False(Believed(Hash, Facts("h1", new JobFact("SHA-256", new string('A', 64)))));
        Assert.False(Believed(Hash, Facts("h1", new JobFact("SHA-256", new string('a', 63)))));
        Assert.False(Believed(Hash, Facts("h1", new JobFact("SHA-256", new string('g', 64)))));
        Assert.False(Believed(Hash, Facts("h1", new JobFact("Size", "3 bytes"))));
    }

    [Fact]
    public void Info_needs_a_type_and_no_output_and_failures_carry_nothing()
    {
        Assert.True(Believed(Info, Facts("i1", new JobFact("Type", "Text"))));
        Assert.False(Believed(Info, Facts("i1", new JobFact("Size", "3 bytes"))));
        Assert.False(Believed(Info, new JobResult("i1", true, ErrorCode.None, null, [new(@"C:\p\x", new string('a', 64))], [new JobFact("Type", "Text")])));
        Assert.False(Believed(Info, new JobResult("i1", false, ErrorCode.SourceNotReadable, "x", [], [new JobFact("Type", "Text")])));
        Assert.True(Believed(Info, JobResult.Fail("i1", ErrorCode.SourceNotReadable, "x")));
        Assert.False(Believed(new JobRequest("x1", "files.delete", [@"C:\p\a.txt"]), Facts("x1", new JobFact("Type", "Text"))));
    }

    [Fact]
    public async Task Copy_path_puts_every_path_on_the_clipboard_once_and_never_starts_a_worker()
    {
        var copied = new List<string>();
        var worker = new NoWorker();
        var executor = new FilesExecutor(worker, worker, text => { copied.Add(text); return Task.FromResult(true); });
        var request = new JobRequest("c1", FilesCapabilities.CopyPathId, [@"C:\p\a.png", @"C:\p\holiday\"]);
        var r = Assert.Single(await executor.RunAsync([request], CancellationToken.None));
        Assert.True(r.Success);
        Assert.Equal(["C:\\p\\a.png\r\nC:\\p\\holiday"], copied);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public async Task A_busy_clipboard_is_a_failure_with_its_own_reason()
    {
        var executor = new FilesExecutor(new NoWorker(), new NoWorker(), _ => Task.FromResult(false));
        var r = Assert.Single(await executor.RunAsync([new JobRequest("c1", FilesCapabilities.CopyPathId, [@"C:\p\a.png"])], CancellationToken.None));
        Assert.Equal(ErrorCode.ClipboardUnavailable, r.Error);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Info_and_hash_go_to_their_own_workers()
    {
        // File info gets a short limit, hashing a long one with one file at a time (evaluator, PR #18).
        var (info, hash) = (new NoWorker(), new NoWorker());
        var executor = new FilesExecutor(info, hash, _ => Task.FromResult(true));
        await executor.RunAsync([Hash], CancellationToken.None);
        Assert.Equal((0, 1), (info.Calls, hash.Calls));
        await executor.RunAsync([Info], CancellationToken.None);
        Assert.Equal((1, 1), (info.Calls, hash.Calls));
        await executor.RunAsync([new JobRequest("c1", FilesCapabilities.CopyPathId, [@"C:\p\a.png"])], CancellationToken.None);
        Assert.Equal(1, executor.LastLaneCount); // Copy path leaves the last worker job's values alone (it may still be running)
    }

    private sealed class NoWorker : IJobExecutor
    {
        public int Calls;
        public bool JobObjectAssigned => true;
        public int LastLaneCount => 1;
        public Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken, Action<int>? onFileDone = null)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<JobResult>>([.. requests.Select(r => JobResult.Fail(r.Id, ErrorCode.Internal, "fake"))]);
        }
    }
}
