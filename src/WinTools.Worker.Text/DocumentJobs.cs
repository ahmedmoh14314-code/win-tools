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
        if (request.CapabilityId is not (DocumentCapabilities.ExtractTextId or DocumentCapabilities.InfoId or DocumentCapabilities.RemovePropertiesId))
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
            return request.CapabilityId switch
            {
                DocumentCapabilities.InfoId => Info(id, zip, kind),
                DocumentCapabilities.ExtractTextId => ExtractText(request, path, zip, kind),
                _ => RemoveProperties(request, path, zip, kind),
            };
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
        return Commit(request, path, DocumentCapabilities.Candidates(path, request.OutputDirectory), s => s.Write(bytes),
            temp => File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes), []);
    }

    /// <summary>
    /// Writes the output to an exclusive hidden temp, checks it, carries the source's zone and claims the first free
    /// candidate name; the temp never survives a failure.
    /// </summary>
    private static JobResult Commit(JobRequest request, string source, IEnumerable<string> candidates, Action<FileStream> write, Func<string, bool> valid, IReadOnlyList<JobFact> facts)
    {
        var dir = new DirectoryInfo(request.OutputDirectory ?? Path.GetDirectoryName(source)!);
        if (!dir.Exists) return JobResult.Fail(request.Id, request.OutputDirectory is null ? ErrorCode.SourceNotFound : ErrorCode.OutputDirectoryNotWritable, "The destination folder does not exist.");
        for (var d = dir; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) return JobResult.Fail(request.Id, ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");
        var temp = OutputNaming.TempPath(source, request.Id, request.OutputDirectory);
        var committed = false;
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                write(fs);
                fs.Flush(flushToDisk: true);
            }
            File.SetAttributes(temp, FileAttributes.Hidden);
            if (!valid(temp)) return JobResult.Fail(request.Id, ErrorCode.OutputValidationFailed, "The new file did not check out; nothing was saved.");
            if (MarkOfTheWeb.ZoneToCarry(source) is { } z) MarkOfTheWeb.Apply(temp, z);
            File.SetAttributes(temp, FileAttributes.Normal);
            string hash;
            using (var check = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read)) hash = Convert.ToHexStringLower(SHA256.HashData(check));
            foreach (var candidate in candidates)
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
            if (!committed) try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Relationship types of the package's property parts, transitional and strict (ADR-0025 decision 6).
    private static readonly string[] PropertyRelationships = ["/metadata/core-properties", "/extended-properties", "/custom-properties", "/extendedProperties", "/customProperties"];

    /// <summary>
    /// Remove properties (V2 PR 6.2): a copy of the package whose property parts (found through the package's
    /// relationships, and at their usual names) hold only their empty root element; every other part is copied byte for
    /// byte, in the same order, and checked again in the copy. A signed package is refused: the copy would break its
    /// signature. What stays (comment and revision authors, custom XML data) is said, not silently kept.
    /// </summary>
    private static JobResult RemoveProperties(JobRequest request, string path, ZipArchive zip, Kind kind)
    {
        if (zip.Entries.Any(e => e.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
            throw Signed();
        // A backslash is not a part name Office writes; which part a reader takes for it is anyone's guess (evaluator, PR 6.2).
        if (zip.Entries.Any(e => e.FullName.Contains('\\', StringComparison.Ordinal)))
            throw new DocFailure(ErrorCode.UnsupportedContent, "The document's package uses part names Office does not write; nothing was changed.");
        var empty = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var found = false;
        foreach (var part in PropertyParts(zip))
        {
            var xml = ReadPart(zip, part)!;
            // Only a part that is a properties part is emptied: a relationship cannot make the body one (evaluator, PR 6.2).
            if (!IsPropertiesRoot(xml)) throw new DocFailure(ErrorCode.UnsupportedContent, "A part the document calls its properties is not a properties part; nothing was changed.");
            found |= HasContent(xml);
            empty[part] = Encoding.UTF8.GetBytes(EmptyRoot(xml));
        }
        if (!found) return JobResult.Fail(request.Id, ErrorCode.NothingToDo, "The document has no properties to remove.");

        void Write(FileStream fs)
        {
            using var output = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var e in zip.Entries)
            {
                var copy = output.CreateEntry(e.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = e.LastWriteTime;
                using var to = copy.Open();
                if (empty.TryGetValue(e.FullName, out var bytes)) to.Write(bytes);
                else
                {
                    using var from = e.Open();
                    CopyDeclared(from, to, e.Length, e.Crc32);
                }
            }
        }

        bool Valid(string temp)
        {
            try
            {
                using var copy = Open(temp, kind);
                if (!copy.Entries.Select(e => e.FullName).SequenceEqual(zip.Entries.Select(e => e.FullName), StringComparer.Ordinal)) return false;
                foreach (var e in copy.Entries)
                {
                    if (empty.ContainsKey(e.FullName)) { if (HasContent(ReadPart(copy, e.FullName)!)) return false; }
                    else if (!PartHash(e).AsSpan().SequenceEqual(PartHash(zip.GetEntry(e.FullName)!))) return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is DocFailure or InvalidDataException or XmlException or IOException) { return false; }
        }

        return Commit(request, path, DocumentCapabilities.CleanCandidates(path, request.OutputDirectory), Write, Valid, Kept(zip, kind));
    }

    private static readonly Uri PackageRoot = new("http://package/");

    private static readonly HashSet<string> PropertyNamespaces = new(StringComparer.Ordinal)
    {
        "http://schemas.openxmlformats.org/package/2006/metadata/core-properties",
        "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties",
        "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties",
        "http://purl.oclc.org/ooxml/officeDocument/extendedProperties",
        "http://purl.oclc.org/ooxml/officeDocument/customProperties",
    };

    private static DocFailure Signed() =>
        new(ErrorCode.UnsupportedContent, "The document is digitally signed; a copy without properties would break its signature. Nothing was changed.");

    /// <summary>
    /// The property parts: those the package's relationships name, and those at the usual names. A property relationship
    /// whose part cannot be found is refused, never skipped: the copy would keep it and be called clean (evaluator, PR 6.2).
    /// A signature origin found through its relationship refuses the package too.
    /// </summary>
    private static List<string> PropertyParts(ZipArchive zip)
    {
        var names = new List<string>();
        void Add(ZipArchiveEntry e) { if (!names.Contains(e.FullName, StringComparer.Ordinal)) names.Add(e.FullName); }
        if (Find(zip, "_rels/.rels") is { } relsEntry)
        {
            using var r = Xml(ReadPart(zip, relsEntry.FullName)!);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
                var type = r.GetAttribute("Type") ?? "";
                if (type.EndsWith("/digital-signature/origin", StringComparison.Ordinal)) throw Signed();
                if (string.Equals(r.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                if (!PropertyRelationships.Any(t => type.EndsWith(t, StringComparison.Ordinal))) continue;
                Add((r.GetAttribute("Target") is { Length: > 0 } target ? Resolve(zip, target) : null)
                    ?? throw new DocFailure(ErrorCode.UnsupportedContent, "The document names a properties part that is not in it; nothing was changed."));
            }
        }
        foreach (var usual in new[] { "docProps/core.xml", "docProps/app.xml", "docProps/custom.xml" })
            if (Find(zip, usual) is { } e) Add(e);
        return names;
    }

    private static ZipArchiveEntry? Find(ZipArchive zip, string name) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A relationship target as the package rules resolve it: against the package root (RFC 3986, dot segments removed),
    /// the part name in its escaped form first, then unescaped; null when it names no part of this package.
    /// </summary>
    private static ZipArchiveEntry? Resolve(ZipArchive zip, string target)
    {
        if (!Uri.TryCreate(PackageRoot, target, out var uri) || !string.Equals(uri.Authority, PackageRoot.Authority, StringComparison.OrdinalIgnoreCase)) return null;
        var escaped = uri.AbsolutePath.TrimStart('/');
        return escaped.Length == 0 ? null : Find(zip, escaped) ?? Find(zip, Uri.UnescapeDataString(escaped));
    }

    /// <summary>Whether a part's root is a core, extended or custom properties element, in its own namespace.</summary>
    private static bool IsPropertiesRoot(string xml)
    {
        using var r = Xml(xml);
        r.MoveToContent();
        return r.LocalName is "coreProperties" or "Properties" && PropertyNamespaces.Contains(r.NamespaceURI);
    }

    /// <summary>Whether a part's root element holds anything: a child element or text.</summary>
    private static bool HasContent(string xml)
    {
        using var r = Xml(xml);
        r.MoveToContent();
        if (r.IsEmptyElement) return false;
        var depth = r.Depth;
        while (r.Read() && r.Depth > depth)
            if (r.NodeType == XmlNodeType.Element || r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && r.Value.Trim().Length > 0) return true;
        return false;
    }

    /// <summary>The part's root element, empty, in its own namespace and prefix (transitional or strict).</summary>
    private static string EmptyRoot(string xml)
    {
        using var r = Xml(xml);
        r.MoveToContent();
        var ns = System.Security.SecurityElement.Escape(r.NamespaceURI);
        var root = r.Prefix.Length > 0 ? $"{r.Prefix}:{r.LocalName} xmlns:{r.Prefix}=\"{ns}\"" : $"{r.LocalName} xmlns=\"{ns}\"";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<{root}/>";
    }

    /// <summary>
    /// Copies a part, refusing one whose data does not match its declared size (what the bomb rules checked) and CRC-32:
    /// ZipArchive stops at the declared size without a word, so a part that inflates past it would be silently cut.
    /// </summary>
    private static void CopyDeclared(Stream from, Stream to, long declared, uint crc)
    {
        var buffer = new byte[1 << 16];
        var check = new Crc32();
        long total = 0;
        int n;
        while ((n = from.Read(buffer)) > 0)
        {
            // Defensive: ZipArchive itself stops at the declared size, so in practice the CRC-32 below is what catches it.
            if ((total += n) > declared) throw new DocFailure(ErrorCode.DecodeFailed, "A part of the document is larger than it says; nothing was saved.");
            check.Append(buffer.AsSpan(0, n));
            to.Write(buffer, 0, n);
        }
        if (total != declared || check.Value != crc) throw new DocFailure(ErrorCode.DecodeFailed, "A part of the document is damaged (its size or check value does not match); nothing was saved.");
    }

    private static byte[] PartHash(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        return SHA256.HashData(s);
    }

    /// <summary>
    /// What Remove properties leaves in place, said as a notice: names and paths that live in the content, not the
    /// properties (evaluator, PR 6.2): comment and revision authors in any part, custom XML data, Excel's saved folder and
    /// file-sharing user name, Word's attached template path, and the thumbnail.
    /// </summary>
    private static List<JobFact> Kept(ZipArchive zip, Kind kind)
    {
        var kept = new List<string>();
        var names = zip.Entries.Select(e => e.FullName).ToList();
        var revisions = kind switch
        {
            Kind.Word => names.Where(n => n.StartsWith("word/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .Any(n => HasAttribute(ReadPart(zip, n)!, null, "author")),
            Kind.Excel => names.Any(n => n.StartsWith("xl/revisions/", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
        if (revisions || names.Any(n => n.Contains("comment", StringComparison.OrdinalIgnoreCase) || n.EndsWith("people.xml", StringComparison.OrdinalIgnoreCase)
            || n.Contains("/persons/", StringComparison.OrdinalIgnoreCase) || n.EndsWith("/authors.xml", StringComparison.OrdinalIgnoreCase)))
            kept.Add("comment and revision authors");
        if (names.Any(n => n.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase))) kept.Add("custom XML data");
        if (kind == Kind.Excel && Find(zip, "xl/workbook.xml") is { } workbook && ReadPart(zip, workbook.FullName) is { } wb)
        {
            if (HasElement(wb, "absPath")) kept.Add("the saved folder path");
            if (HasAttribute(wb, "fileSharing", "userName")) kept.Add("the sharing user name");
        }
        // Any link to a local file (the template, a mail-merge source, a linked workbook) carries a path, often a user name.
        if (names.Where(n => n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)).Any(n => HasLocalFileLink(ReadPart(zip, n)!)))
            kept.Add("linked file paths (such as the template)");
        if (names.Any(n => n.StartsWith("docProps/thumbnail", StringComparison.OrdinalIgnoreCase)) || HasRelationship(zip, "/metadata/thumbnail"))
            kept.Add("a thumbnail");
        return kept.Count == 0 ? [] : [new("Kept", $"Not removed: {string.Join(", ", kept)}.", Notice: true)]; // at most 160 characters with every item: never clipped
    }

    /// <summary>Whether any element (of a local name, or any) carries an attribute of a local name.</summary>
    private static bool HasAttribute(string xml, string? element, string attribute)
    {
        using var r = Xml(xml);
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && (element is null || r.LocalName == element) && r.HasAttributes)
                for (var i = 0; i < r.AttributeCount; i++)
                {
                    r.MoveToAttribute(i);
                    if (r.LocalName == attribute) return true;
                }
        return false;
    }

    private static bool HasLocalFileLink(string rels)
    {
        using var r = Xml(rels);
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship"
                && string.Equals(r.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                && r.GetAttribute("Target") is { } t && (t.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || t.StartsWith(@"\\", StringComparison.Ordinal) || t.Length > 2 && t[1] == ':'))
                return true;
        return false;
    }

    /// <summary>Whether the package root has a relationship of this type (to a part that is there).</summary>
    private static bool HasRelationship(ZipArchive zip, string typeSuffix)
    {
        if (Find(zip, "_rels/.rels") is not { } entry) return false;
        using var r = Xml(ReadPart(zip, entry.FullName)!);
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && (r.GetAttribute("Type") ?? "").EndsWith(typeSuffix, StringComparison.Ordinal)
                && r.GetAttribute("Target") is { Length: > 0 } target && Resolve(zip, target) is not null)
                return true;
        return false;
    }

    private static bool HasElement(string xml, string element)
    {
        using var r = Xml(xml);
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && r.LocalName == element) return true;
        return false;
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
