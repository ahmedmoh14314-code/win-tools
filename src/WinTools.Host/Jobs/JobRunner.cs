using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;

namespace WinTools.Host.Jobs;

/// <summary>What the user is told about a batch. Called on pool threads; the implementation marshals to the UI thread.</summary>
internal interface IJobFeedback
{
    void Started(Operation operation, IReadOnlyList<string> paths);
    void Progress(Operation operation, int done, int total);
    void Finished(Operation operation, IReadOnlyList<JobItem> items);
    void Crashed(Operation operation, int files);
}

/// <summary>
/// Runs dropped commands off the UI thread, one batch at a time (a second drop waits for the first). Every result is
/// logged in detail; the user gets a summary through <see cref="IJobFeedback"/> (toast, info panel, sound).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "SemaphoreSlim without AvailableWaitHandle holds no OS handle; jobs may still be running at process exit.")]
internal sealed class JobRunner(WorkerClient client, HostLog log, Func<AppSettings> settings, IJobFeedback feedback)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task Start(Operation operation, IReadOnlyList<string> paths) => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var s = settings();
            var outputDir = s.OutputPolicy == OutputPolicy.Folder ? s.OutputFolder : null;
            var requests = paths.Select(p => new ConversionRequest(Guid.NewGuid().ToString("N"), operation.Id, p, outputDir, s.JpegQuality, s.WebPQuality)).ToArray();
            var started = Stopwatch.GetTimestamp();
            log.Event("job.start", new { op = operation.Id, files = requests.Length });
            feedback.Started(operation, paths);
            var results = await client.RunAsync(requests, CancellationToken.None, done => feedback.Progress(operation, done, requests.Length)).ConfigureAwait(false);
            foreach (var r in results)
                log.Event("job.result", new { id = r.Id, ok = r.Success, error = r.Error.ToString(), r.Message, output = r.OutputPath is null ? null : Path.GetFileName(r.OutputPath) });
            log.Event("job.done", new { op = operation.Id, ok = results.Count(r => r.Success), nothingToDo = results.Count(r => r.Error == ErrorCode.NothingToDo),
                failed = results.Count(r => !r.Success && r.Error != ErrorCode.NothingToDo), ms = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), jobObject = client.JobObjectAssigned, workers = client.LastLaneCount });
            var items = requests.Zip(results, (q, r) => new JobItem(Path.GetFileName(q.SourcePath), r, SizeOf(q.SourcePath), r.OutputPath is { } o ? SizeOf(o) : null)).ToArray();
            feedback.Finished(operation, items);
        }
        catch (Exception ex)
        {
            log.Event("job.error", new { type = ex.GetType().Name });
            feedback.Crashed(operation, paths.Count);
        }
        finally
        {
            _gate.Release();
        }
    });

    /// <summary>A file's size for the summary ("2.4 MB → 610 KB"); null when it cannot be read. Never opens the file.</summary>
    private static long? SizeOf(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? f.Length : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
