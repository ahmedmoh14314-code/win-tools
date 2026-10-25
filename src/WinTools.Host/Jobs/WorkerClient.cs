using System.Diagnostics;
using System.Text;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Windows;

namespace WinTools.Host.Jobs;

/// <summary>
/// Runs a batch of one module's jobs (ADR-0018) in a bounded pool of disposable worker processes and maps every way a
/// worker can fail to a structured result per request. What a believable answer is, and which temporary files a killed
/// worker may leave, is the module's knowledge (<see cref="IJobModule"/>).
/// <list type="bullet">
/// <item>Lanes (at most <see cref="DefaultParallelism"/>, never more than the number of files) take files from one queue,
/// each with its own worker. Each worker may commit up to <see cref="WorkerJobObject.WorkerMemoryLimitBytes"/>, so there
/// are never more lanes than can all reach that cap within the commit the system has left when the batch starts, less a
/// reserve for everything else (ADR-0015).</item>
/// <item>A crash, a hang past the per-job timeout or a protocol violation fails only the file being processed: its worker
/// is killed, its known temporary file is deleted, and the lane starts a new worker for the next file. A lane gives up
/// after its first worker and <see cref="MaxRestartsPerLane"/> restarts have all failed in a row (a success resets the count);
/// what is left is reported as not run.</item>
/// <item>Cancellation stops taking new requests. The running ones are sent a cancel message, and a worker that has not
/// answered within <see cref="CancelGrace"/> is killed; the unfinished ones are reported as Cancelled.</item>
/// <item>Lines of a type the host does not handle (a worker's progress, for instance) are skipped.</item>
/// <item>The host re-checks every success claim through the module (for images: allowed name, real format, SHA-256).</item>
/// </list>
/// </summary>
internal sealed class WorkerClient(string workerPath, TimeSpan jobTimeout, IJobModule module, IReadOnlyDictionary<string, string>? environment = null, int parallelism = 0) : IJobExecutor
{
    /// <summary>Measured choice (docs/performance): half the logical processors, between 1 and 4, bounded by memory.</summary>
    public static int DefaultParallelism => Lanes(Environment.ProcessorCount, SystemMemory.AvailableCommitBytes());

    /// <summary>Commit left for the rest of the system when every worker is at its cap.</summary>
    public const long CommitReserveBytes = 1L << 30;

    /// <summary>
    /// Half the processors, but no more workers than can all reach the memory cap within the available commit less
    /// <see cref="CommitReserveBytes"/>; between 1 and 4. Unknown memory counts as enough.
    /// </summary>
    internal static int Lanes(int processors, ulong? availableCommitBytes)
    {
        var byMemory = availableCommitBytes is { } a
            ? (int)Math.Min(4, Math.Max(0, (long)Math.Min(a, long.MaxValue) - CommitReserveBytes) / WorkerJobObject.WorkerMemoryLimitBytes)
            : 4;
        return Math.Clamp(Math.Min(processors / 2, byMemory), 1, 4);
    }

    public const int MaxRestartsPerLane = 3;

    /// <summary>How long a worker that was sent a cancel message may take to answer before it is killed.</summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(1);

    /// <summary>Whether every worker of the last batch was placed in the host's kill-on-close job object (logged by the caller).</summary>
    public bool JobObjectAssigned { get; private set; }

    /// <summary>How many workers the last batch used (logged by the caller).</summary>
    public int LastLaneCount { get; private set; }

    /// <param name="onFileDone">Called on a pool thread with the number of files finished so far, after each one (progress).</param>
    public async Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken,
        Action<int>? onFileDone = null)
    {
        if (requests.Count == 0) return [];
        if (requests.Any(r => r.Inputs.Count == 0 || !r.Inputs.All(PathPolicy.IsSupported))) // also checked by the resolver; the pipe must never carry one
            return requests.Select(r => JobResult.Fail(r.Id, ErrorCode.InvalidRequest, "Unsupported source path.")).ToArray();

        var results = new JobResult?[requests.Count];
        var queue = new System.Collections.Concurrent.ConcurrentQueue<int>(Enumerable.Range(0, requests.Count));
        var lanes = Math.Clamp(parallelism > 0 ? parallelism : DefaultParallelism, 1, requests.Count);
        LastLaneCount = lanes;
        var allAssigned = true;
        var done = 0;
        void Done() => onFileDone?.Invoke(Interlocked.Increment(ref done));
        await Task.WhenAll(Enumerable.Range(0, lanes).Select(_ => Task.Run(async () =>
        {
            if (!await RunLaneAsync(queue, requests, results, Done, cancellationToken).ConfigureAwait(false)) allAssigned = false;
        }, CancellationToken.None))).ConfigureAwait(false);
        JobObjectAssigned = allAssigned;
        for (var i = 0; i < results.Length; i++)
            results[i] ??= cancellationToken.IsCancellationRequested
                ? JobResult.Fail(requests[i].Id, ErrorCode.Cancelled, "Cancelled before it ran.")
                : JobResult.Fail(requests[i].Id, ErrorCode.WorkerCrashed, "Not run: the workers kept failing.");
        return results!;
    }

    /// <summary>One lane: a worker that processes files from the queue until it is empty. Returns whether every worker it started joined the job object.</summary>
    private async Task<bool> RunLaneAsync(System.Collections.Concurrent.ConcurrentQueue<int> queue, IReadOnlyList<JobRequest> requests,
        JobResult?[] results, Action done, CancellationToken cancellationToken)
    {
        Process? process = null;
        var restarts = 0;
        var assigned = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested && queue.TryDequeue(out var i))
            {
                var request = requests[i];
                if (process is null)
                {
                    process = Start(out var inJob);
                    assigned &= inJob;
                    if (process is null) { results[i] = JobResult.Fail(request.Id, ErrorCode.WorkerCrashed, "The worker could not be started."); done(); return assigned; }
                }
                var attempt = await RunOneAsync(process, request, cancellationToken).ConfigureAwait(false);
                if (attempt.Result is { } ok) { results[i] = ok; restarts = 0; done(); continue; }

                Kill(process);
                process.Dispose();
                process = null;
                DeleteTemp(request);
                results[i] = attempt.Error!;
                done();
                if (cancellationToken.IsCancellationRequested) return assigned;
                if (++restarts > MaxRestartsPerLane) return assigned; // the remaining files are reported as not run
            }
            return assigned;
        }
        finally
        {
            if (process is not null) await CloseAsync(process).ConfigureAwait(false);
        }
    }

    private Process? Start(out bool inJob)
    {
        inJob = false;
        var psi = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
        };
        if (environment is not null) foreach (var (k, v) in environment) psi.Environment[k] = v;
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        // Drained into a fixed buffer so the pipe can never fill and no line of any length is held; codec text is handled in the worker.
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null)
            .ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // a killed worker breaks the pipe: expected
        inJob = WorkerJobObject.TryAssign(process); // if the host dies, Windows kills the worker with it
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        return process;
    }

    private static async Task CloseAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(exit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) { Kill(process); }
        finally { process.Dispose(); }
    }

    private readonly record struct Attempt(JobResult? Result, JobResult? Error);

    private async Task<Attempt> RunOneAsync(Process process, JobRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(jobTimeout);
        string? line;
        try
        {
            await process.StandardInput.WriteLineAsync(WorkerProtocol.Serialize(WorkerMessage.ForRequest(request)).AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            line = await ReadResultLineAsync(process.StandardOutput, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, JobResult.Fail(request.Id, ErrorCode.WorkerTimeout, $"The job did not finish within {jobTimeout.TotalSeconds:0} s."));
        }
        catch (OperationCanceledException)
        {
            // Ask the worker to stop; one that answers in time keeps its process, one that does not is killed by the lane.
            if (await CancelAsync(process, request).ConfigureAwait(false) is { } answered) return Judge(request, answered);
            return new(null, JobResult.Fail(request.Id, ErrorCode.Cancelled, "Cancelled."));
        }
        catch (IOException)
        {
            line = null; // pipe broken: the worker died
        }
        return Judge(request, line, process);
    }

    private Attempt Judge(JobRequest request, string? line, Process? process = null)
    {
        if (ReferenceEquals(line, TooLong)) return new(null, JobResult.Fail(request.Id, ErrorCode.WorkerProtocolError, "The worker sent an oversized answer."));
        if (line is null)
        {
            var code = process is not null && process.WaitForExit(2000) ? process.ExitCode.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : "?";
            return new(null, JobResult.Fail(request.Id, ErrorCode.WorkerCrashed, $"The worker stopped unexpectedly (exit 0x{code})."));
        }
        var result = WorkerProtocol.Deserialize<WorkerMessage>(line)?.Result;
        if (result is null || result.Id != request.Id || result.Outputs is null || result.Facts is null || !module.IsBelievable(request, result))
            return new(null, JobResult.Fail(request.Id, ErrorCode.WorkerProtocolError, "The worker sent an invalid answer."));
        return new(result, null);
    }

    /// <summary>Sends a cancel message and waits <see cref="CancelGrace"/> for the request's answer; null if none came.</summary>
    private static async Task<string?> CancelAsync(Process process, JobRequest request)
    {
        using var grace = new CancellationTokenSource(CancelGrace);
        try
        {
            await process.StandardInput.WriteLineAsync(WorkerProtocol.Serialize(WorkerMessage.Cancel(request.Id)).AsMemory(), grace.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(grace.Token).ConfigureAwait(false);
            return await ReadResultLineAsync(process.StandardOutput, grace.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { return null; }
    }

    /// <summary>The next line that is a result, skipping lines of other types (progress); the raw line, for judging.</summary>
    internal static async Task<string?> ReadResultLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
            if (line is null || ReferenceEquals(line, TooLong)) return line;
            if (WorkerProtocol.Deserialize<WorkerMessage>(line) is { Type: not WorkerMessage.ResultType }) continue;
            return line;
        }
    }

    /// <summary>Returned (compared by reference) for a line longer than the protocol allows.</summary>
    private static readonly string TooLong = new('?', 1);

    /// <summary>
    /// One line of at most <see cref="WorkerProtocol.MaxLineLength"/> characters; <see cref="TooLong"/> as soon as a line
    /// grows past that (the rest is never buffered), null at the end of the stream.
    /// </summary>
    internal static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false) == 1)
        {
            if (one[0] == '\n') return line.Length > 0 && line[^1] == '\r' ? line.ToString(0, line.Length - 1) : line.ToString();
            if (line.Length >= WorkerProtocol.MaxLineLength) return TooLong;
            line.Append(one[0]);
        }
        return line.Length > 0 ? line.ToString() : null;
    }

    private void DeleteTemp(JobRequest request)
    {
        foreach (var temp in module.TempPathsOf(request))
        {
            // A module's staging folder (Split's pages) has the temp file's deterministic name; it is never a result.
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); else File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
