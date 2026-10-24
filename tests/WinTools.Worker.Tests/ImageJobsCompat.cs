using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker;

namespace WinTools.Worker.Tests;

/// <summary>
/// The image tests were written against the image module's own request and result (one file in, one file or an
/// <see cref="ImageInfo"/> out). V2 PR 1.2 puts the shared job contract on the pipe; these helpers let those tests keep
/// their assertions and go through the real v2 path: the request is sent as a <see cref="JobRequest"/>, and the answer
/// is read back into the module's shape (facts are compared through <see cref="ImageJobs.From"/>).
/// </summary>
internal static class ImageJobsCompat
{
    public static WorkerClient Images(string workerExe, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null, int parallelism = 0) =>
        new(workerExe, timeout, ImageJobModule.Instance, environment, parallelism);

    public static JobRequest ToJob(this ConversionRequest r)
    {
        var parameters = new Dictionary<string, string>();
        if (r.JpegQuality is { } j) parameters[ImageJobs.JpegQuality] = j.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (r.WebPQuality is { } w) parameters[ImageJobs.WebPQuality] = w.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new JobRequest(r.Id, r.OperationId, [r.SourcePath], r.OutputDirectory, parameters.Count == 0 ? null : parameters);
    }

    /// <summary>A shared result in the module's shape: the single output and its hash; facts are not mapped back.</summary>
    public static ConversionResult ToConversion(this JobResult r) =>
        new(r.Id, r.Success, r.Error, r.Message, r.OutputPath, r.Outputs.Count == 1 ? r.Outputs[0].Sha256 : null);

    public static async Task<IReadOnlyList<ConversionResult>> RunAsync(this WorkerClient client, IReadOnlyList<ConversionRequest> requests,
        CancellationToken cancellationToken, Action<int>? onFileDone = null) =>
        [.. (await client.RunAsync([.. requests.Select(ToJob)], cancellationToken, onFileDone).ConfigureAwait(false)).Select(ToConversion)];

    public static bool IsBelievable(ConversionRequest request, ConversionResult result) =>
        ImageJobModule.Instance.IsBelievable(request.ToJob(), ImageJobs.From(result));
}
