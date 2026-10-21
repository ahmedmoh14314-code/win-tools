using System.Diagnostics;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Settings;

namespace WinTools.Host.Jobs;

/// <summary>
/// Runs dropped commands off the UI thread, one batch at a time (a second drop waits for the first). Results are
/// logged; any failure plays the system error sound, since this slice has no notification UI.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "SemaphoreSlim without AvailableWaitHandle holds no OS handle; jobs may still be running at process exit.")]
internal sealed class JobRunner(WorkerClient client, HostLog log, AppSettings settings, Action onFailure, Action onNothingToDo, Action<IReadOnlyList<ImageInfo>> showInfo)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task Start(Operation operation, IReadOnlyList<string> paths) => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var outputDir = settings.OutputPolicy == OutputPolicy.Folder ? settings.OutputFolder : null;
            var requests = paths.Select(p => new ConversionRequest(Guid.NewGuid().ToString("N"), operation.Id, p, outputDir, settings.JpegQuality, settings.WebPQuality)).ToArray();
            var started = Stopwatch.GetTimestamp();
            log.Event("job.start", new { op = operation.Id, files = requests.Length });
            var results = await client.RunAsync(requests, CancellationToken.None).ConfigureAwait(false);
            foreach (var r in results)
                log.Event("job.result", new { id = r.Id, ok = r.Success, error = r.Error.ToString(), r.Message, output = r.OutputPath is null ? null : Path.GetFileName(r.OutputPath) });
            log.Event("job.done", new { op = operation.Id, ok = results.Count(r => r.Success), nothingToDo = results.Count(r => r.Error == ErrorCode.NothingToDo),
                failed = results.Count(r => !r.Success && r.Error != ErrorCode.NothingToDo), ms = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), jobObject = client.JobObjectAssigned, workers = client.LastLaneCount });
            var facts = results.Where(r => r.Info is not null).Select(r => r.Info!).ToArray();
            if (facts.Length > 0) _ = Task.Run(() => showInfo(facts)); // a modal box must not hold the next drop back
            if (results.Any(r => !r.Success && r.Error != ErrorCode.NothingToDo)) onFailure();
            else if (results.Any(r => r.Error == ErrorCode.NothingToDo)) onNothingToDo(); // "already fits": a different, gentler cue
        }
        catch (Exception ex)
        {
            log.Event("job.error", new { type = ex.GetType().Name });
            onFailure();
        }
        finally
        {
            _gate.Release();
        }
    });
}
