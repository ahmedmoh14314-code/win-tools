using System.Diagnostics;
using System.Threading.Channels;
using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;

namespace WinTools.Host.Jobs;

/// <summary>What the user is told about a job. Called on pool threads; the implementation marshals to the UI thread.</summary>
internal interface IJobFeedback
{
    void Started(CapabilityDescriptor capability, IReadOnlyList<string> paths);
    void Progress(CapabilityDescriptor capability, int done, int total);
    void Finished(CapabilityDescriptor capability, IReadOnlyList<JobItem> items);
    void Crashed(CapabilityDescriptor capability, int files);

    /// <summary>One archive needs a password (ADR-0027): the host asks for it instead of a toast; any progress shown ends.</summary>
    void SecretNeeded(CapabilityDescriptor capability);
}

/// <summary>What runs one module's requests: its pool of workers (<see cref="WorkerClient"/>), or a fake in tests.</summary>
internal interface IJobExecutor
{
    Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken, Action<int>? onFileDone = null);
    bool JobObjectAssigned { get; }
    int LastLaneCount { get; }
}

/// <summary>One module as the scheduler runs it: its host-side knowledge, what runs its requests, and its parameters.</summary>
internal sealed record JobModuleRunner(IJobModule Module, IJobExecutor Client, Func<AppSettings, IReadOnlyDictionary<string, string>?> Parameters);

/// <summary>A job the scheduler accepted: it can be cancelled, queued or running, and awaited.</summary>
internal sealed class ScheduledJob(string id, CapabilityDescriptor capability, IReadOnlyList<string> paths,
    IReadOnlyDictionary<string, string>? parameters = null) : IDisposable
{
    private volatile IReadOnlyDictionary<string, string>? _parameters = parameters;

    /// <summary>
    /// This job's own parameters: the secret typed for it (V2 PR 2.2, ADR-0026), on top of the module's from settings.
    /// Dropped when the job ends, so the host keeps a password no longer than the job that needed it. Never logged.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? Parameters => _parameters;

    internal void DropParameters() => _parameters = null;

    private volatile bool _secretNeeded;

    /// <summary>
    /// The job ran without a secret and its one input needs one (V2 PR 5.3, ADR-0027): the user was told nothing, and the
    /// host asks for the password and queues the job again with it. Set before <see cref="Completion"/> completes.
    /// </summary>
    public bool SecretNeeded => _secretNeeded;

    internal void MarkSecretNeeded() => _secretNeeded = true;

    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id { get; } = id;
    public CapabilityDescriptor Capability { get; } = capability;
    public IReadOnlyList<string> Paths { get; } = paths;
    public Task Completion => _done.Task;
    internal CancellationToken Token => _cancel.Token;

    private volatile bool _cancelRequested;

    /// <summary>Whether the job was cancelled. Set before the token is signalled, because signalling runs the running
    /// job's continuations at once, and the queue may move on before the next job's token is signalled.</summary>
    public bool CancelRequested => _cancelRequested;

    /// <summary>Stops the job: a queued job never starts, a running one stops taking files and its workers are asked to stop.</summary>
    public void Cancel()
    {
        MarkCancelled();
        try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal void MarkCancelled() => _cancelRequested = true;

    internal void Complete() => _done.TrySetResult();

    public void Dispose() => _cancel.Dispose();
}

/// <summary>
/// Runs dropped commands off the UI thread (ADR-0018). Each module has a first-in, first-out queue and runs one job at a
/// time, so a second drop on the same module waits for the first; jobs of different modules run side by side, each with
/// its module's own workers and limits. Every job can be cancelled while queued or running. Every result is logged in
/// detail; the user gets a summary through <see cref="IJobFeedback"/> (toast, info panel, sound).
/// </summary>
internal sealed class JobScheduler
{
    private readonly IReadOnlyDictionary<string, JobModuleRunner> _modules;
    private readonly Dictionary<string, Channel<ScheduledJob>> _queues = [];
    private readonly HostLog _log;
    private readonly Func<AppSettings> _settings;
    private readonly IJobFeedback _feedback;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScheduledJob> _live = new();

    public JobScheduler(IEnumerable<JobModuleRunner> modules, HostLog log, Func<AppSettings> settings, IJobFeedback feedback)
    {
        _modules = modules.ToDictionary(m => m.Module.Name, StringComparer.Ordinal);
        (_log, _settings, _feedback) = (log, settings, feedback);
        foreach (var name in _modules.Keys)
        {
            var queue = Channel.CreateUnbounded<ScheduledJob>(new UnboundedChannelOptions { SingleReader = true });
            _queues[name] = queue;
            _ = Task.Run(() => ConsumeAsync(_modules[name], queue.Reader));
        }
    }

    /// <summary>Whether a module of this name is registered: a drop for any other is refused before it is accepted.</summary>
    public bool Runs(string module) => _queues.ContainsKey(module);

    /// <summary>
    /// Queues a job for the capability's module; null when no module of that name is registered. A command that declares
    /// a secret prompt runs only with a non-empty password in <paramref name="parameters"/>, and only such a command takes
    /// parameters of its own (ADR-0026). One whose prompt comes only when required runs without, or with the password
    /// (ADR-0027).
    /// </summary>
    public ScheduledJob? Enqueue(CapabilityDescriptor capability, IReadOnlyList<string> paths, IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (!_queues.TryGetValue(capability.Module, out var queue)) return null;
        // A prompt command: the password, and for an existing one at most its normalized form beside it (ADR-0026).
        var secret = parameters is not null && parameters.TryGetValue(PasswordPolicy.ParameterName, out var p) && p.Length > 0
            && parameters.Keys.All(k => k is PasswordPolicy.ParameterName || k is PasswordPolicy.NormalizedParameterName && capability.Prompt is { Confirm: false });
        var none = parameters is not { Count: > 0 };
        // A command with a dialog: exactly the dialog's answer (V2 PR 7.2).
        var answered = parameters is { Count: 1 } && parameters.TryGetValue(DialogParameters.Name, out var d) && d.Length > 0;
        if (!(capability.Dialog is not null ? answered : capability.Prompt switch { null => none, { WhenRequired: true } => none || secret, _ => secret }))
        {
            _log.Event("job.refused", new { op = capability.Id, reason = capability.Prompt is not null ? "noSecret" : "unexpectedParameters" });
            return null;
        }
        var job = new ScheduledJob(Guid.NewGuid().ToString("N"), capability, paths, parameters);
        _live[job.Id] = job;
        _log.Event("job.queued", new { job = job.Id, op = capability.Id, module = capability.Module, files = paths.Count });
        // A command that fills the clipboard runs at once, beside the queue: behind a long hash it would overwrite whatever
        // the user copied meanwhile, minutes later (evaluator, PR #18).
        if (capability.Produces == CapabilityOutput.Clipboard) { _ = Task.Run(() => RunOneAsync(_modules[capability.Module], job)); return job; }
        if (!queue.Writer.TryWrite(job)) { _live.TryRemove(job.Id, out _); return null; }
        return job;
    }

    /// <summary>Cancels every queued and running job (the host is exiting); the task ends when all of them have finished,
    /// so the exit can give running workers their grace and clean their temps.</summary>
    public Task CancelAll()
    {
        var jobs = _live.Values.ToArray();
        foreach (var job in jobs) job.MarkCancelled(); // first, so no queued job starts while the running one unwinds
        foreach (var job in jobs) job.Cancel();
        return Task.WhenAll(jobs.Select(j => j.Completion));
    }

    private async Task ConsumeAsync(JobModuleRunner runner, ChannelReader<ScheduledJob> reader)
    {
        await foreach (var job in reader.ReadAllAsync().ConfigureAwait(false)) await RunOneAsync(runner, job).ConfigureAwait(false);
    }

    private async Task RunOneAsync(JobModuleRunner runner, ScheduledJob job)
    {
        // Nothing a job throws may end the queue: RunAsync catches, but its catch logs and tells the user, and either can
        // throw too (evaluator, PR #16). The next drop on this module must still run.
        try { await RunAsync(runner, job).ConfigureAwait(false); }
        catch (Exception) { }
        finally
        {
            job.DropParameters();
            _live.TryRemove(job.Id, out _);
            job.Complete();
            job.Dispose();
        }
    }

    private async Task RunAsync(JobModuleRunner runner, ScheduledJob job)
    {
        var capability = job.Capability;
        var paths = job.Paths;
        try
        {
            var s = _settings();
            var outputDir = s.OutputPolicy == OutputPolicy.Folder ? s.OutputFolder : null;
            var parameters = Merged(runner.Parameters(s), job.Parameters);
            JobRequest Request(IReadOnlyList<string> inputs) => new(Guid.NewGuid().ToString("N"), capability.Id, inputs, outputDir, parameters);
            var requests = capability.Shape == CapabilityShape.PerFile ? paths.Select(p => Request([p])).ToArray() : [Request(paths)];
            var started = Stopwatch.GetTimestamp();
            _log.Event("job.start", new { job = job.Id, op = capability.Id, files = paths.Count, requests = requests.Length });
            _feedback.Started(capability, paths);
            var results = job.CancelRequested
                ? requests.Select(r => JobResult.Fail(r.Id, ErrorCode.Cancelled, "Cancelled before it ran.")).ToArray()
                : await runner.Client.RunAsync(requests, job.Token, done => _feedback.Progress(capability, done, requests.Length)).ConfigureAwait(false);
            foreach (var r in results)
                _log.Event("job.result", new { id = r.Id, ok = r.Success, error = r.Error.ToString(), r.Message, output = r.OutputPath is null ? null : Path.GetFileName(r.OutputPath), outputs = r.Outputs.Count, facts = r.Facts.Count });
            _log.Event("job.done", new { job = job.Id, op = capability.Id, ok = results.Count(r => r.Success), nothingToDo = results.Count(r => r.Error == ErrorCode.NothingToDo),
                failed = results.Count(r => !r.Success && r.Error != ErrorCode.NothingToDo), cancelled = results.Count(r => r.Error == ErrorCode.Cancelled),
                ms = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), jobObject = runner.Client.JobObjectAssigned, workers = runner.Client.LastLaneCount });
            // One archive that needs a password (ADR-0027): asked for instead of a toast, and the job runs again with it.
            // Only a run without a secret asks, so a wrong password is a toast and never a loop.
            // Nor does a job cancelled meanwhile: the host may be exiting (evaluator, PR 5.3).
            if (capability.Prompt is { WhenRequired: true } && job.Parameters is null && !job.CancelRequested
                && results is [{ Success: false, Error: ErrorCode.PasswordRequired }])
            {
                job.MarkSecretNeeded();
                _log.Event("job.secretNeeded", new { job = job.Id, op = capability.Id });
                _feedback.SecretNeeded(capability);
                return;
            }
            var items = requests.Zip(results, (q, r) => new JobItem(q.Inputs.Count == 1 ? NameOf(q.Inputs[0]) : $"{q.Inputs.Count} items", r,
                q.Inputs.Count == 1 ? SizeOf(q.Inputs[0]) : null, r.OutputPath is { } o ? SizeOf(o) : null, q.Inputs.Count)).ToArray();
            _feedback.Finished(capability, items);
        }
        catch (Exception ex)
        {
            _log.Event("job.error", new { job = job.Id, type = ex.GetType().Name });
            _feedback.Crashed(capability, paths.Count);
        }
    }

    /// <summary>The module's parameters from settings with the job's own on top; null when there are none.</summary>
    private static IReadOnlyDictionary<string, string>? Merged(IReadOnlyDictionary<string, string>? module, IReadOnlyDictionary<string, string>? job)
    {
        if (job is not { Count: > 0 }) return module;
        var all = module is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(module, StringComparer.Ordinal);
        foreach (var (k, v) in job) all[k] = v;
        return all;
    }

    /// <summary>The name shown for a path: a marked folder's own name, not the empty name after its separator (evaluator, PR #18).</summary>
    private static string NameOf(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    /// <summary>A file's size for the summary ("2.4 MB → 610 KB"); null when it cannot be read. Never opens the file.</summary>
    private static long? SizeOf(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? f.Length : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
