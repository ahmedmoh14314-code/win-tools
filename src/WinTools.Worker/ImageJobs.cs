using System.Globalization;
using WinTools.Core.Feedback;
using WinTools.Core.Jobs;

namespace WinTools.Worker;

/// <summary>
/// The image module's side of the job contract (ADR-0018): one <see cref="JobRequest"/> for one image becomes the
/// module's own <see cref="ConversionRequest"/>, and its result a <see cref="JobResult"/>. Image capabilities are per
/// file, so a request carries exactly one input. Parameters: "jpegQuality" and "webpQuality" (1–100); anything else is
/// refused rather than ignored, so a host and a worker that disagree fail loudly.
/// </summary>
internal static class ImageJobs
{
    public const string JpegQuality = "jpegQuality", WebPQuality = "webpQuality";

    public static JobResult Run(JobRequest request, JobHooks? hooks = null)
    {
        if (request.Inputs is not { Count: 1 } inputs)
            return JobResult.Fail(request.Id, ErrorCode.InvalidRequest, "An image job takes exactly one file.");
        if (request.Parameters?.Keys.FirstOrDefault(k => k is not (JpegQuality or WebPQuality)) is { } unknown)
            return JobResult.Fail(request.Id, ErrorCode.InvalidRequest, $"Unknown parameter \"{unknown}\".");
        if (!TryQuality(request, JpegQuality, out var jpeg) || !TryQuality(request, WebPQuality, out var webp))
            return JobResult.Fail(request.Id, ErrorCode.InvalidRequest, "A quality must be a whole number from 1 to 100.");
        var result = ConversionJob.Run(new ConversionRequest(request.Id, request.CapabilityId, inputs[0], request.OutputDirectory, jpeg, webp), hooks);
        return From(result);
    }

    /// <summary>The module's result in the shared shape. "Image info" becomes the info panel's rows, as facts.</summary>
    public static JobResult From(ConversionResult r)
    {
        if (!r.Success) return JobResult.Fail(r.Id, r.Error, r.Message ?? "");
        if (r.Info is { } info) return JobResult.WithFacts(r.Id, [.. InfoRows.For(info).Select(row => new JobFact(row.Label, row.Value, row.Notice))]);
        return JobResult.Ok(r.Id, new JobOutput(r.OutputPath!, r.OutputSha256!));
    }

    private static bool TryQuality(JobRequest request, string name, out int? quality)
    {
        quality = null;
        if (request.Parameter(name) is not { } text) return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var q) || q is < 1 or > 100) return false;
        quality = q;
        return true;
    }
}
