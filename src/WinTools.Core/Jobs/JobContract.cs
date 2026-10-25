using System.Text.Json.Serialization;

namespace WinTools.Core.Jobs;

/// <summary>
/// One job for a worker, in the same shape for every module (ADR-0018). A per-file capability gets one request per file;
/// a selection capability gets one request with every input. Parameters are strings the module validates; secrets never
/// travel here.
/// </summary>
public sealed record JobRequest(string Id, string CapabilityId, IReadOnlyList<string> Inputs, string? OutputDirectory = null,
    IReadOnlyDictionary<string, string>? Parameters = null)
{
    /// <summary>A parameter, or null when absent.</summary>
    public string? Parameter(string name) => Parameters is not null && Parameters.TryGetValue(name, out var v) ? v : null;
}

/// <summary>A file a job wrote, with the SHA-256 of the bytes the worker validated.</summary>
public sealed record JobOutput(string Path, string Sha256);

/// <summary>A fact a job reports ("Dimensions", "4032 × 3024 px"). <paramref name="Notice"/> marks one the user should notice.</summary>
public sealed record JobFact(string Label, string Value, bool Notice = false);

/// <summary>
/// The limits on facts a worker reports. Facts are worker text that reaches the user (the info panel), so the host
/// shows them only from a believed success, and a module believes them only within these bounds (evaluator, PR #16).
/// </summary>
public static class JobFacts
{
    public const int MaxCount = 32, MaxLabelLength = 40, MaxValueLength = 200;

    /// <summary>
    /// Whether facts can be shown as they are: at least one, at most <see cref="MaxCount"/>, each with a label and a
    /// value within bounds, and no control or bidirectional-formatting characters that could reorder or hide text.
    /// </summary>
    public static bool AreShowable(IReadOnlyList<JobFact>? facts) =>
        facts is { Count: > 0 and <= MaxCount } && facts.All(f => f is { Label.Length: > 0 and <= MaxLabelLength, Value: not null }
            && f.Value.Length <= MaxValueLength && Plain(f.Label) && Plain(f.Value));

    private static bool Plain(string text) =>
        !text.Any(c => char.IsControl(c) || c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\u200E' or '\u200F' or '\u061C');
}

/// <summary>A worker's answer to one <see cref="JobRequest"/>.</summary>
public sealed record JobResult(string Id, bool Success, ErrorCode Error, string? Message,
    IReadOnlyList<JobOutput> Outputs, IReadOnlyList<JobFact> Facts)
{
    public static JobResult Ok(string id, params JobOutput[] outputs) => new(id, true, ErrorCode.None, null, outputs, []);
    public static JobResult WithFacts(string id, IReadOnlyList<JobFact> facts) => new(id, true, ErrorCode.None, null, [], facts);
    public static JobResult Fail(string id, ErrorCode error, string message) => new(id, false, error, message, [], []);

    /// <summary>The single output of a per-file job, or null.</summary>
    [JsonIgnore]
    public string? OutputPath => Outputs.Count == 1 ? Outputs[0].Path : null;
}

/// <summary>
/// The envelope of one protocol line (ADR-0018): <see cref="Type"/> says which of the other fields is set. Either side
/// ignores a type it does not know, so a module can add one (progress) without breaking the other end.
/// </summary>
public sealed record WorkerMessage(string Type, JobRequest? Request = null, JobResult? Result = null, string? JobId = null)
{
    public const string RequestType = "request", ResultType = "result", CancelType = "cancel";

    public static WorkerMessage ForRequest(JobRequest request) => new(RequestType, Request: request);
    public static WorkerMessage ForResult(JobResult result) => new(ResultType, Result: result);
    public static WorkerMessage Cancel(string jobId) => new(CancelType, JobId: jobId);
}
