using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Host.Jobs;

/// <summary>
/// What the host knows about one module's jobs (ADR-0018): which worker runs them, how to tell a believable success from
/// a claim, and which temporary files a killed worker may leave. Module knowledge stays in the module; the scheduler
/// and the worker client are the same for every module.
/// </summary>
internal interface IJobModule
{
    string Name { get; }

    /// <summary>Whether a worker's success claim holds up: what it produced is where and what the module allows.</summary>
    bool IsBelievable(JobRequest request, JobResult result);

    /// <summary>Temporary files the worker may have left for this request when it was killed.</summary>
    IEnumerable<string> TempPathsOf(JobRequest request);
}

/// <summary>The image module's host side: one input, at most one output, named by the naming policy.</summary>
internal sealed class ImageJobModule : IJobModule
{
    public static ImageJobModule Instance { get; } = new();

    public string Name => ImageCapabilities.Module;

    /// <summary>The settings' encoder qualities, as the image worker's job parameters.</summary>
    public static IReadOnlyDictionary<string, string> Parameters(Core.Settings.AppSettings s) => new Dictionary<string, string>
    {
        ["jpegQuality"] = s.JpegQuality.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["webpQuality"] = s.WebPQuality.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// A success claim is believed only if:
    /// <list type="bullet">
    /// <item>"Image info" reports facts and no file;</item>
    /// <item>otherwise there is exactly one output, and its SHA-256 equals the hash of the output the worker validated,
    /// so a pre-existing or replaced file is not believed;</item>
    /// <item>its real format is one this operation can produce (the conversion target, or the source's own format for
    /// operations that keep it);</item>
    /// <item>its name is one the naming policy allows for this source, operation and format.</item>
    /// </list>
    /// </summary>
    public bool IsBelievable(JobRequest request, JobResult result)
    {
        if (!result.Success) return result.Outputs.Count == 0 && result.Error != ErrorCode.None;
        if (OperationCatalog.Find(request.CapabilityId) is not { } op || request.Inputs.Count != 1) return false;
        if (!op.WritesFile) return result.Outputs.Count == 0 && result.Facts.Count > 0; // "Image info": facts, no file
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output] || result.Facts.Count > 0) return false;
        var source = request.Inputs[0];
        try
        {
            // The name first: only a path the naming policy allows is ever opened, whatever the worker claims.
            var expected = op.OutputFormat(SourceFormat(source));
            if (expected == ImageFormat.Unknown || !OutputNaming.Candidates(source, expected, op.NameSuffix, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
            using var file = new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[FileSignature.HeaderLength];
            var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (FileSignature.Detect(header.AsSpan(0, read)) != expected) return false;
            file.Position = 0;
            return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), claimed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; } // incl. empty or NUL-containing paths
    }

    public IEnumerable<string> TempPathsOf(JobRequest request)
    {
        if (request.Inputs.Count != 1) yield break;
        string temp;
        try { temp = OutputNaming.TempPath(request.Inputs[0], request.Id, request.OutputDirectory); }
        catch (ArgumentException) { yield break; }
        yield return temp;
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
}
