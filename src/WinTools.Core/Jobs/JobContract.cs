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
