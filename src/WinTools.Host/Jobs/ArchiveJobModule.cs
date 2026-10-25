using System.Globalization;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Host.Jobs;

/// <summary>
/// The Archives module's host side (V2 PR 5.1, ADR-0023). A success claim is believed only from the disk:
/// <list type="bullet">
/// <item>Info and Test: facts within <see cref="JobFacts"/>' bounds, and no file.</item>
/// <item>Add to ZIP: one file under a name the module allows, that starts with the ZIP signature and whose SHA-256 is the
/// one the worker validated.</item>
/// <item>Extract: one folder under an allowed name, holding no link, whose shape (every relative path and file size) is
/// the one the worker reported. The content is not read again: an extraction may be gigabytes, and the worker checked
/// every file's CRC-32 as it wrote it.</item>
/// </list>
/// A killed worker may leave its temporary file or staging folder, under one deterministic name.
/// </summary>
internal sealed class ArchiveJobModule : IJobModule
{
    public static ArchiveJobModule Instance { get; } = new();

    public string Name => ArchiveCapabilities.Module;

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
                ArchiveCapabilities.InfoId or ArchiveCapabilities.TestId =>
                    request.Inputs.Count == 1 && result.Outputs.Count == 0 && JobFacts.AreShowable(result.Facts),
                ArchiveCapabilities.ZipId => ZipHoldsUp(request, result),
                ArchiveCapabilities.ExtractId => request.Inputs.Count == 1 && ExtractHoldsUp(request, result),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool ZipHoldsUp(JobRequest request, JobResult result)
    {
        // The only fact a ZIP may carry is how many links were not followed, which the toast shows.
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output]) return false;
        if (result.Facts.Count > 0 && !(result.Facts is [{ Label: "Skipped", Notice: true }] && JobFacts.AreShowable(result.Facts))) return false;
        if (!ArchiveCapabilities.Candidates(ArchiveCapabilities.ZipId, request.Inputs, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
        using var file = new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[4];
        if (file.ReadAtLeast(head, 4, throwOnEndOfStream: false) != 4 || head[0] != 'P' || head[1] != 'K') return false;
        file.Position = 0;
        return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), claimed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ExtractHoldsUp(JobRequest request, JobResult result)
    {
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output] || !JobFacts.AreShowable(result.Facts)) return false;
        if (result.Facts.FirstOrDefault(f => f.Label == "Files") is not { } files
            || !int.TryParse(files.Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var count) || count < 0) return false;
        if (!ArchiveCapabilities.Candidates(ArchiveCapabilities.ExtractId, request.Inputs, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
        // Only a real link is refused: OneDrive marks the files it syncs with a reparse point, and they are data. The walk
        // stops past what an extraction may create, so a forged claim cannot send the host through a huge tree.
        var folder = new DirectoryInfo(output.Path);
        if (!folder.Exists || folder.LinkTarget is not null) return false;
        return string.Equals(FolderManifest.ShapeOf(output.Path, ArchiveLimits.MaxEntries + 1), claimed, StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<string> TempPathsOf(JobRequest request)
    {
        if (request.Inputs.Count == 0) yield break;
        string temp;
        try { temp = ArchiveCapabilities.TempPath(request.Id, request.Inputs, request.OutputDirectory); }
        catch (ArgumentException) { yield break; }
        yield return temp;
    }
}
