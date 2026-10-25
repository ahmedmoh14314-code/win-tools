using WinTools.Core.Feedback;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The Archives module, ZIP first (V2 plan §9, PR 5.1, ADR-0023): Extract into a new folder, Test the archive's integrity
/// (every entry decompressed and its CRC-32 checked), ZIP info, and Add to ZIP for any files and folders. Extraction never
/// writes outside its own new folder, never overwrites, never opens what it extracted. An encrypted ZIP is extracted and
/// tested with its password, asked only when the archive needs one (V2 PR 5.3, ADR-0027).
/// </summary>
public static class ArchiveCapabilities
{
    public const string Module = "archive";
    public const string ExtractId = "archive.extract", TestId = "archive.test", InfoId = "archive.info", ZipId = "archive.zip";
    public const string DecompressId = "archive.gunzip";

    /// <summary>"Archive" is the ZIP's own category; Add to ZIP sits in the shared Tools, so no other menu gains a ring sector.</summary>
    public static IReadOnlyList<CapabilityCategory> Categories { get; } =
    [
        new("archive", "Archive", "archive", 12),
        new("actions", "Actions", "actions", 95),
    ];

    /// <summary>The archives Extract, Test and Info read (V2 PR 5.2 adds TAR and TAR.GZ).</summary>
    private static readonly IReadOnlySet<string> ZipOnly = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Zip.Id, FileKinds.Tar.Id, FileKinds.TarGz.Id };
    private static readonly IReadOnlySet<string> GzipOnly = new HashSet<string>(StringComparer.Ordinal) { FileKinds.Gzip.Id };
    private static readonly IReadOnlySet<string> AnyFileOrFolder = FileKinds.Declared.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>The password of an encrypted ZIP: asked after a first run says one is needed, typed once (ADR-0027).</summary>
    private static readonly SecretPrompt ArchivePassword = new("Archive password", Confirm: false, WhenRequired: true);

    /// <summary>Several archives that need a password cannot share one dialog that names none of them.</summary>
    private const string DropAlone = "The archive is protected with a password. Drop it on its own to type the password.";

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        new(ExtractId, "Extract to folder", "archive", Module, new InputRule(ZipOnly), CapabilityShape.PerFile, CapabilityOutput.Folder,
            LongRunning: true, Feedback: new("Extracting", "extract", "Extracted", "Nothing to extract", "archive", "archives", PasswordRequired: DropAlone),
            Prompt: ArchivePassword),
        new(TestId, "Test", "archive", Module, new InputRule(ZipOnly), CapabilityShape.PerFile, CapabilityOutput.Facts,
            LongRunning: true, Feedback: new("Testing", "test", "Tested", "Nothing to test", "archive", "archives", PasswordRequired: DropAlone),
            Prompt: ArchivePassword),
        new(DecompressId, "Decompress", "archive", Module, new InputRule(GzipOnly), CapabilityShape.PerFile, CapabilityOutput.File,
            LongRunning: true, Feedback: new("Decompressing", "decompress", "Decompressed", "Nothing to decompress", "file", "files")),
        new(InfoId, "Archive info", "archive", Module, new InputRule(ZipOnly), CapabilityShape.PerFile, CapabilityOutput.Facts,
            LongRunning: false, Feedback: new("Reading", "read", "Read", "Nothing needed doing", "archive", "archives")),
        new(ZipId, "Add to ZIP", "actions", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.File,
            LongRunning: true, Feedback: new("Compressing", "compress", "ZIP created", "Nothing to add", "item", "items")),
    ];

    /// <summary>
    /// The folder or file a command's output is named after: Extract makes "photos" from "photos.zip"; Add to ZIP makes
    /// "report.zip" from one item, and from several the name of the folder that holds the first (as 7-Zip does).
    /// </summary>
    public static (string Directory, string Stem) OutputStem(string capabilityId, IReadOnlyList<string> inputs, string? outputDirectory)
    {
        var first = Path.TrimEndingDirectorySeparator(inputs[0]);
        var dir = outputDirectory ?? Path.GetDirectoryName(first) ?? throw new ArgumentException("The input has no folder.", nameof(inputs));
        if (capabilityId == ZipId && inputs.Count > 1)
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(first));
            return (dir, string.IsNullOrEmpty(parent) ? "Archive" : parent);
        }
        var stem = capabilityId == ZipId && Path.EndsInDirectorySeparator(inputs[0]) ? Path.GetFileName(first)
            : first.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(first)[..^".tar.gz".Length] // "logs.tar.gz" extracts to "logs"
            : Path.GetFileNameWithoutExtension(first);
        return (dir, stem.Length == 0 ? "Archive" : stem);
    }

    /// <summary>The names to try for a command's output, in order ("photos", "photos (1)"...; "report.zip", "report (1).zip"...).</summary>
    public static IEnumerable<string> Candidates(string capabilityId, IReadOnlyList<string> inputs, string? outputDirectory)
    {
        var (dir, stem) = OutputStem(capabilityId, inputs, outputDirectory);
        var ext = capabilityId == ZipId ? ".zip" : "";
        if (capabilityId == DecompressId)
        {
            // "notes.txt.gz" becomes "notes.txt", then "notes (1).txt"; a bare "data.gz" becomes "data".
            ext = Path.GetExtension(stem);
            stem = Path.GetFileNameWithoutExtension(stem);
            if (stem.Length == 0) (stem, ext) = (ext.Length > 0 ? ext : "file", "");
        }
        yield return Path.Combine(dir, stem + ext);
        for (var i = 1; i <= Output.OutputNaming.MaxCollisionIndex; i++) yield return Path.Combine(dir, $"{stem} ({i}){ext}");
    }

    /// <summary>The temporary name in the destination for one request (a file for Add to ZIP, a folder for Extract).</summary>
    public static string TempPath(string requestId, IReadOnlyList<string> inputs, string? outputDirectory)
    {
        if (requestId.Length == 0 || requestId.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Request id must be ASCII letters/digits.", nameof(requestId));
        var (dir, _) = OutputStem(ExtractId, inputs, outputDirectory);
        return Path.Combine(dir, $"~wintools-{requestId}.tmp");
    }
}
