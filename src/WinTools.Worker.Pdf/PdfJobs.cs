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
/// The PDF module's jobs (V2 PR 2.1, ADR-0022), with the output-safety rules every module follows (ADR-0005): sources
/// are opened for reading only and never written; qpdf writes a temporary file (or, for Split, a staging folder) in the
/// destination, which is checked, marked with the source's zone, and only then given its final name with a no-overwrite
/// move. Any failure removes what was written. A PDF that needs a password to open is refused before qpdf reads it.
/// </summary>
internal static class PdfJobs
{
    public const long MaxInputBytes = 1L << 30;

    /// <summary>One qpdf run; the host's per-job limit is longer, so a stuck qpdf is ended here with a clear reason.</summary>
    internal static TimeSpan QpdfTimeout { get; set; } = TimeSpan.FromMinutes(4);

    private const int ErrorFileExists = unchecked((int)0x80070050), ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const int ErrorDiskFull = unchecked((int)0x80070070), ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;

    private sealed class PdfFailure(ErrorCode code, string message) : Exception(message)
    {
        public ErrorCode Code { get; } = code;
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
        try
        {
            var single = request.CapabilityId != PdfCapabilities.MergeId;
            if (single && request.Inputs.Count != 1) return JobResult.Fail(id, ErrorCode.InvalidRequest, "One file per request.");
            return request.CapabilityId switch
            {
                PdfCapabilities.InfoId => Info(id, request.Inputs[0]),
                PdfCapabilities.MergeId => Merge(request),
                PdfCapabilities.SplitId => Split(request),
                _ when PdfCapabilities.RotationOf(request.CapabilityId) is { } angle => Rotate(request, angle),
                _ => JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command."),
            };
        }
        catch (PdfFailure ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (UnauthorizedAccessException) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (PathTooLongException) { return JobResult.Fail(id, ErrorCode.OutputNameInvalid, "The output name would be too long."); }
        catch (IOException ex) when (ex.HResult is ErrorDiskFull or ErrorHandleDiskFull) { return JobResult.Fail(id, ErrorCode.DiskFull, "The destination drive is full."); }
        catch (IOException ex) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, $"Writing the output failed (0x{ex.HResult:X8})."); }
    }

    /// <summary>What the worker learnt about one source before any engine read it.</summary>
    private sealed record Source(string Path, string Version, long Length, int? Zone);

    /// <summary>
    /// Checks one source: a file (not a folder), present on this PC (not an online-only placeholder), within the size
    /// limit, and a PDF by its header ("%PDF-" in the first 1024 bytes, as readers accept), whatever its name says.
    /// </summary>
    private static Source Inspect(string path)
    {
        if (Directory.Exists(path)) throw new PdfFailure(ErrorCode.IsAFolder, "The source is a folder, not a file.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            throw new PdfFailure(ErrorCode.OnlineOnly, "The file is online only; reading it would download it.");
        if (info.Length > MaxInputBytes) throw new PdfFailure(ErrorCode.InputTooLarge, $"The file is larger than {MaxInputBytes >> 20} MB.");
        if (info.Length == 0) throw new PdfFailure(ErrorCode.DecodeFailed, "The file is empty.");
        var head = new byte[1024];
        int read;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new PdfFailure(ErrorCode.SourceNotReadable, "The source file could not be read.");
        }
        var at = head.AsSpan(0, read).IndexOf("%PDF-"u8);
        if (at < 0) throw new PdfFailure(ErrorCode.ContentMismatch, "The file is not a PDF.");
        var version = Encoding.ASCII.GetString(head, at + 5, Math.Min(3, read - at - 5));
        if (version.Length != 3 || !char.IsAsciiDigit(version[0]) || version[1] != '.' || !char.IsAsciiDigit(version[2])) version = "unknown";
        return new Source(path, version, info.Length, MarkOfTheWeb.ZoneToCarry(path));
    }

    /// <summary>
    /// The page count, which also proves qpdf can open the file. A file that needs a password to open is refused: the
    /// user removes the protection first (with the password), and this module never guesses or prompts.
    /// </summary>
    private static (int Pages, bool Repaired) Open(Source source)
    {
        var run = Qpdf.Run(["--show-npages", source.Path], QpdfTimeout);
        if (run.TimedOut) throw new PdfFailure(ErrorCode.WorkerTimeout, "The PDF took too long to read.");
        if (run.Usable && int.TryParse(run.Output.AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pages) && pages > 0)
            return (pages, run.ExitCode == QpdfRun.Warnings);
        var password = Qpdf.Run(["--requires-password", source.Path], QpdfTimeout);
        if (password is { TimedOut: false, ExitCode: QpdfRun.Success })
            throw new PdfFailure(ErrorCode.PasswordRequired, "The PDF is protected with a password. Remove the protection first.");
        throw new PdfFailure(ErrorCode.DecodeFailed, "The PDF is damaged and could not be read.");
    }

    private static JobResult Info(string id, string path)
    {
        var source = Inspect(path);
        var facts = new List<JobFact> { new("Type", "PDF document"), new("Version", source.Version) };
        var encryption = Qpdf.Run(["--requires-password", path], QpdfTimeout);
        if (encryption is { TimedOut: false, ExitCode: QpdfRun.Success })
        {
            facts.Add(new("Protection", "A password is needed to open it", Notice: true));
            facts.Add(new("Size", Size(source.Length)));
            return JobResult.WithFacts(id, facts);
        }
        var (pages, repaired) = Open(source);
        facts.Add(new("Pages", pages.ToString("N0", CultureInfo.InvariantCulture)));
        facts.Add(new("Size", Size(source.Length)));
        facts.Add(new("Protection", encryption is { TimedOut: false, ExitCode: QpdfRun.Warnings } ? "Restrictions set by the author (opens without a password)" : "None"));
        var linear = Qpdf.Run(["--check-linearization", path], QpdfTimeout);
        facts.Add(new("Fast web view", linear.Usable && linear.Output.Contains("no linearization errors", StringComparison.Ordinal) ? "Yes" : "No"));
        if (repaired) facts.Add(new("Structure", "Has errors; readers repair it when they open it", Notice: true));
        return JobResult.WithFacts(id, facts);
    }

    private static JobResult Rotate(JobRequest request, string angle)
    {
        var source = Inspect(request.Inputs[0]);
        var (pages, _) = Open(source);
        return WriteOne(request, source, [$"--rotate={angle}:1-z", source.Path], pages, request.Inputs[0]);
    }

    private static JobResult Merge(JobRequest request)
    {
        if (request.Inputs.Count is < 2 or > PdfCapabilities.MaxMergeInputs)
            return JobResult.Fail(request.Id, ErrorCode.InvalidRequest, $"Merge takes 2 to {PdfCapabilities.MaxMergeInputs} PDFs.");
        var sources = request.Inputs.Select(Inspect).ToArray();
        var total = 0;
        foreach (var s in sources) total += Open(s).Pages;
        // The strictest zone of any source: the merged file holds content from every one of them.
        var zone = sources.Max(s => s.Zone);
        List<string> args = ["--empty", "--pages", .. sources.Select(s => s.Path), "--"];
        return WriteOne(request, sources[0] with { Zone = zone }, args, total, request.Inputs[0]);
    }

    /// <summary>
    /// Runs qpdf with <paramref name="arguments"/> plus a temporary output, checks that the output is a PDF qpdf can open
    /// with <paramref name="expectedPages"/> pages, marks it, and moves it to the first free name.
    /// </summary>
    private static JobResult WriteOne(JobRequest request, Source zoneSource, List<string> arguments, int expectedPages, string nameSource)
    {
        Destination(request, nameSource);
        var temp = OutputNaming.TempPath(nameSource, request.Id, request.OutputDirectory);
        Claim(temp);
        var committed = false;
        try
        {
            var run = Qpdf.Run([.. arguments, temp], QpdfTimeout);
            if (run.TimedOut) throw new PdfFailure(ErrorCode.WorkerTimeout, "The PDF took too long to process.");
            if (!run.Usable) throw new PdfFailure(ErrorCode.EncodeFailed, "The PDF engine could not write the result.");
            File.SetAttributes(temp, FileAttributes.Hidden); // not a result until it has its final name
            var check = Qpdf.Run(["--show-npages", temp], QpdfTimeout);
            if (!check.Usable || !int.TryParse(check.Output.AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var got) || got != expectedPages)
                throw new PdfFailure(ErrorCode.OutputValidationFailed, "The result did not check out; nothing was saved.");
            if (zoneSource.Zone is { } z) MarkOfTheWeb.Apply(temp, z);
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
            if (!committed) TryDelete(temp);
        }
    }

    private static JobResult Split(JobRequest request)
    {
        var path = request.Inputs[0];
        var source = Inspect(path);
        var (pages, _) = Open(source);
        if (pages > PdfCapabilities.MaxSplitPages)
            return JobResult.Fail(request.Id, ErrorCode.LimitExceeded, $"The PDF has {pages:N0} pages; Split writes at most {PdfCapabilities.MaxSplitPages:N0}.");
        Destination(request, path);
        var staging = OutputNaming.TempPath(path, request.Id, request.OutputDirectory); // a folder of that name, never a result
        if (File.Exists(staging) || Directory.Exists(staging)) throw new PdfFailure(ErrorCode.Internal, "A previous job's folder is in the way.");
        Directory.CreateDirectory(staging);
        var committed = false;
        try
        {
            File.SetAttributes(staging, FileAttributes.Hidden | FileAttributes.Directory);
            // qpdf numbers the pages in its own fixed pattern; the user's file name never reaches qpdf's "%d" expansion.
            var run = Qpdf.Run(["--split-pages", path, Path.Combine(staging, "page-%d.pdf")], QpdfTimeout);
            if (run.TimedOut) throw new PdfFailure(ErrorCode.WorkerTimeout, "The PDF took too long to split.");
            if (!run.Usable) throw new PdfFailure(ErrorCode.EncodeFailed, "The PDF engine could not split the file.");
            var written = Directory.GetFiles(staging);
            if (written.Length != pages) throw new PdfFailure(ErrorCode.OutputValidationFailed, "The split did not produce one file per page; nothing was saved.");
            var files = new List<(string, string)>(pages);
            foreach (var file in written)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith("page-", StringComparison.Ordinal) || !int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1 || page > pages)
                    throw new PdfFailure(ErrorCode.OutputValidationFailed, "The split wrote an unexpected file; nothing was saved.");
                if (!LooksLikeWholePdf(file)) throw new PdfFailure(ErrorCode.OutputValidationFailed, "A page did not check out; nothing was saved.");
                if (source.Zone is { } z) MarkOfTheWeb.Apply(file, z);
                var final = Path.Combine(staging, PdfCapabilities.PageFileName(path, page, pages));
                File.Move(file, final, overwrite: false);
                files.Add((Path.GetFileName(final), Sha256Of(final)));
            }
            var manifest = FolderManifest.Hash(files);
            File.SetAttributes(staging, FileAttributes.Directory);
            foreach (var candidate in OutputNaming.FolderCandidates(path, PdfCapabilities.NameSuffix(PdfCapabilities.SplitId)!, request.OutputDirectory))
            {
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                try
                {
                    Directory.Move(staging, candidate); // fails if the name was taken meanwhile; never merges into a folder
                    committed = true;
                    return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, manifest)],
                        [new JobFact("Pages", pages.ToString("N0", CultureInfo.InvariantCulture))]);
                }
                catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists) { }
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free folder name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) TryDeleteFolder(staging);
        }
    }

    /// <summary>The destination folder, which must exist and must not be, or be inside, a link or junction.</summary>
    private static DirectoryInfo Destination(JobRequest request, string source)
    {
        var dir = new DirectoryInfo(request.OutputDirectory ?? Path.GetDirectoryName(source)!);
        if (!dir.Exists)
            throw request.OutputDirectory is null
                ? new PdfFailure(ErrorCode.SourceNotFound, "The source folder no longer exists.")
                : new PdfFailure(ErrorCode.OutputDirectoryNotWritable, "The configured output folder does not exist.");
        for (var d = dir; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) throw new PdfFailure(ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");
        return dir;
    }

    /// <summary>
    /// Creates the temporary file so the name is ours before qpdf writes to it (CreateNew: never an existing file). It is
    /// hidden only after qpdf has written it: Windows refuses to replace a hidden file that is opened without the flag.
    /// </summary>
    private static void Claim(string temp)
    {
        using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
