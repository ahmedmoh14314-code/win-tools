using WinTools.Core.Feedback;
using WinTools.Core.Files;

namespace WinTools.Core.Capabilities;

/// <summary>
/// The General Files module (V2 PR 1.4, the walking skeleton of a second module): File info and SHA-256, read in the
/// text worker, and Copy path, which runs in the host and parses nothing. They accept any file; File info and Copy path
/// accept folders too. They share the Tools category with the image module, whose top ring is full, and categories
/// are designed to be shared (ADR-0019).
/// V2 PR 7.2 adds Copy name and Copy folder path (the host, like Copy path) and Batch rename (a dialog with a preview, then
/// the host renames; files only, in one folder, never overwriting). ADR-0032 adds Copy to and Move to (a folder picked in
/// the dialogs process, then the host copies or moves, never overwriting), Show in folder and Open in Terminal (the host
/// opens Explorer or a terminal at the user's request).
/// </summary>
public static class FilesCapabilities
{
    public const string Module = "files";
    public const string InfoId = "files.info", Sha256Id = "files.sha256", CopyPathId = "files.copy-path";
    public const string CopyNameId = "files.copy-name", CopyFolderId = "files.copy-folder", RenameId = "files.rename";
    public const string CopyToId = "files.copy-to", MoveToId = "files.move-to", ShowId = "files.show-in-folder", TerminalId = "files.terminal";

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

    /// <summary>What a failed Copy to or Move to tells the user (ADR-0032), where the generic reasons speak of outputs.</summary>
    private static readonly IReadOnlyDictionary<Jobs.ErrorCode, string> TransferReasons = new Dictionary<Jobs.ErrorCode, string>
    {
        [Jobs.ErrorCode.NoFreeOutputName] = "Every free name in the destination is taken. Nothing was changed.",
        [Jobs.ErrorCode.OutputNameInvalid] = "The name is too long for the destination. Nothing was changed.",
        [Jobs.ErrorCode.OutputDirectoryNotWritable] = "The destination folder cannot be written to. Nothing was changed.",
        [Jobs.ErrorCode.OutputDirectoryIsLink] = "The destination is a link or junction, or inside one. Nothing was changed.",
        [Jobs.ErrorCode.SourceNotReadable] = "It is open in another program or cannot be read. The original is unchanged.",
        [Jobs.ErrorCode.SourceNotFound] = "It is no longer there, or the destination folder is gone.",
        [Jobs.ErrorCode.OutputValidationFailed] = "The copy did not match the original, so it was removed. The original is unchanged.",
        [Jobs.ErrorCode.UnsupportedOperation] = "A folder cannot go into itself. Nothing was changed.",
        [Jobs.ErrorCode.LimitExceeded] = "The folder holds links or junctions, which are not moved between drives. The original is unchanged.",
        [Jobs.ErrorCode.Incomplete] = "The copy is in the destination, but the original changed meanwhile or could not be fully removed. Check both.",
        [Jobs.ErrorCode.InvalidRequest] = "Windows cannot tell whether this folder is inside the one you are moving. Nothing was changed.",
    };

    /// <summary>What a failed Show in folder or Open in Terminal tells the user (ADR-0032).</summary>
    private static readonly IReadOnlyDictionary<Jobs.ErrorCode, string> WindowReasons = new Dictionary<Jobs.ErrorCode, string>
    {
        [Jobs.ErrorCode.SourceNotFound] = "It is no longer there.",
        [Jobs.ErrorCode.LimitExceeded] = "The items are in more than 5 folders. Drop fewer at a time.",
        [Jobs.ErrorCode.Internal] = "Windows did not open it. The path may be too long.",
    };

    /// <summary>The commands the host runs itself, putting text on the clipboard.</summary>
    public static bool IsCopy(string capabilityId) => capabilityId is CopyPathId or CopyNameId or CopyFolderId;

    /// <summary>Copy to and Move to, which the host runs itself after the folder window (ADR-0032).</summary>
    public static bool IsTransfer(string capabilityId) => capabilityId is CopyToId or MoveToId;

    /// <summary>Show in folder and Open in Terminal, which open another program's window at once (ADR-0032).</summary>
    public static bool IsWindow(string capabilityId) => capabilityId is ShowId or TerminalId;

    /// <summary>How many windows Show in folder or Open in Terminal opens at most, one per folder.</summary>
    public const int MaxWindows = 5;

    /// <summary>
    /// Tools, declared as the image module declares it, so the module stands on its own (the first declaration wins). Tools
    /// holds the file's information (File info, SHA-256, Folder path); Actions what to do with the file rather than to it
    /// (copy its path or name, copy or move it, rename, show it in its folder), the third group beside Convert and
    /// Tools. Actions holds long labels, so beyond four commands its ring shows three and "More…" (ADR-0032).
    /// </summary>
    public static IReadOnlyList<CapabilityCategory> Categories { get; } =
        [new("tools", "Tools", "tools", 11), new("actions", "Actions", "actions", 95, MostOnRing: CapabilityCategory.LongLabels)];

    private static readonly IReadOnlySet<string> AnyFile = FileKinds.Declared.Where(k => k != FileKinds.Folder).Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> AnyFileOrFolder = FileKinds.Declared.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<CapabilityDescriptor> Catalog { get; } =
    [
        // In the order a normal user needs them: on an image the ring shows the first ones, the rest go under "More…" (V2 PR 7.2).
        // A fallback: a file with its own info (Image info, PDF info, Video info...) needs no second one, and its Tools ring
        // stays narrow enough for its labels; any other file, folder or mixed selection gets it (PR #37 review).
        new(InfoId, "File info", "tools", Module, new InputRule(AnyFileOrFolder), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: false,
            Feedback: new("Reading", "read", "Read", "Nothing needed doing", "item", "items"), Fallback: true),
        new(Sha256Id, "SHA-256", "tools", Module, new InputRule(AnyFile), CapabilityShape.PerFile, CapabilityOutput.Facts, LongRunning: true,
            Feedback: new("Hashing", "hash", "Hashed", "Nothing needed doing", "file", "files")),
        new(CopyPathId, "Copy path", "actions", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.Clipboard, LongRunning: false,
            Feedback: new("Copying", "copy", "Path copied", "Nothing needed doing", "path", "paths")),
        // Move to and Copy to: any file or folder, each item on its own (a failure is that item's, the others go on); the
        // folder window is asked once for the whole drop (ADR-0032).
        new(MoveToId, "Move to…", "actions", Module, new InputRule(AnyFileOrFolder), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Moving", "move", "Moved", "Already in that folder", "item", "items", SuccessTitle: "{0} moved", Reasons: TransferReasons, ShowSizes: false),
            Dialog: Jobs.DialogKinds.Folder),
        new(RenameId, "Rename files", "actions", Module, new InputRule(AnyFile), CapabilityShape.Selection, CapabilityOutput.Files, LongRunning: false,
            Feedback: new("Renaming", "rename", "Renamed", "No name changed", "file", "files", SuccessTitle: "Files renamed", Reasons: RenameReasons),
            Dialog: Jobs.DialogKinds.Rename),
        new(CopyToId, "Copy to…", "actions", Module, new InputRule(AnyFileOrFolder), CapabilityShape.PerFile, CapabilityOutput.File, LongRunning: true,
            Feedback: new("Copying", "copy", "Copied", "Nothing needed doing", "item", "items", SuccessTitle: "{0} copied", Reasons: TransferReasons, ShowSizes: false),
            Dialog: Jobs.DialogKinds.Folder),
        new(CopyNameId, "Copy name", "actions", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.Clipboard, LongRunning: false,
            Feedback: new("Copying", "copy", "Name copied", "Nothing needed doing", "name", "names")),
        new(CopyFolderId, "Folder path", "tools", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.Clipboard, LongRunning: false,
            Feedback: new("Copying", "copy", "Folder path copied", "Nothing needed doing", "path", "paths")),
        // Where the item is: Explorer with the items selected, or a terminal in the folder. Under "More…" with Copy to, Copy
        // name and Add to ZIP: Actions shows three, and no Tools ring has room left (ADR-0032).
        new(ShowId, "Show in folder", "actions", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.Window, LongRunning: false,
            Feedback: new("Opening", "show", "Shown", "Nothing needed doing", "item", "items", Reasons: WindowReasons)),
        new(TerminalId, "Open in Terminal", "actions", Module, new InputRule(AnyFileOrFolder), CapabilityShape.Selection, CapabilityOutput.Window, LongRunning: false,
            Feedback: new("Opening", "open a terminal for", "Opened", "Nothing needed doing", "item", "items", Reasons: WindowReasons)),
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
