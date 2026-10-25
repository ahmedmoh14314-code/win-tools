using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Subtitles;

namespace WinTools.Host.Jobs;

/// <summary>
/// The Subtitles module's host side (V2 PR 7.1, ADR-0024): Info is believed as bounded facts; a written file only under a
/// name the module allows, when it parses as subtitles without a problem and its SHA-256 is the one the worker validated.
/// </summary>
internal sealed class SubtitleJobModule : IJobModule
{
    public static SubtitleJobModule Instance { get; } = new();

    public string Name => SubtitleCapabilities.Module;

    public static IReadOnlyDictionary<string, string>? Parameters(Core.Settings.AppSettings _) => null;

    public bool IsBelievable(JobRequest request, JobResult result)
    {
        if (result.Outputs is null || result.Facts is null) return false;
        if (!result.Success) return result.Outputs.Count == 0 && result.Facts.Count == 0 && result.Error != ErrorCode.None;
        if (request.Inputs is not [var source]) return false;
        if (request.CapabilityId == SubtitleCapabilities.InfoId) return result.Outputs.Count == 0 && JobFacts.AreShowable(result.Facts);
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output] || result.Facts.Count > 0) return false;
        try
        {
            if (!SubtitleCapabilities.Candidates(request.CapabilityId, source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(output.Path);
            if (!info.Exists || info.Length > SubtitleCapabilities.MaxInputBytes * 2) return false;
            var bytes = File.ReadAllBytes(output.Path);
            if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), claimed, StringComparison.OrdinalIgnoreCase)) return false;
            var doc = SubtitleParser.Parse(SubtitleEncoding.Decode(bytes).Text, SubtitleCapabilities.OutputFormat(request.CapabilityId, source));
            return doc.Problems.Count == 0 && doc.Cues.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public IEnumerable<string> TempPathsOf(JobRequest request)
    {
        if (request.Inputs.Count != 1) yield break;
        string temp;
        try { temp = Core.Output.OutputNaming.TempPath(request.Inputs[0], request.Id, request.OutputDirectory); }
        catch (ArgumentException) { yield break; }
        yield return temp;
    }
}
