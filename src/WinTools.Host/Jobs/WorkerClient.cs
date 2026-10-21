using System.Diagnostics;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;
using WinTools.Windows;

namespace WinTools.Host.Jobs;

/// <summary>
/// Runs a batch in a bounded pool of disposable worker processes and maps every way a worker can fail to a structured
/// result per file.
/// <list type="bullet">
/// <item>Lanes (at most <see cref="DefaultParallelism"/>, never more than the number of files) take files from one queue,
/// each with its own worker. Each worker may commit up to <see cref="WorkerJobObject.WorkerMemoryLimitBytes"/>, so there
/// are never more lanes than can all reach that cap within the commit the system has left when the batch starts, less a
/// reserve for everything else (ADR-0015).</item>
/// <item>A crash, a hang past the per-job timeout or a protocol violation fails only the file being processed: its worker
/// is killed, its known temporary file is deleted, and the lane starts a new worker for the next file. A lane gives up
/// after its first worker and <see cref="MaxRestartsPerLane"/> restarts have all failed in a row (a success resets the count);
/// what is left is reported as not run.</item>
/// <item>Cancellation stops taking new files; the unfinished ones are reported as Cancelled.</item>
/// <item>The host re-checks every success claim (allowed name, real format, SHA-256).</item>
/// </list>
/// </summary>
internal sealed class WorkerClient(string workerPath, TimeSpan jobTimeout, IReadOnlyDictionary<string, string>? environment = null, int parallelism = 0)
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

    /// <summary>Whether every worker of the last batch was placed in the host's kill-on-close job object (logged by the caller).</summary>
    public bool JobObjectAssigned { get; private set; }

    /// <summary>How many workers the last batch used (logged by the caller).</summary>
    public int LastLaneCount { get; private set; }

    public async Task<IReadOnlyList<ConversionResult>> RunAsync(IReadOnlyList<ConversionRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return [];
        if (requests.Any(r => !PathPolicy.IsSupported(r.SourcePath))) // also checked by the resolver; the pipe must never carry one
            return requests.Select(r => ConversionResult.Fail(r.Id, ErrorCode.InvalidRequest, "Unsupported source path.")).ToArray();

        var results = new ConversionResult?[requests.Count];
        var queue = new System.Collections.Concurrent.ConcurrentQueue<int>(Enumerable.Range(0, requests.Count));
        var lanes = Math.Clamp(parallelism > 0 ? parallelism : DefaultParallelism, 1, requests.Count);
        LastLaneCount = lanes;
        var allAssigned = true;
        await Task.WhenAll(Enumerable.Range(0, lanes).Select(_ => Task.Run(async () =>
        {
            if (!await RunLaneAsync(queue, requests, results, cancellationToken).ConfigureAwait(false)) allAssigned = false;
        }, CancellationToken.None))).ConfigureAwait(false);
        JobObjectAssigned = allAssigned;
        for (var i = 0; i < results.Length; i++)
            results[i] ??= cancellationToken.IsCancellationRequested
                ? ConversionResult.Fail(requests[i].Id, ErrorCode.Cancelled, "Cancelled before it ran.")
                : ConversionResult.Fail(requests[i].Id, ErrorCode.WorkerCrashed, "Not run: the workers kept failing.");
        return results!;
    }

    /// <summary>One lane: a worker that processes files from the queue until it is empty. Returns whether every worker it started joined the job object.</summary>
    private async Task<bool> RunLaneAsync(System.Collections.Concurrent.ConcurrentQueue<int> queue, IReadOnlyList<ConversionRequest> requests,
        ConversionResult?[] results, CancellationToken cancellationToken)
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
                    if (process is null) { results[i] = ConversionResult.Fail(request.Id, ErrorCode.WorkerCrashed, "The conversion worker could not be started."); return assigned; }
                }
                var attempt = await RunOneAsync(process, request, cancellationToken).ConfigureAwait(false);
                if (attempt.Result is { } ok) { results[i] = ok; restarts = 0; continue; }

                Kill(process);
                process.Dispose();
                process = null;
                DeleteTemp(request);
                results[i] = attempt.Error!;
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

    private readonly record struct Attempt(ConversionResult? Result, ConversionResult? Error);

    private async Task<Attempt> RunOneAsync(Process process, ConversionRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(jobTimeout);
        string? line;
        try
        {
            await process.StandardInput.WriteLineAsync(WorkerProtocol.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            line = await ReadLineAsync(process.StandardOutput, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, ConversionResult.Fail(request.Id, ErrorCode.WorkerTimeout, $"The conversion did not finish within {jobTimeout.TotalSeconds:0} s."));
        }
        catch (OperationCanceledException)
        {
            return new(null, ConversionResult.Fail(request.Id, ErrorCode.Cancelled, "Cancelled."));
        }
        catch (IOException)
        {
            line = null; // pipe broken: the worker died
        }

        if (ReferenceEquals(line, TooLong)) return new(null, ConversionResult.Fail(request.Id, ErrorCode.WorkerProtocolError, "The conversion worker sent an oversized answer."));
        if (line is null)
        {
            var code = process.WaitForExit(2000) ? process.ExitCode.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : "?";
            return new(null, ConversionResult.Fail(request.Id, ErrorCode.WorkerCrashed, $"The conversion worker stopped unexpectedly (exit 0x{code})."));
        }

        var result = WorkerProtocol.Deserialize<ConversionResult>(line);
        if (result is null || result.Id != request.Id || !IsBelievable(request, result))
            return new(null, ConversionResult.Fail(request.Id, ErrorCode.WorkerProtocolError, "The conversion worker sent an invalid answer."));
        return new(result, null);
    }

    /// <summary>
    /// A success claim is believed only if:
    /// <list type="bullet">
    /// <item>the file's SHA-256 equals the hash of the output the worker validated, so a pre-existing or replaced file is
    /// not believed;</item>
    /// <item>its real format is one this operation can produce (the conversion target, or the source's own format for
    /// operations that keep it);</item>
    /// <item>its name is one the naming policy allows for this source, operation and format.</item>
    /// </list>
    /// </summary>
    internal static bool IsBelievable(ConversionRequest request, ConversionResult result)
    {
        if (!result.Success) return result.OutputPath is null && result.Error != ErrorCode.None;
        if (OperationCatalog.Find(request.OperationId) is not { } op) return false;
        if (!op.WritesFile) return result.OutputPath is null && result.Info is not null; // "Image info": facts, no file
        if (result.OutputPath is null || result.OutputSha256 is not { Length: 64 } claimed) return false;
        try
        {
            // The name first: only a path the naming policy allows is ever opened, whatever the worker claims.
            var expected = op.OutputFormat(SourceFormat(request.SourcePath));
            if (expected == ImageFormat.Unknown || !OutputNaming.Candidates(request.SourcePath, expected, op.NameSuffix, request.OutputDirectory).Contains(result.OutputPath, StringComparer.OrdinalIgnoreCase)) return false;
            using var file = new FileStream(result.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[FileSignature.HeaderLength];
            var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (FileSignature.Detect(header.AsSpan(0, read)) != expected) return false;
            file.Position = 0;
            return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), claimed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; } // incl. empty or NUL-containing paths
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

    /// <summary>The source's real format from its header (the hint from its name if it can no longer be read).</summary>
    private static ImageFormat SourceFormat(string path)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[FileSignature.HeaderLength];
            return FileSignature.Detect(header.AsSpan(0, source.ReadAtLeast(header, header.Length, throwOnEndOfStream: false)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return FormatHint.FromPath(path); }
    }

    private static void DeleteTemp(ConversionRequest request)
    {
        try { File.Delete(OutputNaming.TempPath(request.SourcePath, request.Id, request.OutputDirectory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
