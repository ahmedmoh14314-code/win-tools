using System.Globalization;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;

namespace WinTools.Host.Jobs;

/// <summary>
/// To PDF through the user's LibreOffice (ADR-0033), host side: one PDF under a name the module allows ("report.pdf",
/// "report (1).pdf"...), starting "%PDF-", at most <see cref="OfficeCapabilities.MaxOutputBytes"/>, whose SHA-256 is the
/// one the worker validated, and one fact beside notices, its page count. A killed worker may leave its destination temp and its work
/// folder (the copy, the profile, LibreOffice's output) under the user's temp folder; both are removed.
/// </summary>
internal sealed class OfficeJobModule : IJobModule
{
    public static OfficeJobModule Instance { get; } = new();

    public string Name => OfficeCapabilities.Module;

    public static IReadOnlyDictionary<string, string>? Parameters(Core.Settings.AppSettings _) => null;

    public bool IsBelievable(JobRequest request, JobResult result)
    {
        if (result.Outputs is null || result.Facts is null) return false;
        if (!result.Success) return result.Outputs.Count == 0 && result.Facts.Count == 0 && result.Error != ErrorCode.None;
        if (request.CapabilityId != OfficeCapabilities.ToPdfId || request.Inputs is not [var source]) return false;
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output]) return false;
        if (!JobFacts.AreShowable(result.Facts) || result.Facts.Where(f => !f.Notice).ToArray() is not [{ Label: "Pages" } pages]
            || !int.TryParse(pages.Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var n) || n < 1) return false;
        try
        {
            if (!OfficeCapabilities.Candidates(source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(output.Path);
            if (!info.Exists || info.LinkTarget is not null || info.Length > OfficeCapabilities.MaxOutputBytes) return false;
            using var file = new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[5];
            if (file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) != head.Length || !head.AsSpan().SequenceEqual("%PDF-"u8)) return false;
            file.Position = 0;
            return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), claimed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// Removes conversions' work folders a crash left: each holds a copy of a user's document. Called once, when the host
    /// starts: it is the only host of this session, so no job of it is running. A file another session of the same user holds
    /// open stays, and the next start tries again.
    /// </summary>
    public static void SweepLeftovers(string? tempFolder = null)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(tempFolder ?? Path.GetTempPath(), OfficeCapabilities.WorkFolderPrefix + "*"))
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public IEnumerable<string> TempPathsOf(JobRequest request)
    {
        if (request.Inputs.Count != 1 || request.Id.Length == 0 || request.Id.Any(c => !char.IsAsciiLetterOrDigit(c))) yield break;
        yield return Path.Combine(Path.GetTempPath(), OfficeCapabilities.WorkFolderName(request.Id));
        string temp;
        try { temp = Core.Output.OutputNaming.TempPath(request.Inputs[0], request.Id, request.OutputDirectory); }
        catch (ArgumentException) { yield break; }
        yield return temp;
    }
}
