using System.Security.Cryptography;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Host.Jobs;

/// <summary>
/// The Media module's host side (ADR-0028). A success claim is believed only from the disk:
/// <list type="bullet">
/// <item>Video info and Audio info: facts within <see cref="JobFacts"/>' bounds, and no file.</item>
/// <item>A new file: one, under a name the module allows for that command, whose SHA-256 is the one the worker validated
/// (any facts are notices the toast shows).</item>
/// <item>Save frames and Split: one folder under an allowed name, holding no link, whose shape (paths and sizes) is the one the
/// worker reported.</item>
/// </list>
/// A killed worker may leave its temporary file or folder, under one deterministic name.
/// </summary>
internal sealed class MediaJobModule : IJobModule
{
    public static MediaJobModule Instance { get; } = new();

    public string Name => MediaCapabilities.Module;

    /// <summary>No settings reach the module; Trim and Split carry only their form's answer.</summary>
    public static IReadOnlyDictionary<string, string>? Parameters(Core.Settings.AppSettings _) => null;

    public bool IsBelievable(JobRequest request, JobResult result)
    {
        if (result.Outputs is null || result.Facts is null) return false;
        if (!result.Success) return result.Outputs.Count == 0 && result.Facts.Count == 0 && result.Error != ErrorCode.None;
        if (request.Inputs.Count == 0 || result.Facts.Count > 0 && !JobFacts.AreShowable(result.Facts)) return false;
        try
        {
            switch (request.CapabilityId)
            {
                case MediaCapabilities.VideoInfoId or MediaCapabilities.AudioInfoId:
                    return request.Inputs.Count == 1 && result.Outputs.Count == 0 && result.Facts.Any(f => f.Label == "Duration");
                case MediaCapabilities.FramesId or MediaCapabilities.SplitVideoId or MediaCapabilities.SplitAudioId:
                {
                    if (result.Outputs is not [{ Sha256: { Length: 64 } shape } output] || request.Inputs.Count != 1) return false;
                    if (!MediaCapabilities.Candidates(request.CapabilityId, request.Inputs[0], request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
                    var folder = new DirectoryInfo(output.Path);
                    return folder.Exists && folder.LinkTarget is null
                        && string.Equals(FolderManifest.ShapeOf(output.Path, MediaJobsLimits.MaxFrames + 1), shape, StringComparison.OrdinalIgnoreCase);
                }
                default:
                {
                    if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } file]) return false;
                    if (result.Facts.Any(f => !f.Notice)) return false;
                    var first = request.CapabilityId == MediaCapabilities.JoinId ? PdfCapabilities.MergeOrder(request.Inputs)[0] : request.Inputs[0];
                    if (request.CapabilityId != MediaCapabilities.JoinId && request.Inputs.Count != 1) return false;
                    if (!MediaCapabilities.Candidates(request.CapabilityId, first, request.OutputDirectory).Contains(file.Path, StringComparer.OrdinalIgnoreCase)) return false;
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return stream.Length > 0 && string.Equals(Convert.ToHexString(SHA256.HashData(stream)), claimed, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public IEnumerable<string> TempPathsOf(JobRequest request)
    {
        if (request.Inputs.Count == 0) yield break;
        string temp;
        try
        {
            var first = request.CapabilityId == MediaCapabilities.JoinId ? PdfCapabilities.MergeOrder(request.Inputs)[0] : request.Inputs[0];
            temp = OutputNaming.TempPath(first, request.Id, request.OutputDirectory);
        }
        catch (ArgumentException) { yield break; }
        yield return temp;
    }
}

/// <summary>The bound the host walks a Save frames folder to (the worker writes at most this many frames).</summary>
internal static class MediaJobsLimits
{
    public const int MaxFrames = 300;
}
