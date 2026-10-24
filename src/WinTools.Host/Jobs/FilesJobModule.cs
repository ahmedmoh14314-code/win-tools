using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;

namespace WinTools.Host.Jobs;

/// <summary>
/// The General Files module's host side (V2 PR 1.4). Its worker jobs (File info, SHA-256) write nothing, so a success
/// is believed only as facts within <see cref="JobFacts"/>' bounds and no output; SHA-256 must carry a well-formed hash.
/// There are no temporary files to clean.
/// </summary>
internal sealed class FilesJobModule : IJobModule
{
    public static FilesJobModule Instance { get; } = new();

    public string Name => FilesCapabilities.Module;

    public bool IsBelievable(JobRequest request, JobResult result)
    {
        if (result.Outputs is null || result.Facts is null) return false;
        if (!result.Success) return result.Outputs.Count == 0 && result.Facts.Count == 0 && result.Error != ErrorCode.None;
        if (request.Inputs.Count != 1 || result.Outputs.Count != 0 || !JobFacts.AreShowable(result.Facts)) return false;
        return request.CapabilityId switch
        {
            FilesCapabilities.InfoId => result.Facts.Any(f => f.Label == "Type"),
            FilesCapabilities.Sha256Id => result.Facts.Any(f => f.Label == "SHA-256" && f.Value.Length == 64 && f.Value.All(char.IsAsciiHexDigitLower)),
            _ => false,
        };
    }

    public IEnumerable<string> TempPathsOf(JobRequest request) => [];

    /// <summary>The module's commands take no parameters.</summary>
    public static IReadOnlyDictionary<string, string>? Parameters(Core.Settings.AppSettings _) => null;
}

/// <summary>
/// Runs the General Files module's requests: Copy path in the host (it parses nothing, so it needs no worker), the rest
/// in the text worker. A job holds requests of one command, so each call goes one way.
/// </summary>
internal sealed class FilesExecutor(IJobExecutor info, IJobExecutor hash, Func<string, Task<bool>> copyText) : IJobExecutor
{
    private IJobExecutor? _last; // what ran the last job, for the log; null after Copy path, which starts no worker

    public bool JobObjectAssigned => _last?.JobObjectAssigned ?? true;
    public int LastLaneCount => _last?.LastLaneCount ?? 0;

    public async Task<IReadOnlyList<JobResult>> RunAsync(IReadOnlyList<JobRequest> requests, CancellationToken cancellationToken, Action<int>? onFileDone = null)
    {
        if (requests.Count == 0 || requests[0].CapabilityId != FilesCapabilities.CopyPathId)
        {
            // File info is quick and gets a short limit; hashing reads whole files, one at a time, with a long one.
            _last = requests.Count > 0 && requests[0].CapabilityId == FilesCapabilities.Sha256Id ? hash : info;
            return await _last.RunAsync(requests, cancellationToken, onFileDone).ConfigureAwait(false);
        }
        _last = null;
        var results = new List<JobResult>(requests.Count);
        foreach (var r in requests)
        {
            if (cancellationToken.IsCancellationRequested) { results.Add(JobResult.Fail(r.Id, ErrorCode.Cancelled, "Cancelled before it ran.")); continue; }
            var copied = await copyText(FilesCapabilities.ClipboardText(r.Inputs)).ConfigureAwait(false);
            results.Add(copied ? new JobResult(r.Id, true, ErrorCode.None, null, [], []) : JobResult.Fail(r.Id, ErrorCode.ClipboardUnavailable, "The clipboard was busy."));
            onFileDone?.Invoke(results.Count);
        }
        return results;
    }
}
