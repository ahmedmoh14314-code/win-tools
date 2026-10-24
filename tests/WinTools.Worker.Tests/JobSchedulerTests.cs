using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;
using WinTools.Host;
using WinTools.Host.Jobs;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 1.2 (ADR-0018): the scheduler that replaced the single global gate. Per module, jobs run one at a time in the
/// order they were dropped; different modules run side by side; a job can be cancelled while queued or running; a
/// capability for the whole selection gets one request with every file. The executors are fakes the tests release.
/// </summary>
public sealed class JobSchedulerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly HostLog _log;

    public JobSchedulerTests() => _log = HostLog.Open(Path.Combine(_dir.Path, "host.jsonl"));

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    private sealed class Module(string name) : IJobModule
    {
        public string Name => name;
        public bool IsBelievable(JobRequest request, JobResult result) => true;
        public IEnumerable<string> TempPathsOf(JobRequest request) => [];
    }

    /// <summary>Each call waits for the test to release it (or for cancellation), and records what it was given.</summary>
    private sealed class Gate : IJobExecutor
    {
        public readonly List<IReadOnlyList<JobRequest>> Calls = [];
        public readonly List<TaskCompletionSource> Releases = [];
        public readonly System.Threading.Channels.Channel<bool> Entered = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        public bool JobObjectAssigned => true;
        public int LastLaneCount => 1;

        public async Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken, Action<int>? onFileDone = null)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Calls) { Calls.Add(requests); Releases.Add(release); }
            Entered.Writer.TryWrite(true);
            try { await release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { return [.. requests.Select(r => JobResult.Fail(r.Id, ErrorCode.Cancelled, "Cancelled."))]; }
            return [.. requests.Select(r => JobResult.WithFacts(r.Id, [new JobFact("done", "yes")]))];
        }

        public void Release(int call) { lock (Calls) Releases[call].SetResult(); }
    }

    private sealed class Recorder : IJobFeedback
    {
        public readonly List<string> Events = [];
        public readonly List<IReadOnlyList<JobItem>> Finished = [];
        public void Started(CapabilityDescriptor capability, IReadOnlyList<string> paths) { lock (Events) Events.Add($"start {capability.Id}"); }
        public void Progress(CapabilityDescriptor capability, int done, int total) { }
        void IJobFeedback.Finished(CapabilityDescriptor capability, IReadOnlyList<JobItem> items) { lock (Events) { Events.Add($"done {capability.Id}"); Finished.Add(items); } }
        public void Crashed(CapabilityDescriptor capability, int files) { lock (Events) Events.Add($"crash {capability.Id}"); }
    }

    private static CapabilityDescriptor Capability(string id, string module, CapabilityShape shape = CapabilityShape.PerFile) =>
        new(id, id, "tools", module, new InputRule(WinTools.Core.Files.FileKinds.All.Select(k => k.Id).ToHashSet()), shape, CapabilityOutput.Facts, LongRunning: true);

    private JobScheduler Scheduler(Recorder recorder, params (string Name, Gate Gate)[] modules) =>
        new([.. modules.Select(m => new JobModuleRunner(new Module(m.Name), m.Gate, _ => null))], _log, () => new AppSettings(), recorder);

    private static async Task Entered(Gate gate) => await gate.Entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task<bool> EnteredWithin(Gate gate, TimeSpan time) =>
        await Task.WhenAny(gate.Entered.Reader.WaitToReadAsync().AsTask(), Task.Delay(time)) is Task<bool>;

    [Fact]
    public async Task Jobs_of_one_module_run_one_at_a_time_in_drop_order()
    {
        var gate = new Gate();
        var recorder = new Recorder();
        var scheduler = Scheduler(recorder, ("a", gate));
        var first = scheduler.Enqueue(Capability("a.one", "a"), [@"C:\x\1.png"])!;
        var second = scheduler.Enqueue(Capability("a.two", "a"), [@"C:\x\2.png"])!;

        await Entered(gate);
        Assert.False(await EnteredWithin(gate, TimeSpan.FromMilliseconds(300)), "the second job started while the first was running");
        gate.Release(0);
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await Entered(gate);
        gate.Release(1);
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["start a.one", "done a.one", "start a.two", "done a.two"], recorder.Events);
    }

    [Fact]
    public async Task Different_modules_run_side_by_side()
    {
        var (a, b) = (new Gate(), new Gate());
        var scheduler = Scheduler(new Recorder(), ("a", a), ("b", b));
        var ja = scheduler.Enqueue(Capability("a.one", "a"), [@"C:\x\1.png"])!;
        var jb = scheduler.Enqueue(Capability("b.one", "b"), [@"C:\x\2.txt"])!;
        await Entered(a);
        await Entered(b);                         // both running at once
        a.Release(0);
        b.Release(0);
        await Task.WhenAll(ja.Completion, jb.Completion).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_queued_job_that_is_cancelled_never_reaches_a_worker()
    {
        var gate = new Gate();
        var recorder = new Recorder();
        var scheduler = Scheduler(recorder, ("a", gate));
        var first = scheduler.Enqueue(Capability("a.one", "a"), [@"C:\x\1.png"])!;
        var second = scheduler.Enqueue(Capability("a.two", "a"), [@"C:\x\2.png", @"C:\x\3.png"])!;
        await Entered(gate);
        second.Cancel();
        gate.Release(0);
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(gate.Calls);
        Assert.All(recorder.Finished[1], i => Assert.Equal(ErrorCode.Cancelled, i.Result.Error));
        Assert.Equal(2, recorder.Finished[1].Count);
    }

    [Fact]
    public async Task A_running_job_that_is_cancelled_stops_and_reports_cancelled()
    {
        var gate = new Gate();
        var recorder = new Recorder();
        var scheduler = Scheduler(recorder, ("a", gate));
        var job = scheduler.Enqueue(Capability("a.one", "a"), [@"C:\x\1.png"])!;
        await Entered(gate);
        job.Cancel();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ErrorCode.Cancelled, Assert.Single(recorder.Finished.Single()).Result.Error);
    }

    [Fact]
    public async Task Cancel_all_stops_queued_and_running_jobs()
    {
        var gate = new Gate();
        var recorder = new Recorder();
        var scheduler = Scheduler(recorder, ("a", gate));
        var jobs = new[] { scheduler.Enqueue(Capability("a.one", "a"), [@"C:\x\1.png"])!, scheduler.Enqueue(Capability("a.two", "a"), [@"C:\x\2.png"])! };
        await Entered(gate);
        scheduler.CancelAll();
        await Task.WhenAll(jobs.Select(j => j.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(gate.Calls);
        Assert.All(recorder.Finished.SelectMany(f => f), i => Assert.Equal(ErrorCode.Cancelled, i.Result.Error));
    }

    [Fact]
    public async Task A_selection_capability_gets_one_request_with_every_file_and_per_file_gets_one_each()
    {
        var gate = new Gate();
        var scheduler = Scheduler(new Recorder(), ("a", gate));
        string[] files = [@"C:\x\1.pdf", @"C:\x\2.pdf", @"C:\x\3.pdf"];
        var whole = scheduler.Enqueue(Capability("a.merge", "a", CapabilityShape.Selection), files)!;
        await Entered(gate);
        gate.Release(0);
        await whole.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var each = scheduler.Enqueue(Capability("a.info", "a"), files)!;
        await Entered(gate);
        gate.Release(1);
        await each.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(files, Assert.Single(gate.Calls[0]).Inputs);
        Assert.Equal(files, gate.Calls[1].Select(r => Assert.Single(r.Inputs)));
    }

    [Fact]
    public void A_capability_of_an_unknown_module_is_refused()
    {
        var scheduler = Scheduler(new Recorder(), ("a", new Gate()));
        Assert.Null(scheduler.Enqueue(Capability("z.one", "z"), [@"C:\x\1.png"]));
    }
}
