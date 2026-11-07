using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Host.Jobs;

/// <summary>
/// The Documents module's host side (V2 PR 6.1, ADR-0025): Info is believed as bounded facts; extracted text only under
/// a name the module allows, as valid UTF-8 with a BOM, with the SHA-256 the worker validated; a copy without properties likewise.
/// ADR-0033's conversions the same way: a workbook (a ZIP package), a page (UTF-8 carrying the module's
/// Content-Security-Policy, with no script element), and CSV, one file or a folder of them whose manifest hash is
/// recomputed from the disk. Their facts are notices only (what was read as or changed), and a folder's sheet count.
/// </summary>
internal sealed class DocumentJobModule : IJobModule
{
    public static DocumentJobModule Instance { get; } = new();

    public string Name => DocumentCapabilities.Module;

    public static IReadOnlyDictionary<string, string>? Parameters(Core.Settings.AppSettings _) => null;

    public bool IsBelievable(JobRequest request, JobResult result)
    {
        if (result.Outputs is null || result.Facts is null) return false;
        if (!result.Success) return result.Outputs.Count == 0 && result.Facts.Count == 0 && result.Error != ErrorCode.None;
        if (request.Inputs is not [var source]) return false;
        if (request.CapabilityId == DocumentCapabilities.InfoId) return result.Outputs.Count == 0 && JobFacts.AreShowable(result.Facts);
        if (request.CapabilityId == DocumentCapabilities.RemovePropertiesId) return IsBelievableCopy(request, source, result);
        if (request.CapabilityId is DocumentCapabilities.ToXlsxId or DocumentCapabilities.ToHtmlId or DocumentCapabilities.ToCsvId)
            return IsBelievableConversion(request, source, result);
        if (request.CapabilityId != DocumentCapabilities.ExtractTextId || result.Outputs is not [{ Sha256: { Length: 64 } claimed } output] || result.Facts.Count > 0) return false;
        try
        {
            if (!DocumentCapabilities.Candidates(source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
            return IsUtf8TextWithBom(output.Path, claimed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.DecoderFallbackException) { return false; }
    }

    /// <summary>A text file of at most 256 MB, UTF-8 with a BOM, whose SHA-256 is the one claimed.</summary>
    private static bool IsUtf8TextWithBom(string path, string claimed)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > 256L << 20) return false;
        var bytes = File.ReadAllBytes(path);
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), claimed, StringComparison.OrdinalIgnoreCase)) return false;
        if (!bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return false;
        _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 3, bytes.Length - 3);
        return true;
    }

    /// <summary>
    /// To Excel, To HTML and To CSV (ADR-0033): one output under a name the module allows, checked from the disk; notices
    /// only, besides the sheet count of a folder.
    /// </summary>
    private static bool IsBelievableConversion(JobRequest request, string source, JobResult result)
    {
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output] || (result.Facts.Count > 0 && !JobFacts.AreShowable(result.Facts))
            || !result.Facts.All(f => f.Notice || f.Label == "Sheets")) return false;
        try
        {
            switch (request.CapabilityId)
            {
                case DocumentCapabilities.ToXlsxId:
                {
                    if (result.Facts.Any(f => !f.Notice) || !DocumentCapabilities.XlsxCandidates(source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
                    using var file = new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length > 1L << 30) return false;
                    Span<byte> head = stackalloc byte[4];
                    if (file.ReadAtLeast(head, 4, throwOnEndOfStream: false) != 4 || !head.SequenceEqual("PK\u0003\u0004"u8)) return false;
                    file.Position = 0;
                    return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), claimed, StringComparison.OrdinalIgnoreCase);
                }
                case DocumentCapabilities.ToHtmlId:
                {
                    if (result.Facts.Any(f => !f.Notice) || !DocumentCapabilities.HtmlCandidates(source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
                    var info = new FileInfo(output.Path);
                    if (!info.Exists || info.LinkTarget is not null || info.Length > 256L << 20) return false;
                    var bytes = File.ReadAllBytes(output.Path);
                    if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), claimed, StringComparison.OrdinalIgnoreCase)) return false;
                    var html = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                    return html.Contains(DocumentCapabilities.HtmlPolicy, StringComparison.Ordinal) && !html.Contains("<script", StringComparison.OrdinalIgnoreCase);
                }
                default: // To CSV: one file, or a folder of them
                    if (Directory.Exists(output.Path)) return IsBelievableCsvFolder(request, source, result, output.Path, claimed);
                    return result.Facts.All(f => f.Notice) && DocumentCapabilities.CsvCandidates(source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)
                        && IsUtf8TextWithBom(output.Path, claimed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.DecoderFallbackException) { return false; }
    }

    /// <summary>
    /// Several sheets: a folder under an allowed name, not a link, holding only CSV files (2 to the module's maximum, as
    /// many as the "Sheets" fact says), each UTF-8 with a BOM, whose manifest hash is the one claimed.
    /// </summary>
    private static bool IsBelievableCsvFolder(JobRequest request, string source, JobResult result, string folderPath, string claimed)
    {
        if (!DocumentCapabilities.CsvFolderCandidates(source, request.OutputDirectory).Contains(folderPath, StringComparer.OrdinalIgnoreCase)) return false;
        if (result.Facts.Where(f => !f.Notice).ToArray() is not [{ Label: "Sheets" } sheets]
            || !int.TryParse(sheets.Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out var count)
            || count is < 2 or > DocumentCapabilities.MaxCsvSheets) return false;
        var folder = new DirectoryInfo(folderPath);
        if (folder.LinkTarget is not null) return false;
        var entries = folder.EnumerateFileSystemInfos().Take(count + 1).ToArray();
        if (entries.Length != count) return false;
        Span<byte> bom = stackalloc byte[3];
        foreach (var e in entries)
        {
            if (e is not FileInfo f || f.LinkTarget is not null || !string.Equals(f.Extension, ".csv", StringComparison.OrdinalIgnoreCase) || f.Length > 256L << 20) return false;
            using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (s.ReadAtLeast(bom, 3, throwOnEndOfStream: false) != 3 || !bom.SequenceEqual((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return false;
        }
        return string.Equals(FolderManifest.HashOf(folderPath), claimed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Remove properties (V2 PR 6.2): one copy under a name the module allows, a ZIP package with the SHA-256 the worker
    /// validated, and notices only (what was not removed).
    /// </summary>
    private static bool IsBelievableCopy(JobRequest request, string source, JobResult result)
    {
        if (result.Outputs is not [{ Sha256: { Length: 64 } claimed } output] || (result.Facts.Count > 0 && (!result.Facts.All(f => f.Notice) || !JobFacts.AreShowable(result.Facts)))) return false;
        try
        {
            if (!DocumentCapabilities.CleanCandidates(source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
            using var file = new FileStream(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 2 * DocumentCapabilities.MaxInputBytes) return false;
            Span<byte> head = stackalloc byte[4];
            if (file.ReadAtLeast(head, 4, throwOnEndOfStream: false) != 4 || !head.SequenceEqual("PK\u0003\u0004"u8)) return false;
            file.Position = 0;
            return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)), claimed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>The destination temp, which is also To CSV's staging folder (the scheduler removes either).</summary>
    public IEnumerable<string> TempPathsOf(JobRequest request)
    {
        if (request.Inputs.Count != 1) yield break;
        string temp;
        try { temp = Core.Output.OutputNaming.TempPath(request.Inputs[0], request.Id, request.OutputDirectory); }
        catch (ArgumentException) { yield break; }
        yield return temp;
    }
}
