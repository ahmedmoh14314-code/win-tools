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
internal sealed class ScheduledJob(string id, CapabilityDescriptor capability, IReadOnlyList<string> paths) : IDisposable
{
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

    /// <summary>Queues a job for the capability's module; null when no module of that name is registered.</summary>
    public ScheduledJob? Enqueue(CapabilityDescriptor capability, IReadOnlyList<string> paths)
    {
        if (!_queues.TryGetValue(capability.Module, out var queue)) return null;
        var job = new ScheduledJob(Guid.NewGuid().ToString("N"), capability, paths);
        _live[job.Id] = job;
        _log.Event("job.queued", new { job = job.Id, op = capability.Id, module = capability.Module, files = paths.Count });
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
        await foreach (var job in reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Nothing a job throws may end the queue: RunAsync catches, but its catch logs and tells the user, and either can
            // throw too (evaluator, PR #16). The next drop on this module must still run.
            try { await RunAsync(runner, job).ConfigureAwait(false); }
            catch (Exception) { }
            finally
            {
                _live.TryRemove(job.Id, out _);
                job.Complete();
                job.Dispose();
            }
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
            var parameters = runner.Parameters(s);
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
            var items = requests.Zip(results, (q, r) => new JobItem(q.Inputs.Count == 1 ? Path.GetFileName(q.Inputs[0]) : $"{q.Inputs.Count} files", r,
                q.Inputs.Count == 1 ? SizeOf(q.Inputs[0]) : null, r.OutputPath is { } o ? SizeOf(o) : null)).ToArray();
            _feedback.Finished(capability, items);
        }
        catch (Exception ex)
        {
            _log.Event("job.error", new { job = job.Id, type = ex.GetType().Name });
            _feedback.Crashed(capability, paths.Count);
        }
    }

    /// <summary>A file's size for the summary ("2.4 MB → 610 KB"); null when it cannot be read. Never opens the file.</summary>
    private static long? SizeOf(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? f.Length : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
