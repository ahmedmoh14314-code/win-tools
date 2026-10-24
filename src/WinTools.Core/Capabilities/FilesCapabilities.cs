using WinTools.Core.Feedback;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The General Files module (V2 PR 1.4, the walking skeleton of a second module): File info and SHA-256, read in the
/// text worker, and Copy path, which runs in the host and parses nothing. They accept any file; File info and Copy path
/// accept folders too. They share the Tools category with the image module: a sixth category would narrow the image
/// module's top ring until its labels reach into the hub gap, and categories are designed to be shared (ADR-0019).
/// </summary>
public static class FilesCapabilities
{
    public const string Module = "files";
    public const string InfoId = "files.info", Sha256Id = "files.sha256", CopyPathId = "files.copy-path";

    /// <summary>Tools, declared as the image module declares it, so the module stands on its own (the first declaration wins).</summary>
    public static IReadOnlyList<CapabilityCategory> Categories { get; } = [new("tools", "Tools", "tools", 90)];

    private static readonly IReadOnlySet<string> AnyFile = FileKinds.Declared.Where(k => k != FileKinds.Folder).Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> AnyFileOrFolder = FileKinds.Declared.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        new(InfoId, "File info", "tools", Module, new InputRule(AnyFileOrFolder), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: false,
            Feedback: new("Reading", "read", "Read", "Nothing needed doing", "item", "items")),
        new(Sha256Id, "SHA-256", "tools", Module, new InputRule(AnyFile), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: true,
            Feedback: new("Hashing", "hash", "Hashed", "Nothing needed doing", "file", "files")),
        new(CopyPathId, "Copy path", "tools", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.Clipboard, LongRunning: false,
            Feedback: new("Copying", "copy", "Path copied", "Nothing needed doing", "path", "paths")),
    ];

    /// <summary>What Copy path puts on the clipboard: one path per line, a folder without the drop target's mark.</summary>
    public static string ClipboardText(IReadOnlyList<string> paths) =>
        string.Join("\r\n", paths.Select(p => Path.EndsInDirectorySeparator(p) ? p[..^1] : p));
}
