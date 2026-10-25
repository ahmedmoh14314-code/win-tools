using WinTools.Core.Feedback;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The General Files module (V2 PR 1.4, the walking skeleton of a second module): File info and SHA-256, read in the
/// text worker, and Copy path, which runs in the host and parses nothing. They accept any file; File info and Copy path
/// accept folders too. They share the Tools category with the image module: a sixth category would narrow the image
/// module's top ring until its labels reach into the hub gap, and categories are designed to be shared (ADR-0019).
/// V2 PR 7.2 adds Copy name and Copy folder path (the host, like Copy path) and Batch rename (a dialog with a preview, then
/// the host renames; files only, in one folder, never overwriting).
/// </summary>
public static class FilesCapabilities
{
    public const string Module = "files";
    public const string InfoId = "files.info", Sha256Id = "files.sha256", CopyPathId = "files.copy-path";
    public const string CopyNameId = "files.copy-name", CopyFolderId = "files.copy-folder", RenameId = "files.rename";

    /// <summary>What a failed Batch rename tells the user (review, PR 7.2): the generic reasons speak of outputs.</summary>
    private static readonly IReadOnlyDictionary<Jobs.ErrorCode, string> RenameReasons = new Dictionary<Jobs.ErrorCode, string>
    {
        [Jobs.ErrorCode.NoFreeOutputName] = "A new name is already used by another file in the folder. Nothing was renamed.",
        [Jobs.ErrorCode.OutputNameInvalid] = "A new name is not allowed on Windows, or two files would get the same name. Nothing was renamed.",
        [Jobs.ErrorCode.SourceNotReadable] = "A file is open in another program or cannot be changed. Every name was put back.",
        [Jobs.ErrorCode.SourceNotFound] = "A file is no longer there. Nothing was renamed.",
        [Jobs.ErrorCode.OutputValidationFailed] = "The renamed files could not be checked afterwards. Look at the folder to see their names.",
        [Jobs.ErrorCode.Incomplete] = "A file could not be renamed or put back, and keeps a name starting with \"wintools-renaming\". Rename it back by hand.",
    };

    /// <summary>The commands the host runs itself, putting text on the clipboard.</summary>
    public static bool IsCopy(string capabilityId) => capabilityId is CopyPathId or CopyNameId or CopyFolderId;

    /// <summary>Tools, declared as the image module declares it, so the module stands on its own (the first declaration wins).</summary>
    /// <summary>Tools holds the file's information (File info, SHA-256); Actions what to do with the file rather than to it
    /// (copy its path or name, rename), the third group beside Convert and Tools.</summary>
    public static IReadOnlyList<CapabilityCategory> Categories { get; } = [new("tools", "Tools", "tools", 11), new("actions", "Actions", "actions", 95)];

    private static readonly IReadOnlySet<string> AnyFile = FileKinds.Declared.Where(k => k != FileKinds.Folder).Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> AnyFileOrFolder = FileKinds.Declared.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>File info's inputs: files no module reads, and folders. A file with its own info (Image info, PDF info,
    /// Video info...) does not need a second one, and its Tools ring stays narrow enough for its labels.</summary>
    private static readonly IReadOnlySet<string> NoOwnInfo = new HashSet<string>([FileKinds.AnyFile.Id, FileKinds.Folder.Id], StringComparer.Ordinal);

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        // In the order a normal user needs them: on an image the ring shows the first ones, the rest go under "More…" (V2 PR 7.2).
        new(InfoId, "File info", "tools", Module, new InputRule(NoOwnInfo), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: false,
            Feedback: new("Reading", "read", "Read", "Nothing needed doing", "item", "items")),
        new(Sha256Id, "SHA-256", "tools", Module, new InputRule(AnyFile), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: true,
            Feedback: new("Hashing", "hash", "Hashed", "Nothing needed doing", "file", "files")),
        new(CopyPathId, "Copy path", "actions", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.Clipboard, LongRunning: false,
            Feedback: new("Copying", "copy", "Path copied", "Nothing needed doing", "path", "paths")),
        new(CopyNameId, "Copy name", "actions", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.Clipboard, LongRunning: false,
            Feedback: new("Copying", "copy", "Name copied", "Nothing needed doing", "name", "names")),
        new(RenameId, "Rename files", "actions", Module, new InputRule(AnyFile), CapabilityShape.Selection, CapabilityOutput.Files, LongRunning: false,
            Feedback: new("Renaming", "rename", "Renamed", "No name changed", "file", "files", SuccessTitle: "Files renamed", Reasons: RenameReasons),
            Dialog: Jobs.DialogKinds.Rename),
        new(CopyFolderId, "Folder path", "tools", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.Clipboard, LongRunning: false,
            Feedback: new("Copying", "copy", "Folder path copied", "Nothing needed doing", "path", "paths")),
    ];

    /// <summary>What Copy path puts on the clipboard: one path per line, a folder without the drop target's mark.</summary>
    public static string ClipboardText(IReadOnlyList<string> paths) => ClipboardText(CopyPathId, paths);

    /// <summary>What a copy command puts on the clipboard: one path or name per line (a folder without the drop target's
    /// mark), or each containing folder once.</summary>
    public static string ClipboardText(string capabilityId, IReadOnlyList<string> paths)
    {
        var plain = paths.Select(p => Path.EndsInDirectorySeparator(p) ? p[..^1] : p);
        return string.Join("\r\n", capabilityId switch
        {
            CopyNameId => plain.Select(Path.GetFileName),
            CopyFolderId => plain.Select(p => Path.GetDirectoryName(p) ?? p).Distinct(StringComparer.OrdinalIgnoreCase),
            _ => plain,
        });
    }
}
