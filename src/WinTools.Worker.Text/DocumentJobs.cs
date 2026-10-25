using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;

namespace WinTools.Worker.Text;

/// <summary>
/// The Documents module's jobs (V2 PR 6.1, ADR-0025). An Office file is a ZIP package of XML parts, so it is checked as
/// an archive before any part is read: at most <see cref="MaxParts"/> parts, the archive limits on declared sizes and
/// ratio, and every part read counted against <see cref="MaxPartChars"/>. The package must say, in its
/// [Content_Types].xml, that it is the kind its name says. XML is read with DTDs prohibited and no resolver, so no entity
/// expands and nothing outside the package is fetched; relationships are never followed and macros never run.
/// </summary>
internal static class DocumentJobs
{
    public const int MaxParts = 10_000;
    public const int MaxPartChars = 50_000_000;
    private const int ErrorFileExists = unchecked((int)0x80070050), ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;

    private sealed class DocFailure(ErrorCode code, string message) : Exception(message)
    {
        public ErrorCode Code { get; } = code;
    }

    private enum Kind { Word, Excel, PowerPoint }

    public static JobResult Run(JobRequest request)
    {
        var id = request.Id;
        if (id is not { Length: > 0 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return JobResult.Fail("invalid", ErrorCode.InvalidRequest, "Request id must be 1-64 ASCII letters or digits.");
        if (request.CapabilityId is not (DocumentCapabilities.ExtractTextId or DocumentCapabilities.InfoId))
            return JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command.");
        if (request.Parameters is { Count: > 0 }) return JobResult.Fail(id, ErrorCode.InvalidRequest, "This command takes no parameters.");
        if (request.Inputs is not [var path] || !PathPolicy.IsSupported(path) || Path.EndsInDirectorySeparator(path))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "One document per request.");
        if (request.OutputDirectory is { } od && !PathPolicy.IsSupported(Path.Combine(od, "x.txt")))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        try
        {
            var kind = FileKinds.IdentifyByName(path) switch
            {
                var k when k == FileKinds.Docx => Kind.Word,
                var k when k == FileKinds.Xlsx => Kind.Excel,
                var k when k == FileKinds.Pptx => Kind.PowerPoint,
                _ => throw new DocFailure(ErrorCode.UnsupportedContent, "Not a Word, Excel or PowerPoint file."),
            };
            using var zip = Open(path, kind);
            return request.CapabilityId == DocumentCapabilities.InfoId ? Info(id, zip, kind) : ExtractText(request, path, zip, kind);
        }
        catch (DocFailure ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (XmlException) { return JobResult.Fail(id, ErrorCode.DecodeFailed, "A part of the document is not valid XML (or declares a DTD, which is never read)."); }
        catch (InvalidDataException) { return JobResult.Fail(id, ErrorCode.DecodeFailed, "The document's package is damaged."); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (UnauthorizedAccessException) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (IOException ex) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, $"Writing failed (0x{ex.HResult:X8})."); }
    }

    /// <summary>Opens the package after the checks every archive passes, and checks it is the kind its name says.</summary>
    private static ZipArchive Open(string path, Kind kind)
    {
        if (Directory.Exists(path)) throw new DocFailure(ErrorCode.IsAFolder, "It is a folder.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) throw new DocFailure(ErrorCode.OnlineOnly, "The file is online only; reading it would download it.");
        if (info.Length > DocumentCapabilities.MaxInputBytes) throw new DocFailure(ErrorCode.LimitExceeded, $"The file is larger than {DocumentCapabilities.MaxInputBytes >> 20} MB.");
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new DocFailure(ErrorCode.SourceNotReadable, "The document could not be read."); // never "the destination is not writable"
        }
        try
        {
            var head = new byte[4];
            var got = stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
            // A password-protected Office file is an encrypted compound file, not a ZIP (evaluator, PR 6.1).
            if (got == 4 && head is [0xD0, 0xCF, 0x11, 0xE0]) throw new DocFailure(ErrorCode.PasswordRequired, "The document is protected with a password; open it in Office to remove the protection first.");
            if (got < 2 || head[0] != 'P' || head[1] != 'K')
                throw new DocFailure(ErrorCode.ContentMismatch, "The file is not an Office document, whatever its name says.");
            stream.Position = 0;
            var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count > MaxParts) throw new DocFailure(ErrorCode.LimitExceeded, $"The document has more than {MaxParts:N0} parts.");
            long declared = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                // Two parts whose names differ only in case: which one a reader takes is anyone's guess (evaluator, PR 6.1).
                if (!names.Add(e.FullName)) throw new DocFailure(ErrorCode.DecodeFailed, "The document's package holds two parts of the same name.");
                declared += e.Length;
                if (ArchiveLimits.IsBomb(e.Length, e.CompressedLength) || ArchiveLimits.IsArchiveBomb(declared, info.Length)
                    || declared > 4L * DocumentCapabilities.MaxInputBytes + (1L << 30))
                    throw new DocFailure(ErrorCode.LimitExceeded, "The document's package expands far beyond its size; it looks like a compression bomb.");
            }
            var mainType = MainContentType(ReadPart(zip, "[Content_Types].xml") ?? throw new DocFailure(ErrorCode.ContentMismatch, "The file is not an Office document (it has no content types)."), kind);
            if (mainType is null) throw new DocFailure(ErrorCode.ContentMismatch, $"The file is not a {Name(kind)}, whatever its name says.");
            return zip;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string MainPart(Kind kind) => kind switch { Kind.Word => "/word/document.xml", Kind.Excel => "/xl/workbook.xml", _ => "/ppt/presentation.xml" };

    private static string MainType(Kind kind) => kind switch
    {
        Kind.Word => "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
        Kind.Excel => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
        _ => "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
    };

    /// <summary>
    /// The content type [Content_Types].xml gives the kind's main part, when it is exactly that kind's (or its
    /// macro-enabled variant); null otherwise. Parsed, not searched: a comment naming the type proves nothing (evaluator, PR 6.1).
    /// </summary>
    private static string? MainContentType(string types, Kind kind)
    {
        using var r = Xml(types);
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "Override"
                && string.Equals(r.GetAttribute("PartName"), MainPart(kind), StringComparison.OrdinalIgnoreCase)
                && r.GetAttribute("ContentType") is { } t && (t == MainType(kind) || t.StartsWith("application/vnd." + MacroType(kind), StringComparison.Ordinal)))
                return t;
        return null;
    }

    private static string MacroType(Kind kind) => kind switch
    {
        Kind.Word => "ms-word.document.macroEnabled",
        Kind.Excel => "ms-excel.sheet.macroEnabled",
        _ => "ms-powerpoint.presentation.macroEnabled",
    };

    private static string Name(Kind kind) => kind switch { Kind.Word => "Word document", Kind.Excel => "Excel workbook", _ => "PowerPoint presentation" };

    /// <summary>A part's text, bounded; null when the part is not there.</summary>
    private static string? ReadPart(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[1 << 16];
        var text = new StringBuilder();
        int n;
        while ((n = reader.Read(buffer)) > 0)
        {
            if (text.Length + n > MaxPartChars) throw new DocFailure(ErrorCode.LimitExceeded, "A part of the document is too large to read.");
            text.Append(buffer, 0, n);
        }
        return text.ToString();
    }

    /// <summary>An XML reader that reads no DTD, resolves nothing and stops at <see cref="MaxPartChars"/>.</summary>
    private static XmlReader Xml(string text) => XmlReader.Create(new StringReader(text), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxPartChars,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    });

    /// <summary>The parts named prefix + N + ".xml", in number order (slide10 after slide9).</summary>
    private static IEnumerable<(int Number, string Name)> Numbered(ZipArchive zip, string prefix) =>
        zip.Entries.Select(e => e.FullName)
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(n.AsSpan(prefix.Length, n.Length - prefix.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(n => (int.Parse(n.AsSpan(prefix.Length, n.Length - prefix.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture), n))
            .OrderBy(p => p.Item1);

    /// <summary>
    /// The text of every paragraph element (local name <paramref name="paragraph"/>) in a part, its runs joined, a tab in
    /// a run kept as a tab and a line break as a new line. Text boxes, the fallback copy of alternate content and deleted or
    /// moved-away text are skipped whole, so a paragraph holding a text box keeps its own text and nothing comes out twice
    /// (evaluator, PR 6.1); tab-stop definitions are not tabs.
    /// </summary>
    private static IEnumerable<string> Paragraphs(string xml, string paragraph, string textElement)
    {
        using var r = Xml(xml);
        StringBuilder? current = null;
        var inRun = 0;
        // ReadElementContentAsString and Skip leave the reader on the next node: that node is handled next, not skipped.
        var onNext = false;
        while (onNext || r.Read())
        {
            onNext = false;
            if (r.NodeType == XmlNodeType.Element && r.LocalName is "txbxContent" or "Fallback" or "del" or "moveFrom" or "tabs" or "tabLst" && !r.IsEmptyElement)
            {
                r.Skip();
                onNext = true;
            }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == paragraph && current is null) { current = new StringBuilder(); if (r.IsEmptyElement) { yield return ""; current = null; } }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "r") { if (!r.IsEmptyElement) inRun++; }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "r") inRun = Math.Max(0, inRun - 1);
            else if (r.NodeType == XmlNodeType.Element && current is not null && r.LocalName == textElement && !r.IsEmptyElement) { current.Append(r.ReadElementContentAsString()); onNext = true; }
            else if (r.NodeType == XmlNodeType.Element && current is not null && inRun > 0 && r.LocalName == "tab") current.Append('\t');
            else if (r.NodeType == XmlNodeType.Element && current is not null && r.LocalName is "br" or "cr") current.Append("\r\n");
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == paragraph && current is not null) { yield return current.ToString(); current = null; }
        }
    }

    /// <summary>A heading the extractor writes itself ("Slide 3", "Sheet 2"), which is not the document's text.</summary>
    private const char Heading = '\u0001';

    private static IEnumerable<string> TextLines(ZipArchive zip, Kind kind)
    {
        switch (kind)
        {
            case Kind.Word:
                foreach (var p in Paragraphs(ReadPart(zip, "word/document.xml") ?? throw new DocFailure(ErrorCode.DecodeFailed, "The document has no body."), "p", "t")) yield return p;
                break;
            case Kind.PowerPoint:
                foreach (var (n, name) in Numbered(zip, "ppt/slides/slide"))
                {
                    yield return $"{Heading}Slide {n}";
                    foreach (var p in Paragraphs(ReadPart(zip, name)!, "p", "t")) if (p.Length > 0) yield return p;
                    yield return "";
                }
                break;
            default:
                var shared = new List<string>();
                if (ReadPart(zip, "xl/sharedStrings.xml") is { } sst)
                    foreach (var si in Paragraphs(sst, "si", "t")) shared.Add(si);
                foreach (var (n, name) in Numbered(zip, "xl/worksheets/sheet"))
                {
                    yield return $"{Heading}Sheet {n}";
                    foreach (var row in Rows(ReadPart(zip, name)!, shared)) yield return row;
                }
                break;
        }
    }

    /// <summary>
    /// Each row's cells, tab-separated in their columns (the "r" reference, gaps kept): shared strings, inline strings
    /// (all their runs, one cell), numbers and booleans (evaluator, PR 6.1).
    /// </summary>
    private static IEnumerable<string> Rows(string xml, List<string> shared)
    {
        using var r = Xml(xml);
        SortedDictionary<int, string>? cells = null;
        var next = 0;
        int column = 0;
        string? type = null;
        StringBuilder? inline = null;
        var onNext = false;
        while (onNext || r.Read())
        {
            onNext = false;
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "row") { cells = []; next = 0; if (r.IsEmptyElement) { yield return ""; cells = null; } }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "c" && cells is not null)
            {
                type = r.GetAttribute("t");
                column = ColumnOf(r.GetAttribute("r")) ?? next;
                next = column + 1;
                if (r.IsEmptyElement) cells[column] = "";
            }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "v" && cells is not null)
            {
                var v = r.ReadElementContentAsString();
                cells[column] = type switch
                {
                    "s" => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < shared.Count ? shared[i] : "",
                    "b" => v == "1" ? "TRUE" : "FALSE",
                    _ => v,
                };
                onNext = true;
            }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "is" && cells is not null) inline = new StringBuilder();
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && inline is not null && !r.IsEmptyElement) { inline.Append(r.ReadElementContentAsString()); onNext = true; }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "is" && inline is not null && cells is not null) { cells[column] = inline.ToString(); inline = null; }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "row" && cells is not null)
            {
                var width = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
                if (width > 16_384) throw new DocFailure(ErrorCode.LimitExceeded, "A row is wider than a worksheet can be.");
                yield return string.Join('\t', Enumerable.Range(0, width).Select(k => cells.TryGetValue(k, out var c) ? c : ""));
                cells = null;
            }
        }
    }

    /// <summary>The zero-based column of a cell reference ("C2" is 2), or null.</summary>
    private static int? ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var column = 0;
        var letters = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetterUpper(ch)) break;
            column = column * 26 + (ch - 'A' + 1);
            if (++letters > 3) return null;
        }
        return letters == 0 ? null : column - 1;
    }

    private static JobResult ExtractText(JobRequest request, string path, ZipArchive zip, Kind kind)
    {
        var text = new StringBuilder();
        var content = false; // headings alone are not text (evaluator, PR 6.1)
        foreach (var line in TextLines(zip, kind))
        {
            if (line.Length > 0 && line[0] == Heading) text.Append(line, 1, line.Length - 1);
            else { text.Append(line); content |= line.Trim().Length > 0; }
            text.Append("\r\n");
            if (text.Length > MaxPartChars) throw new DocFailure(ErrorCode.LimitExceeded, "The document's text is too large.");
        }
        if (!content) return JobResult.Fail(request.Id, ErrorCode.NothingToDo, "The document has no text.");
        var output = kind == Kind.PowerPoint ? text.ToString().TrimEnd('\r', '\n') + "\r\n" : text.ToString();
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(output)];
        return Commit(request, path, bytes);
    }

    private static JobResult Commit(JobRequest request, string source, byte[] bytes)
    {
        var dir = new DirectoryInfo(request.OutputDirectory ?? Path.GetDirectoryName(source)!);
        if (!dir.Exists) return JobResult.Fail(request.Id, request.OutputDirectory is null ? ErrorCode.SourceNotFound : ErrorCode.OutputDirectoryNotWritable, "The destination folder does not exist.");
        for (var d = dir; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) return JobResult.Fail(request.Id, ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");
        var temp = OutputNaming.TempPath(source, request.Id, request.OutputDirectory);
        var committed = false;
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.SetAttributes(temp, FileAttributes.Hidden);
            if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes)) return JobResult.Fail(request.Id, ErrorCode.OutputValidationFailed, "The new file did not check out; nothing was saved.");
            if (MarkOfTheWeb.ZoneToCarry(source) is { } z) MarkOfTheWeb.Apply(temp, z);
            File.SetAttributes(temp, FileAttributes.Normal);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            foreach (var candidate in DocumentCapabilities.Candidates(source, request.OutputDirectory))
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
            if (!committed) try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static JobResult Info(string id, ZipArchive zip, Kind kind)
    {
        var facts = new List<JobFact> { new("Type", Name(kind)) };
        if (ReadPart(zip, "docProps/core.xml") is { } core)
        {
            var props = Properties(core);
            void Add(string label, string key) { if (props.TryGetValue(key, out var v) && v.Trim().Length > 0) facts.Add(new(label, Clip(v.Trim()))); }
            Add("Title", "title");
            Add("Author", "creator");
            Add("Last saved by", "lastModifiedBy");
            if (props.TryGetValue("modified", out var m) && DateTime.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when))
                facts.Add(new("Modified", when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        }
        switch (kind)
        {
            case Kind.PowerPoint: facts.Add(new("Slides", Numbered(zip, "ppt/slides/slide").Count().ToString("N0", CultureInfo.InvariantCulture))); break;
            case Kind.Excel: facts.Add(new("Sheets", Numbered(zip, "xl/worksheets/sheet").Count().ToString("N0", CultureInfo.InvariantCulture))); break;
            default:
                if (ReadPart(zip, "docProps/app.xml") is { } app && Properties(app) is var a)
                {
                    if (a.TryGetValue("Pages", out var pages)) facts.Add(new("Pages", Clip(pages)));
                    if (a.TryGetValue("Words", out var words)) facts.Add(new("Words", Clip(words)));
                }
                break;
        }
        var macroType = MainContentType(ReadPart(zip, "[Content_Types].xml") ?? "", kind) is { } t && t.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase);
        if (macroType || zip.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
            facts.Add(new("Macros", "Contains macros (WinTools never runs them)", Notice: true));
        var embedded = zip.Entries.Count(e => e.FullName.Contains("/embeddings/", StringComparison.Ordinal));
        if (embedded > 0) facts.Add(new("Embedded", $"{embedded:N0} embedded objects", Notice: true));
        // Parsed, so quoting and case do not matter; every relationships part counts (a template link lives in settings).
        var external = zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)).Any(e => HasExternalTarget(ReadPart(zip, e.FullName) ?? ""));
        if (external) facts.Add(new("Links", "Links to files or websites outside the document (never opened)", Notice: true));
        return JobResult.WithFacts(id, [.. facts.Take(JobFacts.MaxCount)]);
    }

    /// <summary>At most 150 characters, never cutting a character in two (a surrogate pair, an emoji).</summary>
    private static string Clip(string s)
    {
        if (s.Length <= 150) return s;
        var info = new StringInfo(s);
        var cut = info.LengthInTextElements > 149 ? info.SubstringByTextElements(0, 149) : s;
        while (cut.Length > 149) cut = new StringInfo(cut).SubstringByTextElements(0, new StringInfo(cut).LengthInTextElements - 1);
        return cut + "…";
    }

    private static bool HasExternalTarget(string rels)
    {
        if (rels.Length == 0) return false;
        using var r = Xml(rels);
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && string.Equals(r.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Text a fact may show: no control or format characters (bidi marks and overrides), as JobFacts requires.</summary>
    private static string Showable(string value) =>
        new([.. value.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]);

    /// <summary>
    /// The text of each top-level element of a properties part, by local name. An element with child elements (Office's
    /// HeadingPairs and TitlesOfParts vectors, a custom property) is not text and is left out, never an error.
    /// </summary>
    private static Dictionary<string, string> Properties(string xml)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        using var r = Xml(xml);
        r.MoveToContent();
        var depth = r.Depth;
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.Depth != depth + 1 || r.IsEmptyElement) continue;
            var name = r.LocalName;
            var value = new StringBuilder();
            var simple = true;
            while (r.Read() && r.Depth > depth + 1)
            {
                if (r.NodeType == XmlNodeType.Element) simple = false;
                else if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace) value.Append(r.Value);
            }
            // One RLM must not make the whole Info unbelievable (evaluator, PR 6.1).
            if (simple) props.TryAdd(name, Showable(value.ToString()));
        }
        return props;
    }
}
