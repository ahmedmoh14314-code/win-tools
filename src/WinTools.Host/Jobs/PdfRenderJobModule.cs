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
            var format = PdfRenderCapabilities.PageFormatOf(request.CapabilityId);
            if (Directory.Exists(output.Path))
            {
                // A folder of pages: exactly "page 001" to the count the worker reported, each a picture of the format, no
                // link and nothing else, and the content hash the worker computed (review, #39: the shape alone let a
                // compromised worker leave any files there).
                if (format is not { } f) return false;
                if (!PdfRenderCapabilities.Candidates(request.CapabilityId, source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
                var folder = new DirectoryInfo(output.Path);
                if (folder.LinkTarget is not null || result.Facts.FirstOrDefault(x => x.Label == "Pages") is not { } count
                    || !int.TryParse(count.Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out var pages)
                    || pages is < 2 or > PdfRenderCapabilities.MaxPages) return false;
                var ext = f == Core.Files.ImageFormat.Png ? ".png" : ".jpg";
                var entries = folder.GetFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
                if (entries.Length != pages || entries.Where((e, i) => e is not FileInfo { LinkTarget: null } || e.Name != PdfRenderCapabilities.PageFileName(i, pages, ext)).Any()) return false;
                if (!entries.All(e => Looks(e.FullName, request.CapabilityId))) return false;
                return string.Equals(FolderManifest.HashOf(output.Path), claimed, StringComparison.OrdinalIgnoreCase);
            }
            if (!PdfRenderCapabilities.Candidates(request.CapabilityId, source, request.OutputDirectory, onePage: format is not null).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
            if (!Looks(output.Path, request.CapabilityId)) return false;
            using var stream = new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length > 0 && string.Equals(Convert.ToHexString(SHA256.HashData(stream)), claimed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>Whether a written file starts as its command's output must: a PNG or JPEG signature, "%PDF-", or text that is UTF-8.</summary>
    private static bool Looks(string path, string capabilityId)
    {
        var bytes = File.ReadAllBytes(path);
        return capabilityId switch
        {
            PdfRenderCapabilities.ToPngId => bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            PdfRenderCapabilities.ToJpgId => bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
            PdfRenderCapabilities.ImagesToPdfId => bytes.AsSpan().StartsWith("%PDF-"u8),
            _ => System.Text.Unicode.Utf8.IsValid(bytes),
        };
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
