using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;
using WinTools.Core.Subtitles;
using WinTools.Worker.Common;

namespace WinTools.Worker.Text;

/// <summary>How a conversion reaches LibreOffice: the real program, or a stand-in in tests.</summary>
internal interface IOfficeEngine
{
    /// <summary>The user's soffice.exe, or null when LibreOffice is not installed.</summary>
    string? Locate();

    ProgramRun Convert(string soffice, IReadOnlyList<string> arguments, string workFolder, IReadOnlyDictionary<string, string> environment, TimeSpan timeout);

    /// <summary>qpdf on the PDF LibreOffice wrote (the bundled one, ADR-0022), or null when it is not there.</summary>
    ProgramRun? Qpdf(IReadOnlyList<string> arguments, string workFolder);
}

/// <summary>The engine of a real run: the worker's own look for LibreOffice, and the bundled qpdf next to it.</summary>
internal sealed class InstalledOfficeEngine : IOfficeEngine
{
    public static InstalledOfficeEngine Instance { get; } = new();

    private static readonly string QpdfPath = Path.Combine(AppContext.BaseDirectory, "qpdf", "qpdf.exe");

    public string? Locate() => InstalledLibreOffice.Find();

    public ProgramRun Convert(string soffice, IReadOnlyList<string> arguments, string workFolder, IReadOnlyDictionary<string, string> environment, TimeSpan timeout) =>
        ChildProcess.Run(soffice, arguments, workFolder, environment, timeout);

    public ProgramRun? Qpdf(IReadOnlyList<string> arguments, string workFolder) => File.Exists(QpdfPath)
        ? ChildProcess.Run(QpdfPath, arguments, workFolder, new Dictionary<string, string>
        {
            ["SystemRoot"] = System.Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
            ["TEMP"] = Path.Combine(workFolder, Soffice.TempFolder),
            ["TMP"] = Path.Combine(workFolder, Soffice.TempFolder),
        }, TimeSpan.FromSeconds(60))
        : null;
}

/// <summary>
/// To PDF through the user's LibreOffice (ADR-0033). One document per request, one request at a time (the host runs
/// this module's jobs on one worker). The steps:
/// <list type="number">
/// <item>LibreOffice is looked for again here (a path is never taken from the request); not found is "not found".</item>
/// <item>The document is checked as its kind: an Office package on ADR-0025's rules (limits and content types), an
/// OpenDocument package by its limits and its mimetype entry, a compound file or RTF by its signature, text by decoding
/// it. It then opens with its kind's own filter, so nothing reaches LibreOffice's content detection. A password-protected
/// Office file is said to be one.</item>
/// <item>A private work folder under the user's temp folder (created exclusively): the document copied in under a fixed
/// name (text decoded and written again as UTF-8), and a fresh profile (<see cref="Soffice"/>). Nothing is ever written
/// next to the original, not even LibreOffice's lock file.</item>
/// <item>LibreOffice runs headless with a time limit; on the limit it is killed with every process it started. Any
/// exit code but 0 is a failure.</item>
/// <item>The PDF must be the only file written, start with "%PDF-", pass "qpdf --check" without a warning (qpdf reads a cut
/// PDF with exit 3, and loses pages doing so), and have at least one page.</item>
/// <item>It is copied to an exclusive temp in the destination, checked again by hash, given the source's zone and
/// moved to the first free name ("report.pdf", "report (1).pdf"...), never over a file.</item>
/// </list>
/// The work folder is removed afterwards, whatever happened; the host removes it too if the worker is killed.
/// </summary>
internal static class OfficeJobs
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;
    private const int DiskFull = unchecked((int)0x80070070);

    /// <summary>How long LibreOffice may take for one document (a fresh profile starts in seconds; a long deck takes longer).</summary>
    public static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The largest text or CSV file handed over (it is decoded in memory first).</summary>
    public const long MaxTextBytes = 20L << 20;

    private sealed class OfficeFailure(ErrorCode code, string message) : Exception(message)
    {
        public ErrorCode Code { get; } = code;
    }

    public static string WorkFolderOf(string requestId) => Path.Combine(Path.GetTempPath(), OfficeCapabilities.WorkFolderName(requestId));

    public static JobResult Run(JobRequest request) => Run(request, InstalledOfficeEngine.Instance, ConvertTimeout);

    internal static JobResult Run(JobRequest request, IOfficeEngine engine, TimeSpan timeout)
    {
        var id = request.Id;
        if (id is not { Length: > 0 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return JobResult.Fail("invalid", ErrorCode.InvalidRequest, "Request id must be 1-64 ASCII letters or digits.");
        if (request.CapabilityId != OfficeCapabilities.ToPdfId) return JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command.");
        if (request.Parameters is { Count: > 0 }) return JobResult.Fail(id, ErrorCode.InvalidRequest, "This command takes no parameters.");
        if (request.Inputs is not [var path] || !PathPolicy.IsSupported(path) || Path.EndsInDirectorySeparator(path))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "One document per request.");
        if (request.OutputDirectory is { } od && !PathPolicy.IsSupported(Path.Combine(od, "x.pdf")))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        var kind = FileKinds.IdentifyByName(path);
        if (kind is null || !OfficeCapabilities.Catalog[0].Input.Accepts(kind))
            return JobResult.Fail(id, ErrorCode.UnsupportedContent, "LibreOffice is not used for this kind of file.");
        var work = WorkFolderOf(id);
        var workCreated = false;
        var atDestination = false; // past this point an IO failure is the destination's, not the temp folder's
        try
        {
            var soffice = engine.Locate();
            if (soffice is null) return JobResult.Fail(id, ErrorCode.UnsupportedOperation, "LibreOffice was not found on this PC.");
            var source = Check(path, kind, out var text);
            switch (ExclusiveDirectory.Create(work))
            {
                case CreateOutcome.Created: workCreated = true; break;
                case CreateOutcome.NotWritable: return JobResult.Fail(id, ErrorCode.Internal, "The temporary folder cannot be written to.");
                default: return JobResult.Fail(id, ErrorCode.Internal, "Something else is using this job's temporary folder.");
            }
            foreach (var sub in new[] { Soffice.InputFolder, Soffice.OutputFolder, Soffice.TempFolder, Path.Combine(Soffice.ProfileFolder, "user") })
                Directory.CreateDirectory(Path.Combine(work, sub));
            File.WriteAllText(Path.Combine(work, Soffice.ProfileFolder, "user", "registrymodifications.xcu"), Soffice.RegistryModifications(), new UTF8Encoding(false));
            // A CSV goes as a workbook, on To Excel's rules; other text as UTF-8 with a BOM, whatever it was.
            var extension = kind == FileKinds.Csv ? ".xlsx" : kind.Extensions[0];
            var inputName = Soffice.InputName(extension);
            var input = Path.Combine(work, Soffice.InputFolder, inputName);
            var replaced = 0;
            if (kind == FileKinds.Csv)
            {
                var separator = Csv.DetectSeparator(text!.Text, out var sepLine);
                var table = Csv.Parse(text.Text, separator, sepLine);
                if (!table.HasData) return JobResult.Fail(id, ErrorCode.NothingToDo, "The table is empty.");
                var workbook = ConvertJobs.Workbook(table, path);
                replaced = workbook.Replaced;
                File.WriteAllBytes(input, workbook.Bytes);
            }
            else if (text is not null) File.WriteAllBytes(input, [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(text.Text)]);
            else CopyNew(source.FullName, input);

            var run = engine.Convert(soffice, Soffice.Arguments(work, inputName, Soffice.InputFilter(extension)), work,
                Soffice.Environment(work, System.Environment.GetEnvironmentVariable), timeout);
            if (run.TimedOut) throw new OfficeFailure(ErrorCode.WorkerTimeout, "LibreOffice took too long and was stopped.");
            if (run.ExitCode != 0) throw new OfficeFailure(ErrorCode.EncodeFailed, $"LibreOffice stopped with code {run.ExitCode}.");
            var pdf = Path.Combine(work, Soffice.OutputName);
            var written = Directory.GetFileSystemEntries(Path.Combine(work, Soffice.OutputFolder));
            if (written.Length == 0) throw new OfficeFailure(ErrorCode.EncodeFailed, "LibreOffice did not write a PDF (the document may be damaged or protected).");
            if (written is not [var only] || !string.Equals(only, pdf, StringComparison.OrdinalIgnoreCase)) throw new OfficeFailure(ErrorCode.OutputValidationFailed, "LibreOffice wrote something unexpected; nothing was saved.");
            var pages = Validate(engine, work, pdf);
            var hash = Sha256Of(pdf);
            atDestination = true;
            List<JobFact> facts = [new JobFact("Pages", pages.ToString("N0", CultureInfo.InvariantCulture))];
            // A legacy code page is a judgement, not a fact the file states: said, as the subtitles and To Excel say it.
            if (text is { } read && read.EncodingName.StartsWith("Windows-", StringComparison.Ordinal)) facts.Add(new("Read as", $"Read as {read.EncodingName}", Notice: true));
            if (replaced > 0) facts.Add(new("Changed", ConvertJobs.ReplacedNotice, Notice: true));
            return DocumentJobs.Commit(request, path, OfficeCapabilities.Candidates(path, request.OutputDirectory),
                s => { using var from = new FileStream(pdf, FileMode.Open, FileAccess.Read, FileShare.Read); from.CopyTo(s); },
                temp => string.Equals(Sha256Of(temp), hash, StringComparison.Ordinal),
                facts);
        }
        catch (OfficeFailure ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (DocumentJobs.DocFailure ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (Csv.Problem ex) { return JobResult.Fail(id, ex.Limit ? ErrorCode.LimitExceeded : ErrorCode.DecodeFailed, ex.Message); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { return JobResult.Fail(id, ErrorCode.DecodeFailed, "The document's package is damaged."); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (System.ComponentModel.Win32Exception) { return JobResult.Fail(id, ErrorCode.EncodeFailed, "LibreOffice is installed but could not be started."); }
        catch (Exception ex) when (!atDestination && ex is UnauthorizedAccessException or IOException) { return JobResult.Fail(id, ex.HResult == DiskFull ? ErrorCode.DiskFull : ErrorCode.Internal, "The temporary folder could not be used."); }
        catch (UnauthorizedAccessException) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (IOException ex) { return JobResult.Fail(id, ex.HResult == DiskFull ? ErrorCode.DiskFull : ErrorCode.OutputDirectoryNotWritable, $"Writing failed (0x{ex.HResult:X8})."); }
        finally
        {
            if (workCreated) DeleteFolder(work);
        }
    }

    /// <summary>
    /// The source, checked: a file that can be read here, within the limits, whose first bytes are what its kind is
    /// (text: whatever decodes, returned decoded).
    /// </summary>
    private static FileInfo Check(string path, FileKind kind, out DecodedText? text)
    {
        text = null;
        if (Directory.Exists(path)) throw new OfficeFailure(ErrorCode.IsAFolder, "It is a folder.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) throw new OfficeFailure(ErrorCode.OnlineOnly, "The file is online only; reading it would download it.");
        var textual = kind == FileKinds.Text || kind == FileKinds.Csv;
        var limit = textual ? MaxTextBytes : OfficeCapabilities.MaxInputBytes;
        if (info.Length > limit) throw new OfficeFailure(ErrorCode.LimitExceeded, $"The file is larger than {limit >> 20} MB.");
        if (info.Length == 0) throw new OfficeFailure(ErrorCode.NothingToDo, "The file is empty.");
        byte[] head;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            head = new byte[textual ? info.Length : Math.Min(info.Length, 8)];
            head = head[..fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new OfficeFailure(ErrorCode.SourceNotReadable, "The document could not be read.");
        }
        var signature = SignatureOf(kind);
        if (textual)
        {
            text = SubtitleEncoding.TryDecode(head, out var why) ?? throw new OfficeFailure(ErrorCode.UnsupportedContent, why);
            if (text.Text.Trim().Length == 0) throw new OfficeFailure(ErrorCode.NothingToDo, "The file is empty.");
            return info;
        }
        var compound = head.AsSpan().StartsWith(CompoundFile);
        // An encrypted .docx, .xlsx or .pptx is a compound file, not a ZIP (ADR-0025): it needs its password in Office first.
        if (compound && signature.SequenceEqual(Zip)) throw new OfficeFailure(ErrorCode.PasswordRequired, "The document is protected with a password; open it in Office to remove the protection first.");
        if (!head.AsSpan().StartsWith(signature)) throw new OfficeFailure(ErrorCode.ContentMismatch, $"The file is not a {kind.DisplayName}, whatever its name says.");
        CheckPackage(path, kind);
        return info;
    }

    /// <summary>
    /// A ZIP-based kind as a package: Office on ADR-0025's rules (limits, and the content type of its kind), OpenDocument by
    /// the same limits and its "mimetype" entry. Anything else with a ZIP signature stops here.
    /// </summary>
    private static void CheckPackage(string path, FileKind kind)
    {
        if (kind == FileKinds.Docx || kind == FileKinds.Xlsx || kind == FileKinds.Pptx)
        {
            using var _ = DocumentJobs.Open(path, kind == FileKinds.Docx ? DocumentJobs.Kind.Word : kind == FileKinds.Xlsx ? DocumentJobs.Kind.Excel : DocumentJobs.Kind.PowerPoint);
            return;
        }
        var expected = kind == FileKinds.Odt ? "application/vnd.oasis.opendocument.text"
            : kind == FileKinds.Ods ? "application/vnd.oasis.opendocument.spreadsheet"
            : kind == FileKinds.Odp ? "application/vnd.oasis.opendocument.presentation" : null;
        if (expected is null) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > DocumentJobs.MaxParts) throw new OfficeFailure(ErrorCode.LimitExceeded, "The document has too many parts.");
        long declared = 0;
        foreach (var e in zip.Entries)
        {
            declared += e.Length;
            if (ArchiveLimits.IsBomb(e.Length, e.CompressedLength) || ArchiveLimits.IsArchiveBomb(declared, stream.Length))
                throw new OfficeFailure(ErrorCode.LimitExceeded, "The document's package expands far beyond its size; it looks like a compression bomb.");
        }
        if (zip.GetEntry("mimetype") is not { Length: < 200 } entry) throw new OfficeFailure(ErrorCode.ContentMismatch, $"The file is not a {kind.DisplayName}, whatever its name says.");
        using var reader = new StreamReader(entry.Open(), Encoding.ASCII);
        if (reader.ReadToEnd().Trim() != expected) throw new OfficeFailure(ErrorCode.ContentMismatch, $"The file is not a {kind.DisplayName}, whatever its name says.");
    }

    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04], CompoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], RtfHeader = "{\\rtf"u8.ToArray();

    private static byte[] SignatureOf(FileKind kind) =>
        kind == FileKinds.Doc || kind == FileKinds.Xls || kind == FileKinds.Ppt ? CompoundFile
        : kind == FileKinds.Rtf ? RtfHeader
        : Zip; // docx, xlsx, pptx, odt, ods, odp

    /// <summary>The PDF's page count, after its header and qpdf's whole-file check; a failure says nothing was saved.</summary>
    private static int Validate(IOfficeEngine engine, string work, string pdf)
    {
        var info = new FileInfo(pdf);
        if (info.Length < 64 || info.Length > OfficeCapabilities.MaxOutputBytes) throw new OfficeFailure(ErrorCode.OutputValidationFailed, "LibreOffice did not produce a usable PDF; nothing was saved.");
        using (var fs = new FileStream(pdf, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var head = new byte[5];
            if (fs.ReadAtLeast(head, 5, throwOnEndOfStream: false) != 5 || !head.AsSpan().SequenceEqual("%PDF-"u8))
                throw new OfficeFailure(ErrorCode.OutputValidationFailed, "LibreOffice did not produce a PDF; nothing was saved.");
        }
        var relative = Soffice.OutputName;
        var check = engine.Qpdf(["--check", relative], work) ?? throw new OfficeFailure(ErrorCode.Internal, "The PDF checker (qpdf) is missing from this copy of WinTools.");
        if (check.TimedOut || check.ExitCode != 0) throw new OfficeFailure(ErrorCode.OutputValidationFailed, "The PDF LibreOffice wrote does not check out; nothing was saved.");
        var count = engine.Qpdf(["--show-npages", relative], work);
        if (count is not { TimedOut: false, ExitCode: 0 } || !int.TryParse(count.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pages) || pages < 1)
            throw new OfficeFailure(ErrorCode.OutputValidationFailed, "The PDF LibreOffice wrote has no pages; nothing was saved.");
        return pages;
    }

    private static string Sha256Of(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    private static void CopyNew(string from, string to)
    {
        try
        {
            using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            var buffer = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = source.Read(buffer)) > 0)
            {
                if ((total += n) > OfficeCapabilities.MaxInputBytes) throw new OfficeFailure(ErrorCode.LimitExceeded, $"The file is larger than {OfficeCapabilities.MaxInputBytes >> 20} MB.");
                target.Write(buffer, 0, n);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new OfficeFailure(ErrorCode.SourceNotReadable, "The document could not be read.");
        }
    }

    private static void DeleteFolder(string path)
    {
        // LibreOffice may still be letting go of its profile for a moment after it exits.
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }
}
