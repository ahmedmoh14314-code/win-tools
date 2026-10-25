using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Worker.Pdf;

/// <summary>
/// The PDF module's jobs (V2 PR 2.1 and 2.3, ADR-0022), with the output-safety rules every module follows (ADR-0005).
/// <list type="bullet">
/// <item><b>qpdf never sees a user's path.</b> Each source is copied, read-only and bounded, into a private work folder
/// under the user's temp folder with a fixed ASCII name (<c>in-001.pdf</c>), and qpdf runs there with that folder as its
/// working directory and relative names only. A long path, a "%d" or "@" in any folder or file name, or a source that
/// changes meanwhile cannot reach it (evaluator, PR 2.1).</item>
/// <item>What qpdf writes is checked in the work folder (it opens, it has the expected pages). Only then is it copied to
/// an exclusive temporary file (Split: an exclusive staging folder) in the destination, marked with the source's zone,
/// and given its final name with a no-overwrite move.</item>
/// <item>Any failure removes what was written, and the work folder always goes. A PDF that needs a password is refused.</item>
/// </list>
/// </summary>
internal static class PdfJobs
{
    public const long MaxInputBytes = 1L << 30;

    /// <summary>One qpdf run; the host's per-job limit is longer, so a stuck qpdf is ended here with a clear reason.</summary>
    internal static TimeSpan QpdfTimeout { get; set; } = TimeSpan.FromMinutes(4);

    private const int ErrorFileExists = unchecked((int)0x80070050), ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const int ErrorDiskFull = unchecked((int)0x80070070), ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorSharingViolation = unchecked((int)0x80070020), ErrorAccessDenied = unchecked((int)0x80070005);
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;

    private sealed class PdfFailure(ErrorCode code, string message) : Exception(message)
    {
        public ErrorCode Code { get; } = code;
    }

    /// <summary>The private folder one request's qpdf runs in; the host removes it too if the worker is killed.</summary>
    public static string WorkFolderOf(string requestId) => Path.Combine(Path.GetTempPath(), PdfCapabilities.WorkFolderName(requestId));

    /// <summary>The most a Merge may read in all (each input is also limited to <see cref="MaxInputBytes"/>).</summary>
    public const long MaxTotalInputBytes = 2L << 30;

    /// <summary>Free space the temp drive keeps after a job's copies and qpdf's output (second review, PR 2.1).</summary>
    public const long TempFreeSpaceReserve = 2L << 30;

    /// <summary>A work folder this old is a leftover: no job runs anywhere near this long (the host stops one at 10 minutes).</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    /// <summary>
    /// Removes work folders an earlier worker could not (its host died, or an antivirus held a file): they hold copies of
    /// the user's documents. Only folders with this module's name pattern and older than <see cref="StaleAfter"/>, so a
    /// job running in another worker is never touched (second review, PR 2.1).
    /// </summary>
    internal static void SweepStaleWorkFolders()
    {
        try
        {
            foreach (var dir in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories(PdfCapabilities.WorkFolderName("*")))
            {
                var id = dir.Name[PdfCapabilities.WorkFolderName("").Length..];
                if (id.Length is 0 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c)) || dir.LinkTarget is not null) continue;
                if (DateTime.UtcNow - dir.CreationTimeUtc > StaleAfter) DeleteFolder(dir.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static JobResult Run(JobRequest request)
    {
        var id = request.Id;
        if (id is not { Length: > 0 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return JobResult.Fail("invalid", ErrorCode.InvalidRequest, "Request id must be 1-64 ASCII letters or digits.");
        if (request.Parameters is { Count: > 0 }) return JobResult.Fail(id, ErrorCode.InvalidRequest, "This command takes no parameters.");
        if (request.Inputs.Count == 0 || !request.Inputs.All(PathPolicy.IsSupported) || request.Inputs.Any(Path.EndsInDirectorySeparator))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "Every input must be a supported file path.");
        if (request.OutputDirectory is { } od && !PathPolicy.IsSupported(Path.Combine(od, "x.pdf")))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        if (!Qpdf.IsPresent) return JobResult.Fail(id, ErrorCode.Internal, "The PDF engine is missing from the WinTools folder. Reinstall WinTools.");
        var single = request.CapabilityId != PdfCapabilities.MergeId;
        if (single && request.Inputs.Count != 1) return JobResult.Fail(id, ErrorCode.InvalidRequest, "One file per request.");
        SweepStaleWorkFolders();
        var work = WorkFolderOf(id);
        // Created exclusively: a folder already under this name is not ours, and is neither used nor deleted.
        switch (ExclusiveDirectory.Create(work))
        {
            case CreateOutcome.Created: break;
            case CreateOutcome.NotWritable: return JobResult.Fail(id, ErrorCode.Internal, "The temporary folder cannot be written to.");
            default: return JobResult.Fail(id, ErrorCode.Internal, "Something else is using this job's work folder.");
        }
        try
        {
            EnsureTempSpace(request, work);
            return request.CapabilityId switch
            {
                PdfCapabilities.InfoId => Info(id, request.Inputs[0], work),
                PdfCapabilities.MergeId => Merge(request, work),
                PdfCapabilities.SplitId => Split(request, work),
                PdfCapabilities.OptimizeId => Optimize(request, work),
                PdfCapabilities.LinearizeId => Linearize(request, work),
                PdfCapabilities.RepairId => Repair(request, work),
                _ when PdfCapabilities.RotationOf(request.CapabilityId) is { } angle => Rotate(request, angle, work),
                _ => JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command."),
            };
        }
        catch (PdfFailure ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (UnauthorizedAccessException) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (PathTooLongException) { return JobResult.Fail(id, ErrorCode.OutputNameInvalid, "The output name would be too long."); }
        catch (IOException ex) when (ex.HResult is ErrorDiskFull or ErrorHandleDiskFull) { return JobResult.Fail(id, ErrorCode.DiskFull, "The drive is full."); }
        catch (IOException ex) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, $"Writing the output failed (0x{ex.HResult:X8})."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return JobResult.Fail(id, ErrorCode.Internal, "The PDF engine could not be started."); }
        finally
        {
            DeleteFolder(work);
        }
    }

    /// <summary>
    /// The copies and qpdf's output need about twice the inputs on the temp drive. That is checked before anything is
    /// copied, with <see cref="TempFreeSpaceReserve"/> left over, so a job never fills the drive for everything else; and a
    /// Merge reads at most <see cref="MaxTotalInputBytes"/> in all (second review, PR 2.1).
    /// </summary>
    private static void EnsureTempSpace(JobRequest request, string work)
    {
        long total = 0;
        foreach (var input in request.Inputs)
        {
            var info = new FileInfo(input);
            if (info.Exists) total += info.Length;
        }
        if (total > MaxTotalInputBytes && request.CapabilityId == PdfCapabilities.MergeId)
            throw new PdfFailure(ErrorCode.LimitExceeded, $"Merge reads at most {MaxTotalInputBytes >> 30} GB in all; these PDFs are larger.");
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(work)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return; } // cannot ask: the copy itself reports a full drive
        if (free - 2 * total < TempFreeSpaceReserve)
            throw new PdfFailure(ErrorCode.DiskFull, $"The temporary drive needs about {FeedbackText.Size(2 * total + TempFreeSpaceReserve)} free for this; free some space and try again.");
    }

    /// <summary>One source, copied into the work folder: its own path (for names and zone), the copy qpdf reads, facts.</summary>
    private sealed record Source(string Path, string Copy, string Version, long Length, int? Zone);

    /// <summary>
    /// Checks one source and copies it into the work folder as <paramref name="copyName"/>: a file (not a folder), present
    /// on this PC (not an online-only placeholder), at most <see cref="MaxInputBytes"/>, and a PDF by its header ("%PDF-"
    /// in the first 1024 bytes, as readers accept), whatever its name says. The header is read from the copy, so what is
    /// checked is what qpdf reads.
    /// </summary>
    private static Source Stage(string path, string work, string copyName)
    {
        if (Directory.Exists(path)) throw new PdfFailure(ErrorCode.IsAFolder, "The source is a folder, not a file.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            throw new PdfFailure(ErrorCode.OnlineOnly, "The file is online only; reading it would download it.");
        if (info.Length > MaxInputBytes) throw new PdfFailure(ErrorCode.InputTooLarge, $"The file is larger than {MaxInputBytes >> 20} MB.");
        if (info.Length == 0) throw new PdfFailure(ErrorCode.DecodeFailed, "The file is empty.");
        var copy = System.IO.Path.Combine(work, copyName);
        FileStream from;
        try { from = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new PdfFailure(ErrorCode.SourceNotReadable, "The source file could not be read.");
        }
        long copied = 0;
        using (from)
        using (var to = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buffer = new byte[1 << 16];
            while (true)
            {
                int n;
                try { n = from.Read(buffer); }
                catch (IOException) { throw new PdfFailure(ErrorCode.SourceNotReadable, "The source file could not be read."); }
                if (n == 0) break;
                copied += n;
                if (copied > MaxInputBytes) throw new PdfFailure(ErrorCode.InputTooLarge, $"The file is larger than {MaxInputBytes >> 20} MB.");
                to.Write(buffer, 0, n); // a full temp drive surfaces as DiskFull
            }
        }
        var head = new byte[1024];
        int read;
        using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read)) read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var at = head.AsSpan(0, read).IndexOf("%PDF-"u8);
        if (at < 0) throw new PdfFailure(ErrorCode.ContentMismatch, "The file is not a PDF.");
        var version = Encoding.ASCII.GetString(head, at + 5, Math.Min(3, read - at - 5));
        if (version.Length != 3 || !char.IsAsciiDigit(version[0]) || version[1] != '.' || !char.IsAsciiDigit(version[2])) version = "unknown";
        return new Source(path, copyName, version, copied, MarkOfTheWeb.ZoneToCarry(path));
    }

    private static QpdfRun Q(string work, params string[] arguments) => Qpdf.Run(arguments, QpdfTimeout, work);

    /// <summary>
    /// The page count, which also proves qpdf can open the file. A file that needs a password to open is refused: the
    /// user removes the protection first (with the password), and this module never guesses or prompts.
    /// </summary>
    private static (int Pages, bool Repaired) Open(Source source, string work)
    {
        var run = Q(work, "--show-npages", source.Copy);
        if (run.TimedOut) throw new PdfFailure(ErrorCode.WorkerTimeout, "The PDF took too long to read.");
        if (run.EngineFailed) throw new PdfFailure(ErrorCode.Internal, "The PDF engine stopped unexpectedly.");
        if (run.Usable && int.TryParse(run.Output.AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pages) && pages > 0)
            return (pages, run.ExitCode == QpdfRun.Warnings);
        if (RequiresPassword(source, work)) throw new PdfFailure(ErrorCode.PasswordRequired, "The PDF is protected with a password. Remove the protection first.");
        throw new PdfFailure(ErrorCode.DecodeFailed, "The PDF is damaged and could not be read.");
    }

    private static bool RequiresPassword(Source source, string work) => Q(work, "--requires-password", source.Copy) is { TimedOut: false, ExitCode: QpdfRun.Success };

    private static JobResult Info(string id, string path, string work)
    {
        var source = Stage(path, work, "in-001.pdf");
        var facts = new List<JobFact> { new("Type", "PDF document"), new("Version", source.Version) };
        var encryption = Q(work, "--requires-password", source.Copy);
        if (encryption is { TimedOut: false, ExitCode: QpdfRun.Success })
        {
            facts.Add(new("Protection", "A password is needed to open it", Notice: true));
            facts.Add(new("Size", Size(source.Length)));
            return JobResult.WithFacts(id, facts);
        }
        var (pages, repaired) = Open(source, work);
        facts.Add(new("Pages", pages.ToString("N0", CultureInfo.InvariantCulture)));
        facts.Add(new("Size", Size(source.Length)));
        facts.Add(new("Protection", encryption is { TimedOut: false, ExitCode: QpdfRun.Warnings } ? "Restrictions set by the author (opens without a password)" : "None"));
        facts.Add(new("Fast web view", IsLinearized(source, work) ? "Yes" : "No"));
        if (repaired) facts.Add(new("Structure", "Has errors; readers repair it when they open it", Notice: true));
        return JobResult.WithFacts(id, facts);
    }

    private static bool IsLinearized(Source source, string work) =>
        Q(work, "--check-linearization", source.Copy) is { Usable: true } r && r.Output.Contains("no linearization errors", StringComparison.Ordinal);

    private static JobResult Rotate(JobRequest request, string angle, string work)
    {
        var source = Stage(request.Inputs[0], work, "in-001.pdf");
        var (pages, _) = Open(source, work);
        return WriteOne(request, source, work, [$"--rotate={angle}:1-z", source.Copy], pages, request.Inputs[0]);
    }

    private static JobResult Merge(JobRequest request, string work)
    {
        if (request.Inputs.Count is < 2 or > PdfCapabilities.MaxMergeInputs)
            return JobResult.Fail(request.Id, ErrorCode.InvalidRequest, $"Merge takes 2 to {PdfCapabilities.MaxMergeInputs} PDFs.");
        // The order is the module's own, by name (PdfCapabilities.MergeOrder), not the drop's: the host names and checks
        // the output after the same first file.
        var ordered = PdfCapabilities.MergeOrder(request.Inputs);
        var sources = ordered.Select((p, i) => Stage(p, work, $"in-{i + 1:000}.pdf")).ToArray();
        var total = 0;
        foreach (var s in sources) total += Open(s, work).Pages;
        // The strictest zone of any source: the merged file holds content from every one of them.
        var zone = sources.Max(s => s.Zone);
        List<string> args = ["--empty", "--pages", .. sources.Select(s => s.Copy), "--"];
        return WriteOne(request, sources[0] with { Zone = zone }, work, args, total, ordered[0]);
    }

    /// <summary>
    /// A lossless rewrite (V2 PR 2.3): object streams, every stream compressed, Flate streams recompressed at the highest
    /// level. Images and content are not touched, so nothing visible changes. The copy is kept only when it saves at least
    /// <see cref="PdfCapabilities.MinOptimizeSaving"/> and <see cref="PdfCapabilities.MinOptimizeSavingBytes"/>; otherwise the
    /// PDF is reported as already optimized and nothing is written.
    /// </summary>
    private static JobResult Optimize(JobRequest request, string work)
    {
        var source = Stage(request.Inputs[0], work, "in-001.pdf");
        var (pages, _) = Open(source, work);
        var least = Math.Max(PdfCapabilities.MinOptimizeSavingBytes, (long)Math.Ceiling(source.Length * PdfCapabilities.MinOptimizeSaving));
        return WriteOne(request, source, work, ["--object-streams=generate", "--compress-streams=y", "--recompress-flate", "--compression-level=9", source.Copy],
            pages, request.Inputs[0], written => source.Length - written >= least ? null : "A lossless rewrite would not make it smaller.");
    }

    /// <summary>Fast web view (linearization, V2 PR 2.3); a file that already has it is left alone.</summary>
    private static JobResult Linearize(JobRequest request, string work)
    {
        var source = Stage(request.Inputs[0], work, "in-001.pdf");
        var (pages, repaired) = Open(source, work);
        if (!repaired && IsLinearized(source, work)) return JobResult.Fail(request.Id, ErrorCode.NothingToDo, "The PDF is already set up for fast web view.");
        return WriteOne(request, source, work, ["--linearize", source.Copy], pages, request.Inputs[0]);
    }

    /// <summary>
    /// Repair (V2 PR 2.3): qpdf reads a damaged file by reconstructing its cross-reference table, and the rewrite has a
    /// clean structure. Best effort: what was lost in the damage stays lost. A file qpdf reads without any warning is
    /// reported as undamaged and nothing is written.
    /// </summary>
    private static JobResult Repair(JobRequest request, string work)
    {
        var source = Stage(request.Inputs[0], work, "in-001.pdf");
        if (RequiresPassword(source, work)) throw new PdfFailure(ErrorCode.PasswordRequired, "The PDF is protected with a password. Remove the protection first.");
        var check = Q(work, "--check", source.Copy);
        if (check.TimedOut) throw new PdfFailure(ErrorCode.WorkerTimeout, "The PDF took too long to check.");
        if (check.ExitCode == QpdfRun.Success) return JobResult.Fail(request.Id, ErrorCode.NothingToDo, "qpdf found no damage to repair.");
        return WriteOne(request, source, work, [source.Copy], expectedPages: null, request.Inputs[0]);
    }

    /// <summary>
    /// Runs qpdf with <paramref name="arguments"/> and the work folder's "out.pdf", checks there that the result opens with
    /// <paramref name="expectedPages"/> pages (any number for a repair), then commits it. <paramref name="notWorthIt"/> may
    /// turn a valid result down by its size (Optimize), with the reason.
    /// </summary>
    private static JobResult WriteOne(JobRequest request, Source zoneSource, string work, List<string> arguments, int? expectedPages, string nameSource,
        Func<long, string?>? notWorthIt = null)
    {
        Destination(request, nameSource);
        const string produced = "out.pdf";
        var run = Q(work, [.. arguments, produced]);
        if (run.TimedOut) throw new PdfFailure(ErrorCode.WorkerTimeout, "The PDF took too long to process.");
        if (!run.Usable) throw new PdfFailure(run.EngineFailed ? ErrorCode.Internal : ErrorCode.EncodeFailed, "The PDF engine could not write the result.");
        var check = Q(work, "--show-npages", produced);
        if (!check.Usable || !int.TryParse(check.Output.AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var got) || got < 1
            || expectedPages is { } expected && got != expected)
            throw new PdfFailure(ErrorCode.OutputValidationFailed, "The result did not check out; nothing was saved.");
        var producedPath = Path.Combine(work, produced);
        if (notWorthIt?.Invoke(new FileInfo(producedPath).Length) is { } reason) return JobResult.Fail(request.Id, ErrorCode.NothingToDo, reason);
        return CommitFile(request, producedPath, zoneSource.Zone, nameSource);
    }

    /// <summary>
    /// Copies a checked result into an exclusive temporary file in the destination, marks it, and moves it to the first
    /// free name without overwriting (ADR-0005). The hash is of the bytes in the destination.
    /// </summary>
    private static JobResult CommitFile(JobRequest request, string producedPath, int? zone, string nameSource)
    {
        var temp = OutputNaming.TempPath(nameSource, request.Id, request.OutputDirectory);
        var committed = false;
        try
        {
            CopyNew(producedPath, temp);
            File.SetAttributes(temp, FileAttributes.Hidden); // not a result until it has its final name
            if (zone is { } z) MarkOfTheWeb.Apply(temp, z);
            File.SetAttributes(temp, FileAttributes.Normal);
            var hash = Sha256Of(temp);
            foreach (var candidate in OutputNaming.CandidatesWithExtension(nameSource, ".pdf", PdfCapabilities.NameSuffix(request.CapabilityId), request.OutputDirectory))
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

    private static JobResult Split(JobRequest request, string work)
    {
        var path = request.Inputs[0];
        var source = Stage(path, work, "in-001.pdf");
        var (pages, _) = Open(source, work);
        if (pages > PdfCapabilities.MaxSplitPages)
            return JobResult.Fail(request.Id, ErrorCode.LimitExceeded, $"The PDF has {pages:N0} pages; Split writes at most {PdfCapabilities.MaxSplitPages:N0}.");
        Destination(request, path);
        Directory.CreateDirectory(Path.Combine(work, "pages"));
        var run = Q(work, "--split-pages", source.Copy, Path.Combine("pages", "page-%d.pdf")); // a fixed relative pattern: no user text
        if (run.TimedOut) throw new PdfFailure(ErrorCode.WorkerTimeout, "The PDF took too long to split.");
        if (!run.Usable) throw new PdfFailure(run.EngineFailed ? ErrorCode.Internal : ErrorCode.EncodeFailed, "The PDF engine could not split the file.");
        var written = Directory.GetFiles(Path.Combine(work, "pages"));
        if (written.Length != pages) throw new PdfFailure(ErrorCode.OutputValidationFailed, "The split did not produce one file per page; nothing was saved.");
        var byPage = new SortedDictionary<int, string>();
        foreach (var file in written)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith("page-", StringComparison.Ordinal) || !int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                || page < 1 || page > pages || !byPage.TryAdd(page, file) || !LooksLikeWholePdf(file))
                throw new PdfFailure(ErrorCode.OutputValidationFailed, "The split wrote an unexpected file; nothing was saved.");
        }

        // The destination's staging folder: created exclusively (an existing folder or junction of that name is refused),
        // hidden until it has its final name, removed on any failure.
        var staging = OutputNaming.TempPath(path, request.Id, request.OutputDirectory);
        switch (ExclusiveDirectory.Create(staging))
        {
            case CreateOutcome.Created: break;
            case CreateOutcome.NotWritable: throw new PdfFailure(ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable.");
            default: throw new PdfFailure(ErrorCode.Internal, "Something else is using this job's folder in the destination.");
        }
        var committed = false;
        try
        {
            File.SetAttributes(staging, FileAttributes.Hidden | FileAttributes.Directory);
            var files = new List<(string, string)>(pages);
            foreach (var (page, file) in byPage)
            {
                var final = Path.Combine(staging, PdfCapabilities.PageFileName(path, page, pages));
                CopyNew(file, final);
                if (source.Zone is { } z) MarkOfTheWeb.Apply(final, z);
                files.Add((Path.GetFileName(final), Sha256Of(final)));
            }
            var manifest = FolderManifest.Hash(files);
            File.SetAttributes(staging, FileAttributes.Directory);
            foreach (var candidate in OutputNaming.FolderCandidates(path, PdfCapabilities.NameSuffix(PdfCapabilities.SplitId)!, request.OutputDirectory))
            {
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                if (MoveFolder(staging, candidate))
                {
                    committed = true;
                    return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, manifest)],
                        [new JobFact("Pages", pages.ToString("N0", CultureInfo.InvariantCulture))]);
                }
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free folder name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) DeleteFolder(staging);
        }
    }

    /// <summary>
    /// Renames the staging folder; false when the name was taken meanwhile (it never merges into a folder). A page an
    /// indexer or antivirus holds open for a moment makes the rename fail briefly, so that is retried a few times.
    /// </summary>
    private static bool MoveFolder(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.Move(from, to); return true; }
            catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists) { return false; }
            catch (Exception ex) when (attempt < 5 && (ex is UnauthorizedAccessException || ex is IOException { HResult: ErrorSharingViolation or ErrorAccessDenied }))
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>The destination folder, which must exist and must not be, or be inside, a link or junction.</summary>
    private static void Destination(JobRequest request, string source)
    {
        var dir = new DirectoryInfo(request.OutputDirectory ?? Path.GetDirectoryName(source)!);
        if (!dir.Exists)
            throw request.OutputDirectory is null
                ? new PdfFailure(ErrorCode.SourceNotFound, "The source folder no longer exists.")
                : new PdfFailure(ErrorCode.OutputDirectoryNotWritable, "The configured output folder does not exist.");
        for (var d = dir; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) throw new PdfFailure(ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");
    }

    /// <summary>Copies to a file that must not exist yet (CreateNew: never an existing file), flushed to disk.</summary>
    private static void CopyNew(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        source.CopyTo(target);
        target.Flush(flushToDisk: true);
    }

    /// <summary>A cheap whole-file check for one split page: the header at the start and the end-of-file marker near the end.</summary>
    private static bool LooksLikeWholePdf(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < 16) return false;
        var head = new byte[8];
        fs.ReadExactly(head);
        if (!head.AsSpan().StartsWith("%PDF-"u8)) return false;
        var tail = new byte[(int)Math.Min(1024, fs.Length)];
        fs.Position = fs.Length - tail.Length;
        fs.ReadExactly(tail);
        return tail.AsSpan().IndexOf("%%EOF"u8) >= 0;
    }

    private static string Sha256Of(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static string Size(long bytes) =>
        bytes < 1024 ? FeedbackText.Size(bytes) : string.Create(CultureInfo.InvariantCulture, $"{FeedbackText.Size(bytes)} ({bytes:N0} bytes)");

    /// <summary>
    /// Deletes a file we wrote; an indexer or antivirus may hold it for a moment, so this retries for about two seconds. A
    /// work folder that still cannot go is removed by the next job's sweep.
    /// </summary>
    private static void DeleteFile(string path)
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }

    /// <summary>Deletes a folder we created, with its contents (a junction inside is removed, never followed); retried as above.</summary>
    private static void DeleteFolder(string path)
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }
}
