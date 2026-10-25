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

    [Fact]
    public void Windows_normalizes_to_nfkc_without_dotnet_globalization()
    {
        // The product runs with InvariantGlobalization, where string.Normalize leaves non-ASCII text alone.
        Assert.Equal("fine", UnicodeText.ToFormKC("\uFB01ne"));
        Assert.Equal("لا", UnicodeText.ToFormKC("\uFEFB"));
        Assert.Equal("a b", UnicodeText.ToFormKC("a\u00A0b"));
        Assert.Equal("plain", UnicodeText.ToFormKC("plain"));
        Assert.Null(UnicodeText.ToFormKC("lone \uD800 half"));
        Assert.Equal("fine", PasswordPolicy.Check("\uFB01ne", UnicodeText.ToFormKC).Password);
    }
}
