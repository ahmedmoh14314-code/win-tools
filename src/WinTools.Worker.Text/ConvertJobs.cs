using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Output;
using WinTools.Core.Subtitles;

namespace WinTools.Worker.Text;

/// <summary>
/// The documents module's in-house conversions (ADR-0033), on the module's output rules (ADR-0025): an exclusive temp,
/// a read-back that must give the same table or page, the source's zone, and the first free name, never an overwrite.
/// <list type="bullet">
/// <item>To Excel: a CSV or TSV (UTF-8, UTF-16, or Arabic Windows-1256 as the subtitles decoder tells it) to a one-sheet
/// workbook. Plain numbers become numbers; everything else stays text, as written.</item>
/// <item>To CSV: each sheet with data to one CSV (UTF-8 with a BOM, comma, CRLF): one sheet gives "book.csv", several
/// a folder "book (CSV)" with one file per sheet. A text cell that Excel would run as a formula when the CSV is opened
/// (starting with = + - @) gets a leading apostrophe, and the toast says how many.</item>
/// <item>To HTML: Markdown to a page (<see cref="MarkdownPage"/>).</item>
/// </list>
/// </summary>
internal static class ConvertJobs
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;
    private const int ErrorFileExists = unchecked((int)0x80070050), ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const int ErrorSharingViolation = unchecked((int)0x80070020), ErrorAccessDenied = unchecked((int)0x80070005);

    /// <summary>The most characters a CSV To CSV writes (the host reads the file again to check it).</summary>
    public const int MaxCsvChars = 200_000_000;

    /// <summary>To Excel and To HTML: the source is text, read once and bounded.</summary>
    /// <summary>The notices, fixed sentences so the toast can say them in the language in use ("say." keys).</summary>
    public const string ReplacedNotice = "Characters Excel cannot hold were replaced with �.";
    public const string GuardedNotice = "Cells starting with = + - @ got a leading ' so Excel shows them as text and does not run them.";

    public static JobResult FromText(JobRequest request, string path)
    {
        var text = ReadText(path);
        return request.CapabilityId == DocumentCapabilities.ToXlsxId ? ToXlsx(request, path, text) : ToHtml(request, path, text);
    }

    /// <summary>The whole file decoded (BOM, strict UTF-8, or unmistakable Arabic Windows-1256), or a refusal.</summary>
    private static DecodedText ReadText(string path)
    {
        if (Directory.Exists(path)) throw new DocumentJobs.DocFailure(ErrorCode.IsAFolder, "It is a folder.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            throw new DocumentJobs.DocFailure(ErrorCode.OnlineOnly, "The file is online only; reading it would download it.");
        if (info.Length > DocumentCapabilities.MaxTextInputBytes)
            throw new DocumentJobs.DocFailure(ErrorCode.LimitExceeded, $"The file is larger than {DocumentCapabilities.MaxTextInputBytes >> 20} MB.");
        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(fs.Length, DocumentCapabilities.MaxTextInputBytes)];
            var n = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            bytes = n == buffer.Length ? buffer : buffer[..n];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new DocumentJobs.DocFailure(ErrorCode.SourceNotReadable, "The file could not be read.");
        }
        return SubtitleEncoding.TryDecode(bytes, out var why) ?? throw new DocumentJobs.DocFailure(ErrorCode.UnsupportedContent, why);
    }

    private static JobResult ToXlsx(JobRequest request, string path, DecodedText decoded)
    {
        var tsv = string.Equals(Path.GetExtension(path), ".tsv", StringComparison.OrdinalIgnoreCase);
        var sepLine = false;
        var separator = tsv ? '\t' : Csv.DetectSeparator(decoded.Text, out sepLine);
        var table = Csv.Parse(decoded.Text, separator, skipSepLine: !tsv && sepLine);
        if (!table.HasData) return JobResult.Fail(request.Id, ErrorCode.NothingToDo, "The table is empty.");
        // A line break inside a cell becomes Excel's own (LF), and a character XML cannot hold becomes U+FFFD: this is
        // what the workbook must give back, cell for cell.
        var replaced = 0;
        var expected = table.Rows.Select(row => row.Select(c => XlsxWriter.XmlSafe(c.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), ref replaced)).ToArray()).ToList();
        var (bytes, _) = XlsxWriter.Build(expected, XlsxWriter.SheetName(Path.GetFileNameWithoutExtension(path)));

        bool Valid(string temp)
        {
            try
            {
                using var zip = DocumentJobs.Open(temp, DocumentJobs.Kind.Excel);
                if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes)) return false;
                var sheets = WorkbookReader.Sheets(zip);
                if (sheets is not [var sheet]) return false;
                var back = WorkbookReader.Rows(DocumentJobs.ReadPart(zip, sheet.Part)!, WorkbookReader.ContextOf(zip), raw: true);
                return SameTable(back, expected);
            }
            catch (Exception ex) when (ex is DocumentJobs.DocFailure or InvalidDataException or System.Xml.XmlException or IOException or Csv.Problem) { return false; }
        }

        var facts = new List<JobFact>();
        if (decoded.EncodingName.StartsWith("Windows-", StringComparison.Ordinal)) facts.Add(new("Read as", $"Read as {decoded.EncodingName}", Notice: true));
        if (replaced > 0) facts.Add(new("Changed", ReplacedNotice, Notice: true));
        return DocumentJobs.Commit(request, path, DocumentCapabilities.XlsxCandidates(path, request.OutputDirectory), s => s.Write(bytes), Valid, facts);
    }

    /// <summary>Two tables hold the same cells, ignoring empty cells at the end of a row and empty rows at the end.</summary>
    internal static bool SameTable(IReadOnlyList<string[]> a, IReadOnlyList<string[]> b)
    {
        static string[] Trim(string[] row) { var n = row.Length; while (n > 0 && row[n - 1].Length == 0) n--; return row[..n]; }
        static List<string[]> Rows(IReadOnlyList<string[]> t) { var l = t.Select(Trim).ToList(); while (l.Count > 0 && l[^1].Length == 0) l.RemoveAt(l.Count - 1); return l; }
        var x = Rows(a);
        var y = Rows(b);
        return x.Count == y.Count && x.Zip(y).All(p => p.First.AsSpan().SequenceEqual(p.Second));
    }

    private static JobResult ToHtml(JobRequest request, string path, DecodedText decoded)
    {
        if (decoded.Text.Trim().Length == 0) return JobResult.Fail(request.Id, ErrorCode.NothingToDo, "The file is empty.");
        if (decoded.Text.Length > MarkdownPage.MaxChars) throw new DocumentJobs.DocFailure(ErrorCode.LimitExceeded, "The Markdown file is too large to convert.");
        string page;
        // Markdig refuses nesting past its depth limit with an ArgumentException ("too deeply nested"), and past the
        // thread's stack with InsufficientExecutionStackException, before either could overflow (found by the 20,000-level test).
        try { page = MarkdownPage.Render(decoded.Text, Path.GetFileNameWithoutExtension(path)); }
        catch (Exception ex) when (ex is ArgumentException or InsufficientExecutionStackException)
        {
            throw new DocumentJobs.DocFailure(ErrorCode.LimitExceeded, "The Markdown is nested too deeply to convert.");
        }
        var bytes = new UTF8Encoding(false).GetBytes(page);
        var facts = new List<JobFact>();
        if (decoded.EncodingName.StartsWith("Windows-", StringComparison.Ordinal)) facts.Add(new("Read as", $"Read as {decoded.EncodingName}", Notice: true));
        return DocumentJobs.Commit(request, path, DocumentCapabilities.HtmlCandidates(path, request.OutputDirectory), s => s.Write(bytes),
            temp => File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes) && MarkdownPage.IsInert(Encoding.UTF8.GetString(bytes)), facts);
    }

    /// <summary>To CSV: every worksheet with data, in the workbook's order.</summary>
    public static JobResult ToCsv(JobRequest request, string path, ZipArchive zip)
    {
        var context = WorkbookReader.ContextOf(zip);
        var sheets = new List<(string Name, List<string[]> Rows)>();
        var guarded = 0;
        foreach (var sheet in WorkbookReader.Sheets(zip))
        {
            var rows = WorkbookReader.Rows(DocumentJobs.ReadPart(zip, sheet.Part) ?? "", context);
            if (!rows.Any(r => r.Any(c => c.Length > 0))) continue;
            if (sheets.Count == DocumentCapabilities.MaxCsvSheets)
                throw new DocumentJobs.DocFailure(ErrorCode.LimitExceeded, $"The workbook has more than {DocumentCapabilities.MaxCsvSheets} sheets with data.");
            guarded += Guard(rows);
            sheets.Add((sheet.Name, rows));
        }
        if (sheets.Count == 0) return JobResult.Fail(request.Id, ErrorCode.NothingToDo, "No sheet has data.");
        var files = sheets.Select(s => (s.Name, s.Rows, Bytes: Csv.Write(s.Rows))).ToList();
        if (files.Any(f => f.Bytes.Length > MaxCsvChars)) throw new DocumentJobs.DocFailure(ErrorCode.LimitExceeded, "A sheet is too large to write as CSV.");
        var facts = new List<JobFact>();
        if (guarded > 0) facts.Add(new("Changed", GuardedNotice, Notice: true));

        // The written CSV must parse back to the same rows, with the same width (every row padded to the widest).
        bool Valid(byte[] bytes, List<string[]> rows)
        {
            try
            {
                var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 3, bytes.Length - 3);
                return bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) && SameTable(Csv.Parse(text, ',').Rows, rows);
            }
            catch (Exception ex) when (ex is DecoderFallbackException or Csv.Problem) { return false; }
        }

        if (files is [var only])
            return DocumentJobs.Commit(request, path, DocumentCapabilities.CsvCandidates(path, request.OutputDirectory), s => s.Write(only.Bytes),
                temp => File.ReadAllBytes(temp).AsSpan().SequenceEqual(only.Bytes) && Valid(only.Bytes, only.Rows), facts);
        foreach (var f in files) if (!Valid(f.Bytes, f.Rows)) return JobResult.Fail(request.Id, ErrorCode.OutputValidationFailed, "A CSV did not check out; nothing was saved.");
        return CommitFolder(request, path, [.. SheetFileNames(files.Select(f => f.Name)).Zip(files, (name, f) => (name, f.Bytes))], facts);
    }

    /// <summary>
    /// Guards the text cells Excel would run when the CSV is opened: a leading = + - @ (or a tab or CR) gets an apostrophe,
    /// which Excel shows as text (OWASP, CSV injection). Numbers and dates the reader wrote ("-5", "2026-10-28") are values,
    /// not text, and are left alone: only cells that do not read as a plain number are guarded.
    /// </summary>
    private static int Guard(List<string[]> rows)
    {
        var count = 0;
        foreach (var row in rows)
            for (var k = 0; k < row.Length; k++)
                if (row[k] is [('=' or '+' or '-' or '@' or '\t' or '\r'), ..] && !Csv.IsPlainNumber(row[k]) && !double.TryParse(row[k], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    row[k] = "'" + row[k];
                    count++;
                }
        return count;
    }

    /// <summary>Each sheet's file name: its name made safe for Windows, unique without regard to case, ".csv".</summary>
    internal static IEnumerable<string> SheetFileNames(IEnumerable<string> sheetNames)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var sheet in sheetNames)
        {
            n++;
            var safe = new StringBuilder();
            foreach (var c in sheet) safe.Append(char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            var stem = safe.ToString().Trim().TrimEnd('.').Trim();
            if (stem.Length > 100) stem = stem[..100].TrimEnd();
            if (stem.Length == 0) stem = $"Sheet {n}";
            if (IsReservedName(stem)) stem = "_" + stem;
            var name = stem + ".csv";
            for (var i = 2; !used.Add(name); i++) name = $"{stem} ({i}).csv";
            yield return name;
        }
    }

    private static bool IsReservedName(string stem)
    {
        var bare = stem.Split('.')[0].TrimEnd().ToUpperInvariant();
        return bare is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (bare.Length == 4 && (bare.StartsWith("COM", StringComparison.Ordinal) || bare.StartsWith("LPT", StringComparison.Ordinal)) && bare[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }

    /// <summary>
    /// Several CSVs in a new folder: staged under the job's temp name (created exclusively, hidden until named), each
    /// file written, read back and given the source's zone, then the folder renamed to the first free "book (CSV)".
    /// </summary>
    private static JobResult CommitFolder(JobRequest request, string source, IReadOnlyList<(string Name, byte[] Bytes)> files, IReadOnlyList<JobFact> facts)
    {
        var dir = new DirectoryInfo(request.OutputDirectory ?? Path.GetDirectoryName(source)!);
        if (!dir.Exists) return JobResult.Fail(request.Id, request.OutputDirectory is null ? ErrorCode.SourceNotFound : ErrorCode.OutputDirectoryNotWritable, "The destination folder does not exist.");
        for (var d = dir; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) return JobResult.Fail(request.Id, ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");
        var staging = OutputNaming.TempPath(source, request.Id, request.OutputDirectory);
        switch (Worker.ExclusiveDirectory.Create(staging))
        {
            case Worker.CreateOutcome.Created: break;
            case Worker.CreateOutcome.NotWritable: return JobResult.Fail(request.Id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable.");
            default: return JobResult.Fail(request.Id, ErrorCode.Internal, "Something else is using this job's folder in the destination.");
        }
        var committed = false;
        try
        {
            File.SetAttributes(staging, FileAttributes.Hidden | FileAttributes.Directory);
            var zone = MarkOfTheWeb.ZoneToCarry(source);
            var written = new List<(string, string)>();
            foreach (var (name, bytes) in files)
            {
                var target = Path.Combine(staging, name);
                using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }
                if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return JobResult.Fail(request.Id, ErrorCode.OutputValidationFailed, "A CSV did not check out; nothing was saved.");
                if (zone is { } z) MarkOfTheWeb.Apply(target, z);
                written.Add((name, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
            var manifest = FolderManifest.Hash(written);
            File.SetAttributes(staging, FileAttributes.Directory);
            foreach (var candidate in DocumentCapabilities.CsvFolderCandidates(source, request.OutputDirectory))
            {
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                if (MoveFolder(staging, candidate))
                {
                    committed = true;
                    return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, manifest)],
                        [new JobFact("Sheets", files.Count.ToString("N0", CultureInfo.InvariantCulture)), .. facts]);
                }
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free folder name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) DeleteFolder(staging);
        }
    }

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

    private static void DeleteFolder(string path)
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }
}
