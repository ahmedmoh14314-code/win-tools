using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Worker.Archive;

/// <summary>
/// The Archives module's jobs, ZIP first (V2 PR 5.1, ADR-0023), on .NET's own System.IO.Compression: no third-party code.
/// <list type="bullet">
/// <item><b>Extract</b>: every entry name passes <see cref="ArchiveEntryPolicy"/> and the final path must stay inside the
/// new folder. Links, devices and encrypted entries are skipped or refused. Bytes are counted as they decompress, against
/// the entry count, total size, ratio and free-space limits (<see cref="ArchiveLimits"/>). Each file's CRC-32 is checked
/// (.NET does not check it on read). Everything goes into an exclusive staging folder in the destination, which takes its
/// final name only when the whole archive succeeded; nothing is ever written outside it, overwritten, or opened.</item>
/// <item><b>Test</b>: the same reading and checks, with nothing written.</item>
/// <item><b>Info</b>: counts and sizes from the archive's directory, nothing decompressed.</item>
/// <item><b>Add to ZIP</b>: the selected files and folders (links and online-only files skipped, never followed or
/// downloaded) into an exclusive temporary file, read back and tested, then given its final name.</item>
/// </list>
/// Every output carries the strictest Mark-of-the-Web zone of its sources.
/// </summary>
internal static class ArchiveJobs
{
    public const long MaxArchiveBytes = 4L << 30;

    private const int ErrorFileExists = unchecked((int)0x80070050), ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const int ErrorDiskFull = unchecked((int)0x80070070), ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;
    private const int UnixFileTypeMask = 0xF000 << 16, UnixSymlink = 0xA000 << 16, UnixRegular = 0x8000 << 16, UnixDirectory = 0x4000 << 16;

    private sealed class ArchiveFailure(ErrorCode code, string message) : Exception(message)
    {
        public ErrorCode Code { get; } = code;
    }

    public static JobResult Run(JobRequest request)
    {
        var id = request.Id;
        if (id is not { Length: > 0 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return JobResult.Fail("invalid", ErrorCode.InvalidRequest, "Request id must be 1-64 ASCII letters or digits.");
        if (request.Parameters is { Count: > 0 }) return JobResult.Fail(id, ErrorCode.InvalidRequest, "This command takes no parameters.");
        if (request.Inputs.Count == 0 || !request.Inputs.All(PathPolicy.IsSupported))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "Every input must be a supported path.");
        if (request.OutputDirectory is { } od && !PathPolicy.IsSupported(Path.Combine(od, "x.zip")))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        try
        {
            if (request.CapabilityId != ArchiveCapabilities.ZipId && (request.Inputs.Count != 1 || Path.EndsInDirectorySeparator(request.Inputs[0])))
                return JobResult.Fail(id, ErrorCode.InvalidRequest, "One archive per request.");
            return request.CapabilityId switch
            {
                ArchiveCapabilities.InfoId => Info(id, request.Inputs[0]),
                ArchiveCapabilities.TestId => Test(id, request.Inputs[0]),
                ArchiveCapabilities.ExtractId => Extract(request),
                ArchiveCapabilities.ZipId => Zip(request),
                _ => JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command."),
            };
        }
        catch (ArchiveFailure ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (InvalidDataException) { return JobResult.Fail(id, ErrorCode.DecodeFailed, "The archive is damaged or is not a ZIP file."); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (UnauthorizedAccessException) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (PathTooLongException) { return JobResult.Fail(id, ErrorCode.OutputNameInvalid, "A name would be too long."); }
        catch (IOException ex) when (ex.HResult is ErrorDiskFull or ErrorHandleDiskFull) { return JobResult.Fail(id, ErrorCode.DiskFull, "The destination drive is full."); }
        catch (IOException ex) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, $"Writing failed (0x{ex.HResult:X8})."); }
    }

    private static void RefuseOnlineOnly(FileSystemInfo item)
    {
        if ((item.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            throw new ArchiveFailure(ErrorCode.OnlineOnly, "The file is online only; reading it would download it.");
    }

    /// <summary>Opens a ZIP for reading: a local file, at most <see cref="MaxArchiveBytes"/>, with the "PK" signature.</summary>
    private static ZipArchive OpenZip(string path, out FileStream stream)
    {
        if (Directory.Exists(path)) throw new ArchiveFailure(ErrorCode.IsAFolder, "The source is a folder, not a file.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        RefuseOnlineOnly(info);
        if (info.Length > MaxArchiveBytes) throw new ArchiveFailure(ErrorCode.InputTooLarge, $"The archive is larger than {MaxArchiveBytes >> 30} GB.");
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new ArchiveFailure(ErrorCode.SourceNotReadable, "The archive could not be read.");
        }
        var head = new byte[4];
        if (stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4 || head[0] != 'P' || head[1] != 'K')
        {
            stream.Dispose();
            throw new ArchiveFailure(ErrorCode.ContentMismatch, "The file is not a ZIP archive.");
        }
        stream.Position = 0;
        try
        {
            var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count > ArchiveLimits.MaxEntries)
            {
                zip.Dispose();
                throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive has more than {ArchiveLimits.MaxEntries:N0} entries.");
            }
            return zip;
        }
        catch (InvalidDataException) { stream.Dispose(); throw; }
    }

    private static bool IsLink(ZipArchiveEntry e) => (e.ExternalAttributes & UnixFileTypeMask) == UnixSymlink;

    /// <summary>A Unix file type other than a regular file or a folder (a device, a pipe, a socket).</summary>
    private static bool IsSpecial(ZipArchiveEntry e)
    {
        var type = e.ExternalAttributes & UnixFileTypeMask;
        return type != 0 && type != UnixRegular && type != UnixDirectory && type != UnixSymlink;
    }

    private static JobResult Info(string id, string path)
    {
        using var zip = OpenZip(path, out _);
        long files = 0, folders = 0, size = 0, packed = 0, encrypted = 0, links = 0;
        var top = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            var isFolder = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
            if (isFolder) folders++; else files++;
            if (IsLink(e)) links++;
            if (e.IsEncrypted) encrypted++;
            size += e.Length;
            packed += e.CompressedLength;
            var first = e.FullName.Replace('\\', '/').TrimStart('/').Split('/')[0];
            if (first.Length > 0) top.Add(first);
        }
        var facts = new List<JobFact>
        {
            new("Type", "ZIP archive"),
            new("Files", files.ToString("N0", CultureInfo.InvariantCulture)),
            new("Folders", folders.ToString("N0", CultureInfo.InvariantCulture)),
            new("Size", $"{FeedbackText.Size(size)} unpacked, {FeedbackText.Size(new FileInfo(path).Length)} packed"),
            new("Top level", top.Count == 1 ? $"One folder or file: {Truncate(top.Single(), 60)}" : $"{top.Count:N0} items"),
        };
        if (encrypted > 0) facts.Add(new("Protection", $"{encrypted:N0} entries need a password", Notice: true));
        if (links > 0) facts.Add(new("Links", $"{links:N0} links (never extracted)", Notice: true));
        // Judged against the archive's own size too: declared sizes can lie and entries can share data (evaluator, PR 5.1).
        if ((packed > 0 && size / Math.Max(packed, 1) > ArchiveLimits.MaxRatio) || ArchiveLimits.IsArchiveBomb(size, new FileInfo(path).Length))
            facts.Add(new("Ratio", "Unusually high compression; it may be an archive bomb", Notice: true));
        return JobResult.WithFacts(id, facts);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>What one pass over an archive's entries found.</summary>
    private sealed record Pass(int Files, int Folders, long Bytes, int Skipped, int Renamed, List<string> Problems);

    /// <summary>
    /// Reads every entry once, checking names, kinds, limits and CRC-32, and writes each file into
    /// <paramref name="into"/> when it is given (Extract), or nowhere (Test and Add to ZIP's read-back).
    /// <paramref name="ratioRules"/>: the bomb rules apply to an archive from elsewhere, not to one just made from the user's
    /// own files, which may be zeros (evaluator, PR 5.1). Everything created counts against one budget, folders included.
    /// </summary>
    private static Pass ReadAll(ZipArchive zip, string? into, long? freeBytes, long archiveBytes, bool ratioRules)
    {
        int files = 0, folders = 0, skipped = 0, renamed = 0;
        long total = 0;
        var problems = new List<string>();
        var budget = new ArchiveLimits.Budget();
        var buffer = new byte[1 << 16];
        void Skip(string why) { skipped++; if (problems.Count < 5) problems.Add(why); }
        foreach (var entry in zip.Entries)
        {
            if (entry.IsEncrypted) throw new ArchiveFailure(ErrorCode.PasswordRequired, "The archive has entries protected with a password.");
            if (IsLink(entry) || IsSpecial(entry)) { Skip("a link or device entry"); continue; } // never followed, never created
            var verdict = ArchiveEntryPolicy.Judge(entry.FullName);
            if (verdict.Refusal is not null) { Skip(verdict.Refusal); continue; }
            if (verdict.Renamed) renamed++;
            var relative = verdict.RelativePath!;
            if (verdict.IsFolder)
            {
                if (!budget.AddFolder(relative))
                {
                    if (budget.Created >= ArchiveLimits.MaxEntries) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive would create more than {ArchiveLimits.MaxEntries:N0} files and folders.");
                    Skip("a folder with the name of a file"); continue;
                }
                if (into is not null)
                {
                    var dir = Path.Combine(into, relative);
                    if (!ArchiveEntryPolicy.IsInside(into, dir)) { Skip("outside the folder"); continue; }
                    Directory.CreateDirectory(dir);
                }
                folders++;
                continue;
            }
            // Two entries that are one name on Windows ("A.txt", "a.txt"): the second gets "a (1).txt".
            relative = Unique(relative, budget);
            if (!budget.AddFile(relative))
            {
                if (budget.Created >= ArchiveLimits.MaxEntries) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive would create more than {ArchiveLimits.MaxEntries:N0} files and folders.");
                Skip("a file with the name of a folder"); continue;
            }
            string? target = null;
            if (into is not null)
            {
                target = Path.Combine(into, relative);
                if (!ArchiveEntryPolicy.IsInside(into, target)) { Skip("outside the folder"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            }
            var crc = new Crc32();
            long written = 0;
            using (var input = entry.Open())
            using (var output = target is null ? null : new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                int n;
                while ((n = input.Read(buffer)) > 0)
                {
                    written += n;
                    total += n;
                    if (ratioRules && ArchiveLimits.IsBomb(written, entry.CompressedLength)) throw new ArchiveFailure(ErrorCode.LimitExceeded, "An entry expands far beyond its packed size; it looks like an archive bomb.");
                    if (ratioRules && ArchiveLimits.IsArchiveBomb(total, archiveBytes)) throw new ArchiveFailure(ErrorCode.LimitExceeded, "The archive expands far beyond its own size; it looks like an archive bomb.");
                    if (total > ArchiveLimits.MaxTotalBytes) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive expands to more than {ArchiveLimits.MaxTotalBytes >> 30} GB.");
                    if (freeBytes is { } free && total > free - ArchiveLimits.FreeSpaceReserve) throw new ArchiveFailure(ErrorCode.DiskFull, "Extracting would leave less than 2 GB free on the drive.");
                    crc.Append(buffer.AsSpan(0, n));
                    output?.Write(buffer, 0, n);
                }
            }
            if (written != entry.Length || crc.Value != entry.Crc32)
                throw new ArchiveFailure(ErrorCode.DecodeFailed, "An entry is damaged (its check value does not match).");
            if (target is not null) File.SetLastWriteTime(target, ClampDate(entry.LastWriteTime.LocalDateTime));
            files++;
        }
        return new Pass(files, folders, total, skipped, renamed, problems);
    }

    private static DateTime ClampDate(DateTime d) => d.Year is < 1980 or > 2107 ? DateTime.Now : d;

    private static string Unique(string relative, ArchiveLimits.Budget budget)
    {
        if (!budget.IsFile(relative)) return relative;
        var dir = Path.GetDirectoryName(relative) ?? "";
        var stem = Path.GetFileNameWithoutExtension(relative);
        var ext = Path.GetExtension(relative);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!budget.IsFile(candidate)) return candidate;
        }
    }

    private static JobResult Test(string id, string path)
    {
        using var zip = OpenZip(path, out _);
        var pass = ReadAll(zip, into: null, freeBytes: null, new FileInfo(path).Length, ratioRules: true);
        var facts = new List<JobFact>
        {
            new("Result", "No errors: every file's check value matches"),
            new("Checked", $"{pass.Files:N0} files, {FeedbackText.Size(pass.Bytes)}"),
        };
        if (pass.Skipped > 0) facts.Add(new("Not checked", $"{pass.Skipped:N0} entries (links, devices or unsafe names)", Notice: true));
        return JobResult.WithFacts(id, facts);
    }

    private static JobResult Extract(JobRequest request)
    {
        var path = request.Inputs[0];
        var dest = Destination(request, path);
        using var zip = OpenZip(path, out _);
        // The archive is read before anything is written, so a damaged directory fails here with nothing to clean.
        var zone = MarkOfTheWeb.ZoneToCarry(path);
        var staging = ArchiveCapabilities.TempPath(request.Id, request.Inputs, request.OutputDirectory);
        switch (ExclusiveDirectory.Create(staging))
        {
            case CreateOutcome.Created: break;
            case CreateOutcome.NotWritable: throw new ArchiveFailure(ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable.");
            default: throw new ArchiveFailure(ErrorCode.Internal, "Something else is using this job's folder in the destination.");
        }
        var committed = false;
        try
        {
            File.SetAttributes(staging, FileAttributes.Hidden | FileAttributes.Directory);
            long? free = null;
            try { free = new DriveInfo(Path.GetPathRoot(dest.FullName)!).AvailableFreeSpace; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            var pass = ReadAll(zip, staging, free, new FileInfo(path).Length, ratioRules: true);
            if (pass.Files == 0 && pass.Folders == 0)
                return JobResult.Fail(request.Id, ErrorCode.NothingToDo, pass.Skipped > 0 ? "Every entry was unsafe to extract." : "The archive is empty.");
            if (zone is { } z)
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)) MarkOfTheWeb.Apply(file, z);
            File.SetAttributes(staging, FileAttributes.Directory);
            var shape = FolderManifest.ShapeOf(staging) ?? throw new ArchiveFailure(ErrorCode.OutputValidationFailed, "The extracted folder did not check out; nothing was kept.");
            foreach (var candidate in ArchiveCapabilities.Candidates(ArchiveCapabilities.ExtractId, request.Inputs, request.OutputDirectory))
            {
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                if (MoveFolder(staging, candidate))
                {
                    committed = true;
                    var facts = new List<JobFact> { new("Files", pass.Files.ToString("N0", CultureInfo.InvariantCulture)) };
                    if (pass.Skipped > 0) facts.Add(new("Skipped", $"{pass.Skipped:N0} entries not extracted (links, devices or unsafe names)", Notice: true));
                    if (pass.Renamed > 0) facts.Add(new("Renamed", $"{pass.Renamed:N0} names changed to be valid on Windows", Notice: true));
                    return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, shape)], facts);
                }
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free folder name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) DeleteFolder(staging);
        }
    }

    /// <summary>One file Add to ZIP will pack, under the name it gets inside the archive.</summary>
    private sealed record ZipItem(FileInfo File, string Name);

    /// <summary>
    /// Add to ZIP (evaluator, PR 5.1). Everything requested is listed first, before anything is written: a file that is
    /// online only, a folder that cannot be read, a tree deeper than <see cref="ArchiveEntryPolicy.MaxDepth"/>, more than
    /// <see cref="ArchiveLimits.MaxEntries"/> files or more than <see cref="ArchiveLimits.MaxTotalBytes"/> fail the job with
    /// the reason; no archive silently lacks a file the user asked for. Only real links (symbolic links, junctions) are
    /// left out, and the user is told how many; OneDrive's files carry a reparse point too and are packed like any other.
    /// Files are read shared for reading only, so one being written fails the job instead of being packed torn.
    /// </summary>
    private static JobResult Zip(JobRequest request)
    {
        Destination(request, Path.TrimEndingDirectorySeparator(request.Inputs[0]));
        var temp = ArchiveCapabilities.TempPath(request.Id, request.Inputs, request.OutputDirectory);
        var items = new List<ZipItem>();
        var folderNames = new List<string>();
        var links = 0;
        long total = 0;
        foreach (var input in request.Inputs)
        {
            var item = Path.TrimEndingDirectorySeparator(input);
            if (Directory.Exists(item))
            {
                var root = new DirectoryInfo(item);
                if (root.LinkTarget is not null) { links++; continue; }
                List(root, root.Name, 1, items, folderNames, ref links, ref total, temp);
            }
            else
            {
                var file = new FileInfo(item);
                if (!file.Exists) throw new FileNotFoundException();
                if (file.LinkTarget is not null) { links++; continue; }
                Add(file, file.Name, items, ref total, temp);
            }
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
            if (!names.Add(i.Name)) throw new ArchiveFailure(ErrorCode.InvalidRequest, $"Two selected items would have the same name in the archive ({i.Name}).");
        if (items.Count == 0 && folderNames.Count == 0) return JobResult.Fail(request.Id, ErrorCode.NothingToDo, "There was nothing that could be added (links are not followed).");

        int? zone = null;
        var committed = false;
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                File.SetAttributes(temp, FileAttributes.Hidden);
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var folder in folderNames) zip.CreateEntry(folder + "/");
                    foreach (var item in items)
                    {
                        Stream source;
                        try { source = new FileStream(item.File.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan); }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                        {
                            throw new ArchiveFailure(ErrorCode.SourceNotReadable, $"{item.Name} could not be read (it may be open in another program); nothing was saved.");
                        }
                        using (source)
                        {
                            var entry = zip.CreateEntry(item.Name, CompressionLevel.Optimal);
                            entry.LastWriteTime = item.File.LastWriteTime.Year is < 1980 or > 2107 ? DateTimeOffset.Now : item.File.LastWriteTime;
                            using var target = entry.Open();
                            source.CopyTo(target);
                        }
                        zone = Strictest(zone, MarkOfTheWeb.ZoneToCarry(item.File.FullName));
                    }
                }
                fs.Flush(flushToDisk: true);
                // Read back: the archive must open and every file decompress to its recorded check value. The bomb rules do
                // not apply: these are the user's own files, which may be highly compressible.
                fs.Position = 0;
                using var check = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
                var pass = ReadAll(check, into: null, freeBytes: null, fs.Length, ratioRules: false);
                if (pass.Files != items.Count || pass.Skipped > 0) throw new ArchiveFailure(ErrorCode.OutputValidationFailed, "The new archive did not check out; nothing was saved.");
            }
            if (zone is { } z) MarkOfTheWeb.Apply(temp, z);
            File.SetAttributes(temp, FileAttributes.Normal);
            string hash;
            using (var read = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read)) hash = Convert.ToHexStringLower(SHA256.HashData(read));
            IReadOnlyList<JobFact> facts = links > 0 ? [new JobFact("Skipped", $"{links:N0} links not followed", Notice: true)] : [];
            foreach (var candidate in ArchiveCapabilities.Candidates(ArchiveCapabilities.ZipId, request.Inputs, request.OutputDirectory))
            {
                try
                {
                    File.Move(temp, candidate, overwrite: false);
                    committed = true;
                    return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, hash)], facts);
                }
                catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists) { }
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free output name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) DeleteFile(temp);
        }
    }

    private static int? Strictest(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static void List(DirectoryInfo dir, string prefix, int depth, List<ZipItem> items, List<string> folders, ref int links, ref long total, string temp)
    {
        if (depth > ArchiveEntryPolicy.MaxDepth) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The folder is more than {ArchiveEntryPolicy.MaxDepth} levels deep.");
        folders.Add(prefix);
        FileSystemInfo[] children;
        try { children = dir.GetFileSystemInfos(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new ArchiveFailure(ErrorCode.SourceNotReadable, $"The folder {prefix} could not be read; nothing was saved.");
        }
        foreach (var child in children)
        {
            if (child.LinkTarget is not null) { links++; continue; } // a symbolic link or junction: never followed
            var name = prefix + "/" + child.Name;
            if (child is DirectoryInfo d) List(d, name, depth + 1, items, folders, ref links, ref total, temp);
            else if (child is FileInfo f) Add(f, name, items, ref total, temp);
        }
    }

    private static void Add(FileInfo file, string name, List<ZipItem> items, ref long total, string temp)
    {
        if (string.Equals(file.FullName, temp, StringComparison.OrdinalIgnoreCase)) return; // the archive being written
        if (file.Name.StartsWith("~wintools-", StringComparison.Ordinal) && file.Name.EndsWith(".tmp", StringComparison.Ordinal)) return; // another job's temporary
        if ((file.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            throw new ArchiveFailure(ErrorCode.OnlineOnly, $"{name} is online only; open it once so Windows downloads it, then try again.");
        if (items.Count + 1 > ArchiveLimits.MaxEntries) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"More than {ArchiveLimits.MaxEntries:N0} files.");
        total += file.Length;
        if (total > ArchiveLimits.MaxTotalBytes) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"More than {ArchiveLimits.MaxTotalBytes >> 30} GB in all.");
        items.Add(new ZipItem(file, name));
    }

    /// <summary>The destination folder, which must exist and must not be, or be inside, a link or junction.</summary>
    private static DirectoryInfo Destination(JobRequest request, string source)
    {
        var dir = new DirectoryInfo(request.OutputDirectory ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source))!);
        if (!dir.Exists)
            throw request.OutputDirectory is null
                ? new ArchiveFailure(ErrorCode.SourceNotFound, "The source folder no longer exists.")
                : new ArchiveFailure(ErrorCode.OutputDirectoryNotWritable, "The configured output folder does not exist.");
        for (var d = dir; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) throw new ArchiveFailure(ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");
        return dir;
    }

    private static bool MoveFolder(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.Move(from, to); return true; }
            catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists) { return false; }
            // An antivirus or indexer looking at the new files holds them for a moment: back off for up to about 11 s.
            catch (Exception ex) when (attempt < 10 && ex is UnauthorizedAccessException or IOException) { Thread.Sleep(200 * attempt); }
        }
    }

    private static void DeleteFile(string path)
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }

    private static void DeleteFolder(string path)
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }
}

/// <summary>CRC-32 (ISO-HDLC, the ZIP check value), table-driven; .NET's own lives in a separate package.</summary>
internal sealed class Crc32
{
    private static readonly uint[] Table = Build();
    private uint _crc = 0xFFFFFFFF;

    public uint Value => ~_crc;

    public void Append(ReadOnlySpan<byte> data)
    {
        var crc = _crc;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
