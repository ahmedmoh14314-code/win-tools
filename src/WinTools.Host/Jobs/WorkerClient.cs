using System.Diagnostics;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;
using WinTools.Windows;

namespace WinTools.Host.Jobs;

/// <summary>
/// Runs a batch of conversions in a fresh worker process and maps every way the worker can fail to a structured
/// result. The worker is disposable: on a crash, a hang past the per-job timeout or a protocol violation the process is
/// killed, the request's known temporary file is deleted, and the rest of the batch is reported as not run. The host
/// also re-checks every success claim (the file exists and has a name the naming policy allows for that source).
/// </summary>
internal sealed class WorkerClient(string workerPath, TimeSpan jobTimeout, IReadOnlyDictionary<string, string>? environment = null)
{
    /// <summary>Whether the last worker was placed in the host's kill-on-close job object (logged by the caller).</summary>
    public bool JobObjectAssigned { get; private set; }

    public async Task<IReadOnlyList<ConversionResult>> RunAsync(IReadOnlyList<ConversionRequest> requests, CancellationToken cancellationToken)
    {
        var results = new List<ConversionResult>(requests.Count);
        if (requests.Count == 0) return results;
        if (requests.Any(r => !PathPolicy.IsSupported(r.SourcePath))) // also checked by the resolver; the pipe must never carry one
            return requests.Select(r => ConversionResult.Fail(r.Id, ErrorCode.InvalidRequest, "Unsupported source path.")).ToArray();

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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return requests.Select(r => ConversionResult.Fail(r.Id, ErrorCode.WorkerCrashed, "The conversion worker could not be started.")).ToArray();
        }

        using (process)
        {
            process.ErrorDataReceived += static (_, _) => { }; // drained so the pipe can never fill; codec text is handled in the worker
            process.BeginErrorReadLine();
            JobObjectAssigned = WorkerJobObject.TryAssign(process); // if the host dies, Windows kills the worker with it
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }

            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                var failure = await RunOneAsync(process, request, cancellationToken).ConfigureAwait(false);
                if (failure.Result is { } ok) { results.Add(ok); continue; }

                Kill(process);
                DeleteTemp(request);
                results.Add(failure.Error!);
                for (var j = i + 1; j < requests.Count; j++)
                    results.Add(ConversionResult.Fail(requests[j].Id, ErrorCode.WorkerCrashed, "Not run: the worker stopped earlier in this batch."));
                break;
            }

            try
            {
                process.StandardInput.Close();
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(exit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) { Kill(process); }
        }
        return results;
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
            line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, ConversionResult.Fail(request.Id, ErrorCode.WorkerTimeout, $"The conversion did not finish within {jobTimeout.TotalSeconds:0} s."));
        }
        catch (OperationCanceledException)
        {
            return new(null, ConversionResult.Fail(request.Id, ErrorCode.WorkerCrashed, "Cancelled."));
        }
        catch (IOException)
        {
            line = null; // pipe broken: the worker died
        }

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
        if (result.OutputPath is null || result.OutputSha256 is not { Length: 64 } claimed || OperationCatalog.Find(request.OperationId) is not { } op) return false;
        try
        {
            using var file = new FileStream(result.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[FileSignature.HeaderLength];
            var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var format = FileSignature.Detect(header.AsSpan(0, read));
            var producible = op.Kind == OperationKind.Convert ? format == op.Target : op.AppliesTo(format);
            if (!producible || !OutputNaming.Candidates(request.SourcePath, format, op.NameSuffix).Contains(result.OutputPath, StringComparer.OrdinalIgnoreCase)) return false;
            file.Position = 0;
            return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), claimed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void DeleteTemp(ConversionRequest request)
    {
        try { File.Delete(OutputNaming.TempPath(request.SourcePath, request.Id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
