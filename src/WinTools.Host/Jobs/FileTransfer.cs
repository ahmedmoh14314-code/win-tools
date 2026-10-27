using System.Security.Cryptography;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;
using WinTools.Windows.Shell;

namespace WinTools.Host.Jobs;

/// <summary>Test seams of <see cref="FileTransfer"/>: treat one volume as two, and act between the steps.</summary>
/// <param name="CrossVolume">Move as across volumes even on one (copy, verify, then remove the original).</param>
/// <param name="AfterCopy">After the copy is written under its temporary name, before it is verified.</param>
/// <param name="BeforeSourceRemoved">After the copy took its name, before the original is removed (a move across volumes).</param>
internal sealed record TransferHooks(bool CrossVolume = false, Action<string>? AfterCopy = null, Action<string>? BeforeSourceRemoved = null);

/// <summary>
/// Copy to and Move to in the host (ADR-0032): they parse nothing, so they need no worker. One item (a file, or a folder
/// marked by the drop target) to the folder the user picked.
/// <list type="bullet">
/// <item>Nothing is ever overwritten: the item keeps its name, or takes "name (1)", "name (2)"... (<see cref="TransferPlan"/>),
/// claimed by a rename that fails when the name is taken.</item>
/// <item>A move on one volume is that rename, atomic. Across volumes it is a copy under a temporary name in the destination,
/// checked (each file's size and SHA-256 against the original), named, and only then is the original removed. Any
/// failure before that leaves the original as it was and removes the copy; a folder whose original cannot be removed
/// keeps its complete copy and says so.</item>
/// <item>A copy is written under a temporary name and named only when its size matches; a failure removes it.</item>
/// <item>A folder never goes into itself. Links and junctions inside a copied folder are skipped (said in the toast) and
/// never followed; a move across volumes refuses them.</item>
/// <item>The copy keeps the original's streams (the Mark of the Web), attributes and times (<c>CopyFileExW</c>).</item>
/// </list>
/// </summary>
internal static class FileTransfer
{
    public static async Task<JobResult> RunAsync(JobRequest request, CancellationToken cancellationToken, TransferHooks? hooks = null)
    {
        hooks ??= new TransferHooks();
        var id = request.Id;
        var move = request.CapabilityId == FilesCapabilities.MoveToId;
        if (!FilesCapabilities.IsTransfer(request.CapabilityId) || request.Inputs.Count != 1 || request.Parameters is not { Count: 1 } p
            || !p.TryGetValue(DialogParameters.Name, out var picked) || TransferPlan.Destination(picked) is not { } destination)
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "Copy to and Move to take one item and the destination folder.");
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c))) return JobResult.Fail(id, ErrorCode.InvalidRequest, "The request id must be ASCII letters or digits.");
        var item = request.Inputs[0];
        if (!PathPolicy.IsSupported(item)) return JobResult.Fail(id, ErrorCode.InvalidRequest, "The item's path is not supported.");
        var folder = TransferPlan.IsFolder(item);
        var source = TransferPlan.Plain(item);
        if (TransferPlan.IntoItself(item, destination)) return JobResult.Fail(id, ErrorCode.UnsupportedOperation, "A folder cannot go into itself.");
        if (move && TransferPlan.AlreadyThere(item, destination)) return JobResult.Fail(id, ErrorCode.NothingToDo, "Already in that folder.");
        try
        {
            if (folder ? !Directory.Exists(source) : !File.Exists(source)) return JobResult.Fail(id, ErrorCode.SourceNotFound, "The item is no longer there.");
            if (CheckDestination(destination) is { } refused) return JobResult.Fail(id, refused, refused == ErrorCode.SourceNotFound
                ? "The destination folder is gone." : "The destination folder is, or is inside, a symbolic link or junction.");
            cancellationToken.ThrowIfCancellationRequested();

            if (move && !hooks.CrossVolume)
            {
                // One volume: a rename, atomic, for a file or a whole folder. Across volumes MoveFileExW answers
                // ERROR_NOT_SAME_DEVICE, and the copy below takes over.
                var (renamed, error) = Claim(source, destination, item);
                if (renamed is not null) return Done(id, item, renamed, destination, "", 0);
                if (error != FileSystemCalls.ErrorNotSameDevice) return Failed(id, error, Side.Source);
            }
            if (move && IsLink(source)) return JobResult.Fail(id, ErrorCode.LimitExceeded, "A link is not moved between drives.");
            return folder
                ? await CopyFolderAsync(id, item, source, destination, move, hooks, cancellationToken).ConfigureAwait(false)
                : await CopyFileAsync(id, item, source, destination, move, hooks, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return JobResult.Fail(id, ErrorCode.Cancelled, "Cancelled; the original is unchanged."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(id, Win32Of(ex), Side.Source);
        }
    }

    private enum Side { Source, Destination }

    /// <summary>The destination must still exist and must not be, or be inside, a link or junction (as for every module's outputs).</summary>
    private static ErrorCode? CheckDestination(string destination)
    {
        var dir = new DirectoryInfo(destination);
        if (!dir.Exists) return ErrorCode.SourceNotFound;
        for (var d = dir; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) return ErrorCode.OutputDirectoryIsLink;
        return null;
    }

    private static bool IsLink(string path)
    {
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is not null;
    }

    /// <summary>
    /// One file: copied under a temporary name in the destination, its size checked (and for a move its SHA-256 against
    /// the original, which is held open against writers meanwhile), named, then for a move the original removed. A copy
    /// that cannot be finished is removed; the original is touched only at the very end.
    /// </summary>
    private static async Task<JobResult> CopyFileAsync(string id, string item, string source, string destination, bool move, TransferHooks hooks, CancellationToken ct)
    {
        var temp = Path.Combine(destination, $"~wintools-{id}.tmp");
        FileStream? hold = null;
        string? named = null;
        try
        {
            // Readable at all? An access error from the copy is then the destination's.
            try
            {
                hold = new FileStream(source, FileMode.Open, FileAccess.Read, move ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failed(id, Win32Of(ex), Side.Source); }
            if (!move) { hold.Dispose(); hold = null; } // a copy does not keep other programs from writing the original
            var error = FileSystemCalls.CopyFile(source, temp, ct);
            if (error != 0) return Failed(id, error, Side.Destination);
            hooks.AfterCopy?.Invoke(temp);
            var length = hold?.Length ?? new FileInfo(source).Length;
            if (new FileInfo(temp).Length != length) return JobResult.Fail(id, ErrorCode.OutputValidationFailed, "The copy's size differs from the original's.");
            string? sha = null;
            if (move)
            {
                var (a, b) = await HashBothAsync(hold!, temp, ct).ConfigureAwait(false);
                if (!a.SequenceEqual(b)) return JobResult.Fail(id, ErrorCode.OutputValidationFailed, "The copy's SHA-256 differs from the original's.");
                sha = Convert.ToHexStringLower(b);
            }
            var (claimed, claimError) = Claim(temp, destination, item);
            if (claimed is null) return Failed(id, claimError, Side.Destination);
            named = claimed;
            if (move)
            {
                hold!.Dispose();
                hold = null;
                hooks.BeforeSourceRemoved?.Invoke(source);
                if (!TryDeleteFile(source))
                {
                    // The original stays; so the copy goes: a move that cannot finish leaves things as they were.
                    TryDeleteFile(named);
                    named = null;
                    return JobResult.Fail(id, ErrorCode.SourceNotReadable, "The original could not be removed, so the copy was removed again.");
                }
            }
            var done = Done(id, item, named, destination, sha ?? "", 0);
            named = null;
            return done;
        }
        finally
        {
            hold?.Dispose();
            if (File.Exists(temp)) TryDeleteFile(temp);
            if (named is not null) TryDeleteFile(named); // cancelled or failed after naming: never a half move
        }
    }

    /// <summary>
    /// A folder: its tree is listed first (so a destination inside it, reached through another name, is never copied into
    /// itself), copied into a temporary folder in the destination, each file checked, the folder named; for a move the
    /// original is then renamed aside (which fails while a file in it is open) and removed.
    /// </summary>
    private static async Task<JobResult> CopyFolderAsync(string id, string item, string source, string destination, bool move, TransferHooks hooks, CancellationToken ct)
    {
        var (dirs, files, links) = List(source);
        if (move && links > 0) return JobResult.Fail(id, ErrorCode.LimitExceeded, "The folder holds links, which are not moved between drives.");
        var temp = Path.Combine(destination, $"~wintools-{id}.tmp");
        if (Directory.Exists(temp) || File.Exists(temp)) return JobResult.Fail(id, ErrorCode.NoFreeOutputName, "The temporary name is taken.");
        var created = false;
        string? named = null;
        try
        {
            try { Directory.CreateDirectory(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failed(id, Win32Of(ex), Side.Destination); }
            created = true;
            foreach (var dir in dirs) Directory.CreateDirectory(Path.Combine(temp, dir));
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var from = Path.Combine(source, file);
                var to = Path.Combine(temp, file);
                var error = FileSystemCalls.CopyFile(from, to, ct);
                if (error != 0) return Failed(id, error, error == FileSystemCalls.ErrorAccessDenied && !Readable(from) ? Side.Source : Side.Destination);
                if (new FileInfo(to).Length != new FileInfo(from).Length) return JobResult.Fail(id, ErrorCode.OutputValidationFailed, "A copied file's size differs from the original's.");
            }
            hooks.AfterCopy?.Invoke(temp);
            if (move)
            {
                foreach (var file in files)
                {
                    using var original = new FileStream(Path.Combine(source, file), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                    var (a, b) = await HashBothAsync(original, Path.Combine(temp, file), ct).ConfigureAwait(false);
                    if (!a.SequenceEqual(b)) return JobResult.Fail(id, ErrorCode.OutputValidationFailed, "A copied file's SHA-256 differs from the original's.");
                }
            }
            var (claimed, claimError) = Claim(temp, destination, item);
            if (claimed is null) return Failed(id, claimError, Side.Destination);
            named = claimed;
            created = false;
            if (move)
            {
                // Renamed aside first: Windows refuses to rename a folder while a file in it is open, so nothing is
                // removed from a folder another program is using.
                var parent = Path.GetDirectoryName(source)!;
                var aside = Path.Combine(parent, $"~wintools-{id}.moving");
                var asideError = FileSystemCalls.MoveOnVolume(source, aside);
                if (asideError != 0) return Failed(id, asideError, Side.Source);
                hooks.BeforeSourceRemoved?.Invoke(aside);
                if (!TryDeleteTree(aside))
                {
                    // Everything is in the copy; what is left of the original goes back to its name for the user to remove.
                    _ = FileSystemCalls.MoveOnVolume(aside, source);
                    named = null; // the complete copy stays
                    return JobResult.Fail(id, ErrorCode.Incomplete, "Copied and checked, but part of the original folder could not be removed.");
                }
            }
            var done = Done(id, item, claimed, destination, "", links);
            named = null;
            return done;
        }
        finally
        {
            if (created) TryDeleteTree(temp);
            if (named is not null) TryDeleteTree(named);
        }
    }

    /// <summary>
    /// The folder's subfolders and files, relative to it, listed before anything is copied. Links and junctions are
    /// counted and never followed; hidden and system entries are copied like the rest.
    /// </summary>
    private static (List<string> Dirs, List<string> Files, int Links) List(string root)
    {
        var dirs = new List<string>();
        var files = new List<string>();
        var links = 0;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var pending = new Stack<string>();
        pending.Push("");
        while (pending.Count > 0)
        {
            var rel = pending.Pop();
            foreach (var entry in new DirectoryInfo(Path.Combine(root, rel)).EnumerateFileSystemInfos("*", options))
            {
                var name = rel.Length == 0 ? entry.Name : Path.Combine(rel, entry.Name);
                if (entry.LinkTarget is not null) { links++; continue; }
                if (entry is DirectoryInfo) { dirs.Add(name); pending.Push(name); }
                else files.Add(name);
            }
        }
        return (dirs, files, links);
    }

    /// <summary>Both files hashed side by side (they are usually on two disks).</summary>
    private static async Task<(byte[] Original, byte[] Copy)> HashBothAsync(FileStream original, string copy, CancellationToken ct)
    {
        original.Position = 0;
        await using var copied = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var a = Task.Run(() => SHA256.HashDataAsync(original, ct).AsTask(), ct);
        var b = Task.Run(() => SHA256.HashDataAsync(copied, ct).AsTask(), ct);
        await Task.WhenAll(a, b).ConfigureAwait(false);
        return (await a.ConfigureAwait(false), await b.ConfigureAwait(false));
    }

    /// <summary>Renames <paramref name="from"/> to the first free name for <paramref name="item"/> in the destination; a
    /// taken name moves on to the next, any other error stops. Retries briefly while another program holds the item.</summary>
    private static (string? Path, int Error) Claim(string from, string destination, string item)
    {
        foreach (var candidate in TransferPlan.Candidates(item, destination))
        {
            int error;
            for (var attempt = 0; ; attempt++)
            {
                error = FileSystemCalls.MoveOnVolume(from, candidate);
                if (error is not (FileSystemCalls.ErrorSharingViolation or FileSystemCalls.ErrorLockViolation) || attempt >= 4) break;
                Thread.Sleep(100); // an indexer, an antivirus or a sync client looking at it for a moment
            }
            if (error == 0) return (candidate, 0);
            if (error is not (FileSystemCalls.ErrorAlreadyExists or FileSystemCalls.ErrorFileExists)) return (null, error);
        }
        return (null, FileSystemCalls.ErrorAlreadyExists);
    }

    private static bool Readable(string path)
    {
        try { using var _ = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Deletes a file, read-only or not; the attribute is put back when the delete fails.</summary>
    private static bool TryDeleteFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            try { File.Delete(path); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.SetAttributes(path, attributes); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Deletes a folder tree, read-only files included; a link inside is removed as a link, never followed. Stops
    /// at the first failure, so as much as possible stays.</summary>
    private static bool TryDeleteTree(string path)
    {
        try
        {
            var root = new DirectoryInfo(path);
            if (!root.Exists) return true;
            if (root.LinkTarget is not null) { root.Delete(); return true; }
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
            foreach (var entry in root.EnumerateFileSystemInfos("*", options))
            {
                if (entry is DirectoryInfo d) { if (!TryDeleteTree(d.FullName)) return false; }
                else if (entry.LinkTarget is not null) entry.Delete();
                else if (!TryDeleteFile(entry.FullName)) return false;
            }
            if (root.Attributes.HasFlag(FileAttributes.ReadOnly)) root.Attributes &= ~FileAttributes.ReadOnly;
            root.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The success: the new item, and notices for the toast in the language in use ("to Documents", "as report (1).pdf").</summary>
    private static JobResult Done(string id, string item, string named, string destination, string sha, int links)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(destination));
        var facts = new List<JobFact> { new("Destination", UiText.F("transfer.to", TransferPlan.ShowableName(folderName.Length > 0 ? folderName : destination)), Notice: true) };
        if (!string.Equals(Path.GetFileName(named), Path.GetFileName(TransferPlan.Plain(item)), StringComparison.Ordinal))
            facts.Add(new("Saved as", UiText.F("transfer.as", TransferPlan.ShowableName(Path.GetFileName(named))), Notice: true));
        if (links > 0) facts.Add(new("Links skipped", UiText.F("transfer.linksSkipped", links), Notice: true));
        return new JobResult(id, true, ErrorCode.None, null, [new JobOutput(named, sha)], facts);
    }

    private static JobResult Failed(string id, int win32, Side side) => win32 switch
    {
        FileSystemCalls.ErrorFileNotFound or FileSystemCalls.ErrorPathNotFound => JobResult.Fail(id, ErrorCode.SourceNotFound, "The item or the destination is gone."),
        FileSystemCalls.ErrorAccessDenied => side == Side.Destination
            ? JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination cannot be written to.")
            : JobResult.Fail(id, ErrorCode.SourceNotReadable, "The item cannot be read or moved."),
        FileSystemCalls.ErrorSharingViolation or FileSystemCalls.ErrorLockViolation => JobResult.Fail(id, ErrorCode.SourceNotReadable, "The item is in use by another program."),
        FileSystemCalls.ErrorDiskFull or FileSystemCalls.ErrorHandleDiskFull => JobResult.Fail(id, ErrorCode.DiskFull, "The destination disk is full."),
        FileSystemCalls.ErrorInvalidName or FileSystemCalls.ErrorFilenameExcedRange => JobResult.Fail(id, ErrorCode.OutputNameInvalid, "The name is not valid in the destination."),
        FileSystemCalls.ErrorAlreadyExists or FileSystemCalls.ErrorFileExists => JobResult.Fail(id, ErrorCode.NoFreeOutputName, "Every free name is taken."),
        FileSystemCalls.ErrorRequestAborted => JobResult.Fail(id, ErrorCode.Cancelled, "Cancelled; the original is unchanged."),
        6000 => JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "An encrypted file cannot be stored encrypted in the destination."), // ERROR_ENCRYPTION_FAILED
        _ => JobResult.Fail(id, ErrorCode.Internal, $"Windows error {win32}."),
    };

    /// <summary>The Win32 error behind a .NET I/O exception (its HRESULT is 0x8007xxxx).</summary>
    private static int Win32Of(Exception ex) => (ex.HResult & 0xFFFF0000) == 0x80070000 ? ex.HResult & 0xFFFF : ex is UnauthorizedAccessException ? FileSystemCalls.ErrorAccessDenied : -1;
}
