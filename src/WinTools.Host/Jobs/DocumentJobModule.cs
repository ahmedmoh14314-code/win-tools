using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;

namespace WinTools.Host.Jobs;

/// <summary>
/// The Documents module's host side (V2 PR 6.1, ADR-0025): Info is believed as bounded facts; extracted text only under
/// a name the module allows, as valid UTF-8 with a BOM, with the SHA-256 the worker validated; a copy without properties likewise.
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
        if (request.CapabilityId != DocumentCapabilities.ExtractTextId || result.Outputs is not [{ Sha256: { Length: 64 } claimed } output] || result.Facts.Count > 0) return false;
        try
        {
            if (!DocumentCapabilities.Candidates(source, request.OutputDirectory).Contains(output.Path, StringComparer.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(output.Path);
            if (!info.Exists || info.Length > 256L << 20) return false;
            var bytes = File.ReadAllBytes(output.Path);
            if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), claimed, StringComparison.OrdinalIgnoreCase)) return false;
            if (!bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return false;
            _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 3, bytes.Length - 3);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.DecoderFallbackException) { return false; }
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

    public IEnumerable<string> TempPathsOf(JobRequest request)
    {
        if (request.Inputs.Count != 1) yield break;
        string temp;
        try { temp = Core.Output.OutputNaming.TempPath(request.Inputs[0], request.Id, request.OutputDirectory); }
        catch (ArgumentException) { yield break; }
        yield return temp;
    }
}
