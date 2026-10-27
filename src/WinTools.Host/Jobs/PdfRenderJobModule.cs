using System.Security.Cryptography;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Host.Jobs;

/// <summary>
/// The pdfrender module's host side (ADR-0029), run by the image worker. A success is believed only from the disk:
/// <list type="bullet">
/// <item>A file (a one-page PDF's picture, its text, a new PDF of pictures): one, under a name the module allows, whose
/// SHA-256 is the one the worker validated; facts only as notices.</item>
/// <item>A folder of pages: one, under an allowed name, holding no link, whose shape (paths and sizes) is the one the
/// worker reported.</item>
/// </list>
/// A killed worker may leave its temporary file or folder, under one deterministic name.
/// </summary>
internal sealed class PdfRenderJobModule : IJobModule
{
    public static PdfRenderJobModule Instance { get; } = new();

    public string Name => PdfRenderCapabilities.Module;

    public static IReadOnlyDictionary<string, string>? Parameters(Core.Settings.AppSettings _) => null;

    public bool IsBelievable(JobRequest request, JobResult result)
    {
        if (result.Outputs is null || result.Facts is null) return false;
        if (!result.Success) return result.Outputs.Count == 0 && result.Facts.Count == 0 && result.Error != ErrorCode.None;
        if (request.Inputs.Count == 0 || result.Facts.Any(f => !f.Notice) || result.Facts.Count > 0 && !JobFacts.AreShowable(result.Facts)) return false;
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output]) return false;
        var images = request.CapabilityId == PdfRenderCapabilities.ImagesToPdfId;
        if (!images && request.Inputs.Count != 1) return false;
        var source = images ? PdfRenderCapabilities.PageOrder(request.Inputs)[0] : request.Inputs[0];
        try
        {
            if (Directory.Exists(output.Path))
            {
                if (PdfRenderCapabilities.PageFormatOf(request.CapabilityId) is null) return false;
                if (!PdfRenderCapabilities.Candidates(request.CapabilityId, source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
                var folder = new DirectoryInfo(output.Path);
                return folder.LinkTarget is null
                    && string.Equals(FolderManifest.ShapeOf(output.Path, PdfRenderCapabilities.MaxPages + 1), claimed, StringComparison.OrdinalIgnoreCase);
            }
            var onePage = PdfRenderCapabilities.PageFormatOf(request.CapabilityId) is not null;
            if (!PdfRenderCapabilities.Candidates(request.CapabilityId, source, request.OutputDirectory, onePage).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
            using var stream = new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length > 0 && string.Equals(Convert.ToHexString(SHA256.HashData(stream)), claimed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public IEnumerable<string> TempPathsOf(JobRequest request)
    {
        if (request.Inputs.Count == 0) yield break;
        string temp;
        try
        {
            var first = request.CapabilityId == PdfRenderCapabilities.ImagesToPdfId ? PdfRenderCapabilities.PageOrder(request.Inputs)[0] : request.Inputs[0];
            temp = OutputNaming.TempPath(first, request.Id, request.OutputDirectory);
        }
        catch (ArgumentException) { yield break; }
        yield return temp;
    }
}
