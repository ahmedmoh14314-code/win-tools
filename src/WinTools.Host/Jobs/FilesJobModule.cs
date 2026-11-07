using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Host.Jobs;

/// <summary>
/// The General Files module's host side (V2 PR 1.4). Its worker jobs (File info, SHA-256) write nothing, so a success
/// is believed only as facts within <see cref="JobFacts"/>' bounds and no output; SHA-256 must carry a well-formed hash.
/// There are no temporary files to clean. Copy to and Move to are believed from the disk (ADR-0032); Show in folder and
/// Open in Terminal leave nothing to check.
/// </summary>
internal sealed class FilesJobModule : IJobModule
{
    public static FilesJobModule Instance { get; } = new();

    public string Name => FilesCapabilities.Module;

    public bool IsBelievable(JobRequest request, JobResult result)
    {
        if (result.Outputs is null || result.Facts is null) return false;
        if (!result.Success) return result.Outputs.Count == 0 && result.Facts.Count == 0 && result.Error != ErrorCode.None;
        if (request.CapabilityId == FilesCapabilities.RenameId) return JobFacts.AreShowable(result.Facts) && BatchRenamer.HoldsUp(request, result);
        if (FilesCapabilities.IsTransfer(request.CapabilityId)) return JobFacts.AreShowable(result.Facts) && TransferHoldsUp(request, result);
        if (FilesCapabilities.IsWindow(request.CapabilityId)) return result.Outputs.Count == 0 && result.Facts.Count == 0;
        if (request.Inputs.Count != 1 || result.Outputs.Count != 0 || !JobFacts.AreShowable(result.Facts)) return false;
        return request.CapabilityId switch
        {
            FilesCapabilities.InfoId => result.Facts.Any(f => f.Label == "Type"),
            FilesCapabilities.Sha256Id => result.Facts.Any(f => f.Label == "SHA-256" && f.Value.Length == 64 && f.Value.All(char.IsAsciiHexDigitLower)),
            FilesCapabilities.RenameId => BatchRenamer.HoldsUp(request, result),
            _ => false,
        };
    }

    /// <summary>
    /// A copy or move the host reports is believed only from the disk: the new item is in the picked folder under one of
    /// the names its item may take, of the same kind (file or folder); a moved original is gone, a copied one is still there.
    /// </summary>
    private static bool TransferHoldsUp(JobRequest request, JobResult result)
    {
        if (request.Inputs.Count != 1 || result.Outputs is not [{ } output] || TransferPlan.Destination(request.Parameter(DialogParameters.Name)) is not { } destination) return false;
        var item = request.Inputs[0];
        var folder = TransferPlan.IsFolder(item);
        if (!TransferPlan.Candidates(item, destination).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
        if (folder ? !Directory.Exists(output.Path) : !File.Exists(output.Path)) return false;
        var source = TransferPlan.Plain(item);
        var stillThere = folder ? Directory.Exists(source) : File.Exists(source);
        return request.CapabilityId == FilesCapabilities.MoveToId ? !stillThere : stillThere;
    }

    public IEnumerable<string> TempPathsOf(JobRequest request) => [];

    /// <summary>The module's commands take no parameters from settings (Batch rename's rule comes from its dialog).</summary>
    public static IReadOnlyDictionary<string, string>? Parameters(Core.Settings.AppSettings _) => null;
}

/// <summary>
/// Runs the General Files module's requests: the copy commands, Batch rename, Copy to and Move to, Show in folder and
/// Open in Terminal in the host (they parse nothing, so they need no worker), the rest in the text worker. A job holds
/// requests of one command, so each call goes one way.
/// </summary>
internal sealed class FilesExecutor(IJobExecutor info, IJobExecutor hash, Func<string, Task<bool>> copyText, IWindowOpener? windows = null,
    TransferHooks? transferHooks = null) : IJobExecutor
{
    // What ran the last worker job, for the log. Copy path never changes it: it runs beside the queue, and would
    // otherwise rewrite a running job's log values (evaluator, PR #18).
    private IJobExecutor? _last;

    public bool JobObjectAssigned => _last?.JobObjectAssigned ?? true;
    public int LastLaneCount => _last?.LastLaneCount ?? 0;

    public async Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken, Action<int>? onFileDone = null)
    {
        if (requests.Count > 0 && requests[0].CapabilityId == FilesCapabilities.RenameId)
        {
            var renamed = new List<JobResult>(requests.Count);
            foreach (var r in requests)
            {
                var result = cancellationToken.IsCancellationRequested ? JobResult.Fail(r.Id, ErrorCode.Cancelled, "Cancelled before it ran.") : BatchRenamer.Apply(r);
                // Believed from the disk like a worker's claim; an unbelievable success is a failure, never a toast that lies.
                renamed.Add(result.Success && !FilesJobModule.Instance.IsBelievable(r, result) ? JobResult.Fail(r.Id, ErrorCode.OutputValidationFailed, "The renamed files did not check out.") : result);
                onFileDone?.Invoke(renamed.Count);
            }
            return renamed;
        }
        if (requests.Count > 0 && FilesCapabilities.IsTransfer(requests[0].CapabilityId))
        {
            // Copy to and Move to: one item per request, one after another (one disk at a time); a failure is that item's.
            var moved = new List<JobResult>(requests.Count);
            foreach (var r in requests)
            {
                var result = cancellationToken.IsCancellationRequested ? JobResult.Fail(r.Id, ErrorCode.Cancelled, "Cancelled before it ran.")
                    : await FileTransfer.RunAsync(r, cancellationToken, transferHooks).ConfigureAwait(false);
                moved.Add(result.Success && !FilesJobModule.Instance.IsBelievable(r, result) ? JobResult.Fail(r.Id, ErrorCode.OutputValidationFailed, "The copied or moved item did not check out.") : result);
                onFileDone?.Invoke(moved.Count);
            }
            return moved;
        }
        if (requests.Count > 0 && FilesCapabilities.IsWindow(requests[0].CapabilityId))
        {
            // Show in folder and Open in Terminal run beside the queue (like Copy path): they open a window and leave nothing.
            var opened = new List<JobResult>(requests.Count);
            foreach (var r in requests)
            {
                opened.Add(cancellationToken.IsCancellationRequested ? JobResult.Fail(r.Id, ErrorCode.Cancelled, "Cancelled before it ran.")
                    : await Task.Run(() => FileWindows.Run(r, windows ?? WindowOpener.Instance), CancellationToken.None).ConfigureAwait(false));
                onFileDone?.Invoke(opened.Count);
            }
            return opened;
        }
        if (requests.Count == 0 || !FilesCapabilities.IsCopy(requests[0].CapabilityId))
        {
            // File info is quick and gets a short limit; hashing reads whole files, one at a time, with a long one.
            _last = requests.Count > 0 && requests[0].CapabilityId == FilesCapabilities.Sha256Id ? hash : info;
            return await _last.RunAsync(requests, cancellationToken, onFileDone).ConfigureAwait(false);
        }

        var results = new List<JobResult>(requests.Count);
        foreach (var r in requests)
        {
            if (cancellationToken.IsCancellationRequested) { results.Add(JobResult.Fail(r.Id, ErrorCode.Cancelled, "Cancelled before it ran.")); continue; }
            var copied = await copyText(FilesCapabilities.ClipboardText(r.CapabilityId, r.Inputs)).ConfigureAwait(false);
            results.Add(copied ? new JobResult(r.Id, true, ErrorCode.None, null, [], []) : JobResult.Fail(r.Id, ErrorCode.ClipboardUnavailable, "The clipboard was busy."));
            onFileDone?.Invoke(results.Count);
        }
        return results;
    }
}
