using System.Globalization;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Host.Jobs;

/// <summary>
/// The PDF module's host side (V2 PR 2.1, ADR-0022). A success claim is believed only when what the worker reports is
/// what the module allows, re-checked from the disk:
/// <list type="bullet">
/// <item>PDF info: facts within <see cref="JobFacts"/>' bounds that name the type, and no file.</item>
/// <item>Merge and Rotate: one file, under a name the naming policy allows for this source and command, that starts as a
/// PDF and whose SHA-256 is the one the worker validated.</item>
/// <item>Split: one folder, under an allowed name, holding exactly the page files the worker reported (the manifest hash
/// over every file name and hash), each named as Split names pages.</item>
/// </list>
/// A killed worker may leave its temporary file, or Split's staging folder, which has the same deterministic name.
/// </summary>
internal sealed class PdfJobModule : IJobModule
{
    public static PdfJobModule Instance { get; } = new();

    public string Name => PdfCapabilities.Module;

    /// <summary>The module's commands take no parameters yet.</summary>
    public static IReadOnlyDictionary<string, string>? Parameters(Core.Settings.AppSettings _) => null;

    public bool IsBelievable(JobRequest request, JobResult result)
    {
        if (result.Outputs is null || result.Facts is null) return false;
        if (!result.Success) return result.Outputs.Count == 0 && result.Facts.Count == 0 && result.Error != ErrorCode.None;
        if (request.Inputs.Count == 0) return false;
        try
        {
            return request.CapabilityId switch
            {
                PdfCapabilities.InfoId => request.Inputs.Count == 1 && result.Outputs.Count == 0 && JobFacts.AreShowable(result.Facts) && result.Facts.Any(f => f.Label == "Type"),
                PdfCapabilities.SplitId => request.Inputs.Count == 1 && SplitHoldsUp(request, result),
                PdfCapabilities.MergeId => request.Inputs.Count >= 2 && FileHoldsUp(request, result),
                _ when PdfCapabilities.RotationOf(request.CapabilityId) is not null => request.Inputs.Count == 1 && FileHoldsUp(request, result),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool FileHoldsUp(JobRequest request, JobResult result)
    {
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output] || result.Facts.Count > 0) return false;
        // The name first: only a path the naming policy allows is ever opened, whatever the worker claims.
        var allowed = OutputNaming.CandidatesWithExtension(request.Inputs[0], ".pdf", PdfCapabilities.NameSuffix(request.CapabilityId), request.OutputDirectory);
        if (!allowed.Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
        using var file = new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[5];
        if (file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) != head.Length || !head.AsSpan().SequenceEqual("%PDF-"u8)) return false;
        file.Position = 0;
        return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), claimed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SplitHoldsUp(JobRequest request, JobResult result)
    {
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output]) return false;
        if (result.Facts is not [{ Label: "Pages" } pagesFact]
            || !int.TryParse(pagesFact.Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var pages)
            || pages is < 1 or > PdfCapabilities.MaxSplitPages) return false;
        var source = request.Inputs[0];
        if (!OutputNaming.FolderCandidates(source, PdfCapabilities.NameSuffix(PdfCapabilities.SplitId)!, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
        var expected = Enumerable.Range(1, pages).Select(p => PdfCapabilities.PageFileName(source, p, pages)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = Directory.EnumerateFileSystemEntries(output.Path).Select(Path.GetFileName).ToArray();
        if (names.Length != pages || !names.All(n => n is not null && expected.Contains(n))) return false;
        return string.Equals(FolderManifest.HashOf(output.Path), claimed, StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<string> TempPathsOf(JobRequest request)
    {
        if (request.Inputs.Count == 0) yield break;
        string temp;
        try { temp = OutputNaming.TempPath(request.Inputs[0], request.Id, request.OutputDirectory); }
        catch (ArgumentException) { yield break; }
        yield return temp;
    }
}
