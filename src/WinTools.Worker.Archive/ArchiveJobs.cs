using System.Globalization;
using System.Formats.Tar;
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
                ArchiveCapabilities.DecompressId => Decompress(request),
                _ => JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command."),
            };
        }
        catch (ArchiveFailure ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (TarRefused ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (NotSupportedException) { return JobResult.Fail(id, ErrorCode.UnsupportedContent, "The archive holds an entry of a kind WinTools does not read."); }
        catch (InvalidDataException) { return JobResult.Fail(id, ErrorCode.DecodeFailed, "The archive is damaged."); }
        catch (EndOfStreamException) { return JobResult.Fail(id, ErrorCode.DecodeFailed, "The archive is cut short."); }
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

    private static bool IsLink(ZipArchiveEntry e) => (e.ExternalAttributes & UnixFileTypeMask) == UnixSymlink;

    /// <summary>A Unix file type other than a regular file or a folder (a device, a pipe, a socket).</summary>
    private static bool IsSpecial(ZipArchiveEntry e)
    {
        var type = e.ExternalAttributes & UnixFileTypeMask;
        return type != 0 && type != UnixRegular && type != UnixDirectory && type != UnixSymlink;
    }

    /// <summary>
    /// One entry of any archive format, as the checks see it (V2 PR 5.2): its name and kind, how to read it, and the
    /// check values the format carries (ZIP: CRC-32 and sizes; TAR: none, its GZIP wrapper checks itself at the end).
    /// </summary>
    private sealed record Entry(string Name, bool IsFolder, bool IsLink, bool IsSpecial, bool IsEncrypted, Func<Stream> Open,
        long? Length, uint? Crc, long CompressedLength, DateTime Modified);

    private static IEnumerable<Entry> EntriesOf(ZipArchive zip) => zip.Entries.Select(e => new Entry(e.FullName,
        e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'), IsLink(e), IsSpecial(e), e.IsEncrypted, e.Open, e.Length, e.Crc32, e.CompressedLength,
        e.LastWriteTime.LocalDateTime));

    /// <summary>The archive formats this worker reads, told by their content.</summary>
    private enum Kind { Zip, Tar, TarGz }

    /// <summary>
    /// An open archive of any supported kind. TAR is read as a stream, entry by entry (TarReader), so an entry's data
    /// must be read before the next is asked for; the checks do exactly that. The kind comes from the content (the "PK"
    /// signature, GZIP's 1F 8B, or a valid TAR header), never from the name alone.
    /// </summary>
    private sealed class Source : IDisposable
    {
        private readonly FileStream _file;
        private readonly ZipArchive? _zip;
        private readonly Stream? _tarStream;

        public Kind Kind { get; }
        public long Length => _file.Length;
        public string TypeName => Kind switch { Kind.Zip => "ZIP archive", Kind.Tar => "TAR archive", _ => "TAR.GZ archive" };

        public Source(string path)
        {
            if (Directory.Exists(path)) throw new ArchiveFailure(ErrorCode.IsAFolder, "The source is a folder, not a file.");
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException();
            RefuseOnlineOnly(info);
            if (info.Length > MaxArchiveBytes) throw new ArchiveFailure(ErrorCode.InputTooLarge, $"The archive is larger than {MaxArchiveBytes >> 30} GB.");
            try { _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
            {
                throw new ArchiveFailure(ErrorCode.SourceNotReadable, "The archive could not be read.");
            }
            var head = new byte[512];
            var read = _file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            _file.Position = 0;
            try
            {
                if (read >= 4 && head[0] == 'P' && head[1] == 'K')
                {
                    Kind = Kind.Zip;
                    _zip = new ZipArchive(_file, ZipArchiveMode.Read, leaveOpen: true);
                    if (_zip.Entries.Count > ArchiveLimits.MaxEntries) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive has more than {ArchiveLimits.MaxEntries:N0} entries.");
                }
                else if (read >= 2 && head[0] == 0x1F && head[1] == 0x8B)
                {
                    Kind = Kind.TarGz;
                    // A first pass checks every header and the GZIP trailer; only then does TarReader read it (evaluator, PR 5.2).
                    using (var scan = new CheckedGzip(_file, _file.Length))
                    {
                        TarGuard.Check(scan, _file.Length);
                        if (!scan.TrailerMatches()) throw new TarRefused("The archive is damaged, cut short, or made of several GZIP parts, which WinTools does not read (its GZIP check value does not match).");
                    }
                    _file.Position = 0;
                    _tarStream = new GZipStream(_file, CompressionMode.Decompress, leaveOpen: true);
                }
                else if (read == 512 && head.AsSpan(257, 5).SequenceEqual("ustar"u8))
                {
                    Kind = Kind.Tar;
                    TarGuard.Check(_file, _file.Length);
                    _file.Position = 0;
                    _tarStream = _file;
                }
                else throw new ArchiveFailure(ErrorCode.ContentMismatch, "The file is not a ZIP, TAR or TAR.GZ archive.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IEnumerable<Entry> Entries() => _zip is not null ? EntriesOf(_zip) : TarEntries();

        private IEnumerable<Entry> TarEntries()
        {
            using var reader = new TarReader(_tarStream!, leaveOpen: true);
            var count = 0;
            while (Next(reader) is { } e)
            {
                if (++count > ArchiveLimits.MaxEntries) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive has more than {ArchiveLimits.MaxEntries:N0} entries.");
                var type = e.EntryType;
                var isFile = type is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;
                var isFolder = type == TarEntryType.Directory;
                var isLink = type is TarEntryType.SymbolicLink or TarEntryType.HardLink;
                var name = isFolder && !e.Name.EndsWith('/') ? e.Name + "/" : e.Name;
                var data = e.DataStream;
                yield return new Entry(name, isFolder, isLink, !isFile && !isFolder && !isLink, false,
                    () => data ?? Stream.Null, isFile ? e.Length : 0, null, -1, e.ModificationTime.LocalDateTime);
            }
            // The GZIP wrapper checks its CRC-32 and length only at its end: read what follows the TAR's last entry, bounded.
            if (Kind == Kind.TarGz)
            {
                var rest = new byte[1 << 16];
                long tail = 0;
                int n;
                while ((n = _tarStream!.Read(rest)) > 0)
                    if ((tail += n) > 16L << 20) throw new ArchiveFailure(ErrorCode.LimitExceeded, "The archive holds unexpected data after its last entry.");
            }
        }

        /// <summary>TarReader's own failures on a hostile header (an overflowing PAX number, a bad field) as a damaged archive, never a crash.</summary>
        private static TarEntry? Next(TarReader reader)
        {
            try { return reader.GetNextEntry(copyData: false); }
            catch (Exception ex) when (ex is OverflowException or FormatException or ArgumentException or EndOfStreamException or InvalidDataException or IOException and not FileNotFoundException)
            {
                throw new ArchiveFailure(ErrorCode.DecodeFailed, "A TAR header is damaged.");
            }
            catch (NotSupportedException)
            {
                throw new ArchiveFailure(ErrorCode.UnsupportedContent, "The archive holds an entry of a kind WinTools does not read.");
            }
        }

        public void Dispose()
        {
            _zip?.Dispose();
            if (_tarStream is not null && !ReferenceEquals(_tarStream, _file)) _tarStream.Dispose();
            _file.Dispose();
        }
    }

    private static JobResult Info(string id, string path)
    {
        using var source = new Source(path);
        long files = 0, folders = 0, size = 0, packed = 0, encrypted = 0, links = 0;
        var top = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in source.Entries())
        {
            if (e.IsFolder) folders++; else files++;
            if (e.IsLink) links++;
            if (e.IsEncrypted) encrypted++;
            size += e.Length ?? 0;
            if (e.CompressedLength >= 0) packed += e.CompressedLength;
            // A TAR is read to the end to list it: declared sizes past the limit stop the listing.
            if (size > ArchiveLimits.MaxTotalBytes) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive declares more than {ArchiveLimits.MaxTotalBytes >> 30} GB.");
            var first = e.Name.Replace('\\', '/').TrimStart('/').Split('/')[0];
            if (first.Length > 0) top.Add(first);
        }
        var facts = new List<JobFact>
        {
            new("Type", source.TypeName),
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
    private static Pass ReadAll(IEnumerable<Entry> entries, string? into, long? freeBytes, long archiveBytes, bool ratioRules)
    {
        int files = 0, folders = 0, skipped = 0, renamed = 0;
        long total = 0;
        var problems = new List<string>();
        var budget = new ArchiveLimits.Budget();
        var buffer = new byte[1 << 16];
        void Skip(string why) { skipped++; if (problems.Count < 5) problems.Add(why); }
        foreach (var entry in entries)
        {
            if (entry.IsEncrypted) throw new ArchiveFailure(ErrorCode.PasswordRequired, "The archive has entries protected with a password.");
            if (entry.IsLink || entry.IsSpecial) { Skip("a link or device entry"); continue; } // never followed, never created
            var verdict = ArchiveEntryPolicy.Judge(entry.Name);
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
                    if (ratioRules && entry.CompressedLength >= 0 && ArchiveLimits.IsBomb(written, entry.CompressedLength)) throw new ArchiveFailure(ErrorCode.LimitExceeded, "An entry expands far beyond its packed size; it looks like an archive bomb.");
                    if (ratioRules && ArchiveLimits.IsArchiveBomb(total, archiveBytes)) throw new ArchiveFailure(ErrorCode.LimitExceeded, "The archive expands far beyond its own size; it looks like an archive bomb.");
                    if (total > ArchiveLimits.MaxTotalBytes) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive expands to more than {ArchiveLimits.MaxTotalBytes >> 30} GB.");
                    if (freeBytes is { } free && total > free - ArchiveLimits.FreeSpaceReserve) throw new ArchiveFailure(ErrorCode.DiskFull, "Extracting would leave less than 2 GB free on the drive.");
                    crc.Append(buffer.AsSpan(0, n));
                    output?.Write(buffer, 0, n);
                }
            }
            if ((entry.Length is { } length && written != length) || (entry.Crc is { } expected && crc.Value != expected))
                throw new ArchiveFailure(ErrorCode.DecodeFailed, "An entry is damaged (its check value does not match).");
            if (target is not null) File.SetLastWriteTime(target, ClampDate(entry.Modified));
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
        using var source = new Source(path);
        var pass = ReadAll(source.Entries(), into: null, freeBytes: null, source.Length, ratioRules: true);
        var facts = new List<JobFact>
        {
            new("Result", source.Kind switch
            {
                Kind.Tar => "No errors: every header checksum matches (TAR keeps no check values for the data)",
                Kind.TarGz => "No errors: every header checksum and the GZIP check value match",
                _ => "No errors: every file's check value matches",
            }),
            new("Checked", $"{pass.Files:N0} files, {FeedbackText.Size(pass.Bytes)}"),
        };
        if (pass.Skipped > 0) facts.Add(new("Not checked", $"{pass.Skipped:N0} entries (links, devices or unsafe names)", Notice: true));
        return JobResult.WithFacts(id, facts);
    }

    /// <summary>
    /// Decompress a single GZIP file (V2 PR 5.2): "notes.txt.gz" becomes "notes.txt" next to it, through an exclusive
    /// temporary file, with the bytes counted against the size, ratio and free-space limits as they come out. GZIP checks
    /// its own CRC-32 and length at the end; a mismatch fails the job and nothing is kept.
    /// </summary>
    private static JobResult Decompress(JobRequest request)
    {
        var path = request.Inputs[0];
        var dest = Destination(request, path);
        if (Directory.Exists(path)) throw new ArchiveFailure(ErrorCode.IsAFolder, "The source is a folder, not a file.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        RefuseOnlineOnly(info);
        if (info.Length > MaxArchiveBytes) throw new ArchiveFailure(ErrorCode.InputTooLarge, $"The file is larger than {MaxArchiveBytes >> 30} GB.");
        var zone = MarkOfTheWeb.ZoneToCarry(path);
        long? free = null;
        try { free = new DriveInfo(Path.GetPathRoot(dest.FullName)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        var temp = ArchiveCapabilities.TempPath(request.Id, request.Inputs, request.OutputDirectory);
        var committed = false;
        try
        {
            string hash;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16))
            {
                var head = new byte[2];
                if (file.ReadAtLeast(head, 2, throwOnEndOfStream: false) < 2 || head[0] != 0x1F || head[1] != 0x8B)
                    throw new ArchiveFailure(ErrorCode.ContentMismatch, "The file is not GZIP-compressed.");
                file.Position = 0;
                using var gzip = new CheckedGzip(file, file.Length);
                using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                File.SetAttributes(temp, FileAttributes.Hidden);
                var buffer = new byte[1 << 16];
                long total = 0;
                int n;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while ((n = gzip.Read(buffer)) > 0)
                {
                    total += n;
                    if (ArchiveLimits.IsArchiveBomb(total, info.Length)) throw new ArchiveFailure(ErrorCode.LimitExceeded, "The file expands far beyond its own size; it looks like a compression bomb.");
                    if (total > ArchiveLimits.MaxTotalBytes) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The file expands to more than {ArchiveLimits.MaxTotalBytes >> 30} GB.");
                    if (free is { } f && total > f - ArchiveLimits.FreeSpaceReserve) throw new ArchiveFailure(ErrorCode.DiskFull, "Decompressing would leave less than 2 GB free on the drive.");
                    output.Write(buffer, 0, n);
                    sha.AppendData(buffer, 0, n);
                }
                output.Flush(flushToDisk: true);
                // GZipStream reports a cut-off file as a clean end: the trailer decides (evaluator, PR 5.2, blocker).
                if (!gzip.TrailerMatches()) throw new ArchiveFailure(ErrorCode.DecodeFailed, "The file is damaged, cut short, or made of several GZIP parts, which WinTools does not read (its GZIP check value does not match); nothing was kept.");
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (zone is { } z) MarkOfTheWeb.Apply(temp, z);
            File.SetAttributes(temp, FileAttributes.Normal);
            foreach (var candidate in ArchiveCapabilities.Candidates(ArchiveCapabilities.DecompressId, request.Inputs, request.OutputDirectory))
            {
                try
                {
                    File.Move(temp, candidate, overwrite: false);
                    committed = true;
                    return JobResult.Ok(request.Id, new JobOutput(candidate, hash));
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

    private static JobResult Extract(JobRequest request)
    {
        var path = request.Inputs[0];
        var dest = Destination(request, path);
        using var source = new Source(path);
        // A ZIP's directory is read before anything is written, so a damaged one fails here with nothing to clean.
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
            var pass = ReadAll(source.Entries(), staging, free, source.Length, ratioRules: true);
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
                    if (pass.Skipped > 0) facts.Add(new("Skipped", $"{pass.Skipped:N0} entries not extracted (links, devices, unsafe or clashing names)", Notice: true));
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
        if (items.Count + folderNames.Count > ArchiveLimits.MaxEntries) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"More than {ArchiveLimits.MaxEntries:N0} files and folders.");
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
                        // Shared for writing too: Office and loggers keep files open that way without writing. Whether the file
                        // changed while it was read is checked instead, and only that fails the job (second review, PR 5.1).
                        item.File.Refresh();
                        var (length, written) = (item.File.Length, item.File.LastWriteTimeUtc);
                        Stream source;
                        try { source = new FileStream(item.File.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan); }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                        {
                            throw new ArchiveFailure(ErrorCode.SourceNotReadable, $"{item.Name} could not be read; nothing was saved.");
                        }
                        using (source)
                        {
                            var entry = zip.CreateEntry(item.Name, CompressionLevel.Optimal);
                            entry.LastWriteTime = item.File.LastWriteTime.Year is < 1980 or > 2107 ? DateTimeOffset.Now : item.File.LastWriteTime;
                            using var target = entry.Open();
                            source.CopyTo(target);
                        }
                        item.File.Refresh();
                        if (!item.File.Exists || item.File.Length != length || item.File.LastWriteTimeUtc != written)
                            throw new ArchiveFailure(ErrorCode.SourceNotReadable, $"{item.Name} changed while it was being packed; nothing was saved. Try again.");
                        zone = Strictest(zone, MarkOfTheWeb.ZoneToCarry(item.File.FullName));
                    }
                }
                fs.Flush(flushToDisk: true);
                // Read back: the archive must open and every file decompress to its recorded check value. The bomb rules do
                // not apply: these are the user's own files, which may be highly compressible.
                fs.Position = 0;
                using var check = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
                var pass = ReadAll(EntriesOf(check), into: null, freeBytes: null, fs.Length, ratioRules: false);
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
        // The name's parts are what an extraction will judge: no file deeper than an archive may hold (second review, PR 5.1).
        if (name.Count(c => c == '/') + 1 > ArchiveEntryPolicy.MaxDepth) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"A file is more than {ArchiveEntryPolicy.MaxDepth} levels deep.");
        if (items.Count + 1 > ArchiveLimits.MaxEntries) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"More than {ArchiveLimits.MaxEntries:N0} files."); // folders are counted once listed
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
