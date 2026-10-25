using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;
using WinTools.Host;
using WinTools.Host.Jobs;
using WinTools.Windows;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 2.2 (ADR-0026): the host side of the secret channel. A password typed in the dialog reaches only the requests
/// of the job it was typed for, never a log, and is dropped when the job ends; a command that needs one never runs
/// without it. Windows' NFKC is the normalizer the policy gets. Written before the code.
/// </summary>
public sealed class JobSecretTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly HostLog _log;
    private readonly string _logPath;

    public JobSecretTests()
    {
        _logPath = Path.Combine(_dir.Path, "host.jsonl");
        _log = HostLog.Open(_logPath);
    }

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    private const string Secret = "pw-سر-123 😀";

    private sealed class Module(string name) : IJobModule
    {
        public string Name => name;
        public bool IsBelievable(JobRequest request, JobResult result) => true;
        public IEnumerable<string> TempPathsOf(JobRequest request) => [];
    }

    /// <summary>Records every request and answers each with facts at once.</summary>
    private sealed class Recording : IJobExecutor
    {
        public readonly List<JobRequest> Requests = [];
        public bool JobObjectAssigned => true;
        public int LastLaneCount => 1;

        public Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken, Action<int>? onFileDone = null)
        {
            lock (Requests) Requests.AddRange(requests);
            return Task.FromResult<IReadOnlyList<JobResult>>([.. requests.Select(r => JobResult.WithFacts(r.Id, [new JobFact("done", "yes")]))]);
        }
    }

    private sealed class Quiet : IJobFeedback
    {
        public void Started(CapabilityDescriptor capability, IReadOnlyList<string> paths) { }
        public void Progress(CapabilityDescriptor capability, int done, int total) { }
        public void Finished(CapabilityDescriptor capability, IReadOnlyList<JobItem> items) { }
        public void Crashed(CapabilityDescriptor capability, int files) { }
        public void SecretNeeded(CapabilityDescriptor capability) { }
    }

    private static CapabilityDescriptor Capability(string id, SecretPrompt? prompt = null) =>
        new(id, id, "tools", "m", new InputRule(WinTools.Core.Files.FileKinds.All.Select(k => k.Id).ToHashSet()), CapabilityShape.PerFile,
            CapabilityOutput.Facts, LongRunning: true, Prompt: prompt);

    private JobScheduler Scheduler(Recording executor) =>
        new([new JobModuleRunner(new Module("m"), executor, _ => new Dictionary<string, string> { ["setting"] = "from settings" })], _log, () => new AppSettings(), new Quiet());

    private static Dictionary<string, string> Password(string value) => new() { [PasswordPolicy.ParameterName] = value };

    [Fact]
    public async Task A_secret_reaches_only_the_requests_of_its_own_job()
    {
        var executor = new Recording();
        var scheduler = Scheduler(executor);
        var a = scheduler.Enqueue(Capability("with", new("Title", Confirm: true)), [@"C:\p\a.pdf", @"C:\p\b.pdf"], Password(Secret))!;
        var b = scheduler.Enqueue(Capability("without"), [@"C:\p\c.pdf"])!;
        await Task.WhenAll(a.Completion, b.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        var with = executor.Requests.Where(r => r.CapabilityId == "with").ToArray();
        Assert.Equal(2, with.Length);
        Assert.All(with, r => Assert.Equal(Secret, r.Parameter(PasswordPolicy.ParameterName)));
        Assert.All(with, r => Assert.Equal("from settings", r.Parameter("setting"))); // the module's own parameters stay
        Assert.Null(executor.Requests.Single(r => r.CapabilityId == "without").Parameter(PasswordPolicy.ParameterName));
    }

    [Fact]
    public void A_command_that_asks_for_a_secret_never_runs_without_one()
    {
        var executor = new Recording();
        var scheduler = Scheduler(executor);
        Assert.Null(scheduler.Enqueue(Capability("with", new("Title", Confirm: false)), [@"C:\p\a.pdf"]));
        Assert.Null(scheduler.Enqueue(Capability("with", new("Title", Confirm: false)), [@"C:\p\a.pdf"], Password("")));
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public void A_secret_job_may_carry_the_normalized_variant_and_nothing_else()
    {
        var scheduler = Scheduler(new Recording());
        var both = new Dictionary<string, string> { [PasswordPolicy.ParameterName] = "x\u00B2", [PasswordPolicy.NormalizedParameterName] = "x2" };
        Assert.NotNull(scheduler.Enqueue(Capability("with", new("Title", Confirm: false)), [@"C:\p\a.pdf"], both));
        var other = new Dictionary<string, string> { [PasswordPolicy.ParameterName] = "a", ["more"] = "b" };
        Assert.Null(scheduler.Enqueue(Capability("with", new("Title", Confirm: false)), [@"C:\p\a.pdf"], other));
        Assert.Null(scheduler.Enqueue(Capability("with", new("Title", Confirm: false)), [@"C:\p\a.pdf"],
            new Dictionary<string, string> { [PasswordPolicy.NormalizedParameterName] = "x2" })); // never the variant alone
    }

    [Fact]
    public void A_command_that_asks_for_none_takes_none()
    {
        var scheduler = Scheduler(new Recording());
        Assert.Null(scheduler.Enqueue(Capability("without"), [@"C:\p\a.pdf"], Password(Secret)));
    }

    [Fact]
    public async Task The_secret_is_never_logged_and_is_dropped_when_the_job_ends()
    {
        var scheduler = Scheduler(new Recording());
        var job = scheduler.Enqueue(Capability("with", new("Title", Confirm: true)), [@"C:\p\a.pdf"], Password(Secret))!;
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(job.Parameters);
        _log.Dispose();
        var text = File.ReadAllText(_logPath);
        Assert.Contains("job.done", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pw-", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordPolicy.ParameterName, text, StringComparison.Ordinal);
    }

    /// <summary>Answers every request with "a password is needed", as the archive worker does for a locked ZIP.</summary>
    private sealed class Locked : IJobExecutor
    {
        public readonly List<JobRequest> Requests = [];
        public bool JobObjectAssigned => true;
        public int LastLaneCount => 1;

        public Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken, Action<int>? onFileDone = null)
        {
            lock (Requests) Requests.AddRange(requests);
            return Task.FromResult<IReadOnlyList<JobResult>>([.. requests.Select(r => JobResult.Fail(r.Id, ErrorCode.PasswordRequired, "needs one"))]);
        }
    }

    private sealed class Told : IJobFeedback
    {
        public int Finished, SecretNeeded;
        public void Started(CapabilityDescriptor capability, IReadOnlyList<string> paths) { }
        public void Progress(CapabilityDescriptor capability, int done, int total) { }
        void IJobFeedback.Finished(CapabilityDescriptor capability, IReadOnlyList<JobItem> items) => Interlocked.Increment(ref Finished);
        void IJobFeedback.SecretNeeded(CapabilityDescriptor capability) => Interlocked.Increment(ref SecretNeeded);
        public void Crashed(CapabilityDescriptor capability, int files) { }
    }

    private static readonly SecretPrompt OnDemand = new("Archive password", Confirm: false, WhenRequired: true);

    [Fact]
    public void A_command_that_asks_only_when_needed_runs_without_a_secret_or_with_one_and_nothing_else()
    {
        var executor = new Recording();
        var scheduler = Scheduler(executor);
        Assert.NotNull(scheduler.Enqueue(Capability("extract", OnDemand), [@"C:\p\a.zip"]));
        Assert.NotNull(scheduler.Enqueue(Capability("extract", OnDemand), [@"C:\p\a.zip"], Password(Secret)));
        var both = new Dictionary<string, string> { [PasswordPolicy.ParameterName] = "x\u00B2", [PasswordPolicy.NormalizedParameterName] = "x2" };
        Assert.NotNull(scheduler.Enqueue(Capability("extract", OnDemand), [@"C:\p\a.zip"], both));
        Assert.Null(scheduler.Enqueue(Capability("extract", OnDemand), [@"C:\p\a.zip"], new Dictionary<string, string> { ["more"] = "x" }));
        Assert.Null(scheduler.Enqueue(Capability("extract", OnDemand), [@"C:\p\a.zip"], Password("")));
    }

    private JobScheduler Scheduler(IJobExecutor executor, IJobFeedback feedback) =>
        new([new JobModuleRunner(new Module("m"), executor, _ => null)], _log, () => new AppSettings(), feedback);

    [Fact]
    public async Task One_archive_that_needs_a_password_asks_for_it_instead_of_a_toast()
    {
        var told = new Told();
        var job = Scheduler(new Locked(), told).Enqueue(Capability("extract", OnDemand), [@"C:\p\a.zip"])!;
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(job.SecretNeeded);
        Assert.Equal((0, 1), (told.Finished, told.SecretNeeded));
    }

    [Fact]
    public async Task Several_archives_or_a_password_already_given_get_the_toast_and_never_a_second_ask()
    {
        var told = new Told();
        var scheduler = Scheduler(new Locked(), told);
        var several = scheduler.Enqueue(Capability("extract", OnDemand), [@"C:\p\a.zip", @"C:\p\b.zip"])!;
        await several.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var given = scheduler.Enqueue(Capability("extract", OnDemand), [@"C:\p\a.zip"], Password(Secret))!;
        await given.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var asksFirst = scheduler.Enqueue(Capability("protect", new("Title", Confirm: false)), [@"C:\p\a.pdf"], Password(Secret))!;
        await asksFirst.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(several.SecretNeeded || given.SecretNeeded || asksFirst.SecretNeeded);
        Assert.Equal((3, 0), (told.Finished, told.SecretNeeded));
    }

    /// <summary>Holds each run until released, then answers "a password is needed".</summary>
    private sealed class GatedLocked : IJobExecutor
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool JobObjectAssigned => true;
        public int LastLaneCount => 1;

        public async Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken, Action<int>? onFileDone = null)
        {
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return [.. requests.Select(r => JobResult.Fail(r.Id, ErrorCode.PasswordRequired, "needs one"))];
        }
    }

    [Fact]
    public async Task A_job_cancelled_while_it_ran_never_asks_for_a_password()
    {
        // Evaluator, PR 5.3: a run that ends "a password is needed" while the host is exiting must not open the dialog.
        var told = new Told();
        var executor = new GatedLocked();
        var job = Scheduler(executor, told).Enqueue(Capability("extract", OnDemand), [@"C:\p\a.zip"])!;
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        job.Cancel();
        executor.Release.TrySetResult();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(job.SecretNeeded);
        Assert.Equal(0, told.SecretNeeded);
    }

    [Fact]
    public void Windows_normalizes_to_nfkc_without_dotnet_globalization()
    {
        // The product runs with InvariantGlobalization, where string.Normalize leaves non-ASCII text alone.
        Assert.Equal("fine", UnicodeText.ToFormKC("\uFB01ne"));
        Assert.Equal("لا", UnicodeText.ToFormKC("\uFEFB"));
        Assert.Equal("a b", UnicodeText.ToFormKC("a\u00A0b"));
        Assert.Equal("plain", UnicodeText.ToFormKC("plain"));
        Assert.Null(UnicodeText.ToFormKC("lone \uD800 half"));
        Assert.False(PasswordPolicy.CheckNew("\uFB01ne", UnicodeText.ToFormKC).Ok); // changed by NFKC: refused as a new password
        Assert.Equal("fine", PasswordPolicy.CheckExisting("\uFB01ne", UnicodeText.ToFormKC).Normalized);
        Assert.Equal("2", UnicodeText.ToFormKC("\u00B2"));
        Assert.Equal("\u0634\u064E\u0651", UnicodeText.ToFormC("\u0634\u0651\u064E")); // canonical reordering of marks
        Assert.Equal("\u00E9", UnicodeText.ToFormC("e\u0301"));
        Assert.Contains("order", PasswordPolicy.CheckNew("\u0634\u0651\u064E", UnicodeText.ToFormKC, UnicodeText.ToFormC).Problem, StringComparison.Ordinal);
        Assert.True(PasswordPolicy.CheckNew("\u0634\u064E\u0651", UnicodeText.ToFormKC, UnicodeText.ToFormC).Ok); // the order NFKC keeps
    }
}
