using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Jobs;
using WinTools.Core.Output;
using WinTools.Worker.Imaging;

namespace WinTools.Worker.Pdf;

/// <summary>
/// The pdfrender module's jobs (ADR-0029). A PDF is read whole (at most <see cref="PdfRenderCapabilities.MaxPdfBytes"/>),
/// must start with "%PDF-", and is opened by PDFium from memory; one that needs a password is refused. Pages become
/// images at <see cref="PdfRenderCapabilities.RenderDpi"/> dpi (less for a page that would pass the image limits), or
/// text. Images to PDF decodes every picture with the image pipeline first. Outputs follow ADR-0005: a temporary file or
/// hidden folder in the destination, checked (pictures decoded, the new PDF opened by PDFium with the right page count),
/// marked with the source's zone, then moved to the first free name without overwriting. A failure leaves nothing.
/// </summary>
internal static class PdfRenderJobs
{
    private const int ErrorFileExists = unchecked((int)0x80070050), ErrorAlreadyExists = unchecked((int)0x800700B7);

    /// <summary>Most characters read from one page (a dense page has a few thousand).</summary>
    private const int MaxCharsPerPage = 200_000;

    private sealed class RenderFailure(ErrorCode code, string message) : Exception(message)
    {
        public ErrorCode Code { get; } = code;
    }

    public static bool Runs(string? capabilityId) =>
        capabilityId == PdfRenderCapabilities.ThumbnailsId || PdfRenderCapabilities.Catalog.Any(c => c.Id == capabilityId);

    public static JobResult Run(JobRequest request)
    {
        var id = request.Id;
        if (id is not { Length: > 0 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return JobResult.Fail("invalid", ErrorCode.InvalidRequest, "Request id must be 1-64 ASCII letters or digits.");
        if (request.Parameters is { Count: > 0 }) return JobResult.Fail(id, ErrorCode.InvalidRequest, "This command takes no parameters.");
        if (!Runs(request.CapabilityId)) return JobResult.Fail(id, ErrorCode.UnsupportedOperation, "This worker does not run that command.");
        if (request.Inputs.Count == 0 || !request.Inputs.All(PathPolicy.IsSupported) || request.Inputs.Any(Path.EndsInDirectorySeparator))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "Every input must be a supported file path.");
        if (request.OutputDirectory is { } od && !PathPolicy.IsSupported(Path.Combine(od, "x.pdf")))
            return JobResult.Fail(id, ErrorCode.InvalidRequest, "The output folder must be a canonical, fully qualified path.");
        var images = request.CapabilityId == PdfRenderCapabilities.ImagesToPdfId;
        if (images ? request.Inputs.Count > PdfRenderCapabilities.MaxImages : request.Inputs.Count != 1)
            return JobResult.Fail(id, ErrorCode.InvalidRequest, images ? $"At most {PdfRenderCapabilities.MaxImages} pictures in one PDF." : "One PDF per request.");
        if (!Pdfium.IsPresent) return JobResult.Fail(id, ErrorCode.Internal, "The PDF engine is missing from this copy of WinTools.");
        try
        {
            return images ? ImagesToPdf(request)
                : request.CapabilityId == PdfRenderCapabilities.ThumbnailsId ? Thumbnails(request)
                : request.CapabilityId == PdfRenderCapabilities.ToPowerPointId ? ToPowerPoint(request)
                : request.CapabilityId == PdfRenderCapabilities.ToExcelId ? ToExcel(request)
                : PdfRenderCapabilities.PageFormatOf(request.CapabilityId) is { } format ? ToImages(request, format)
                : ToText(request, markdown: request.CapabilityId == PdfRenderCapabilities.ToMarkdownId);
        }
        catch (RenderFailure ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (ImagingException ex) { return JobResult.Fail(id, ex.Code, ex.Message); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return JobResult.Fail(id, ErrorCode.SourceNotFound, "The file is no longer there."); }
        catch (UnauthorizedAccessException) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable."); }
        catch (OutOfMemoryException) { return JobResult.Fail(id, ErrorCode.ImageTooLarge, "Not enough memory for this file."); }
        catch (IOException ex) { return JobResult.Fail(id, ErrorCode.OutputDirectoryNotWritable, $"Writing failed (0x{ex.HResult:X8})."); }
    }

    // ---- Reading ----

    private static byte[] Read(string path, long max)
    {
        if (Directory.Exists(path)) throw new RenderFailure(ErrorCode.IsAFolder, "The source is a folder, not a file.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & (FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000)) != 0)
            throw new RenderFailure(ErrorCode.OnlineOnly, "The file is online only; reading it would download it.");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > max) throw new RenderFailure(ErrorCode.InputTooLarge, $"The file is larger than {max >> 20} MB.");
        if (fs.Length == 0) throw new RenderFailure(ErrorCode.DecodeFailed, "The file is empty.");
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    private static PdfDocument OpenPdf(string path)
    {
        var bytes = Read(path, PdfRenderCapabilities.MaxPdfBytes);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 1024)).IndexOf("%PDF-"u8) < 0) // where readers look for the header
            throw new RenderFailure(ErrorCode.ContentMismatch, "The file is not a PDF, whatever its name says.");
        var doc = PdfDocument.Open(bytes, out var failure);
        if (doc is null)
            throw failure == PdfOpenFailure.Password
                ? new RenderFailure(ErrorCode.PasswordRequired, "The PDF is protected with a password: unlock it first (PDF → Protect → Unlock…).")
                : new RenderFailure(ErrorCode.DecodeFailed, "The PDF could not be read; it may be damaged.");
        if (doc.PageCount <= 0)
        {
            doc.Dispose();
            throw new RenderFailure(ErrorCode.DecodeFailed, "The PDF has no pages.");
        }
        return doc;
    }

    // ---- PDF to images ----

    private static JobResult ToImages(JobRequest request, ImageFormat format)
    {
        var path = request.Inputs[0];
        using var doc = OpenPdf(path);
        var pages = Math.Min(doc.PageCount, PdfRenderCapabilities.MaxPages);
        var ext = format == ImageFormat.Png ? ".png" : ".jpg";
        var notices = doc.PageCount > pages ? new List<JobFact> { new("Pages", $"The first {pages} of {doc.PageCount}", Notice: true) } : [];
        if (pages == 1)
        {
            var bytes = RenderPage(doc, 0, format);
            return CommitFile(request, path, bytes, PdfRenderCapabilities.Candidates(request.CapabilityId, path, request.OutputDirectory, onePage: true), check: b => _ = ImagePipeline.Decode(b, format), notices);
        }
        var staging = Staging(path, request);
        var committed = false;
        try
        {
            for (var i = 0; i < pages; i++)
            {
                var bytes = RenderPage(doc, i, format);
                var file = Path.Combine(staging, PdfRenderCapabilities.PageFileName(i, pages, ext));
                using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.Write(bytes);
            }
            if (MarkOfTheWeb.ZoneToCarry(path) is { } z) foreach (var f in Directory.GetFiles(staging)) MarkOfTheWeb.Apply(f, z);
            File.SetAttributes(staging, FileAttributes.Directory);
            var shape = FolderManifest.HashOf(staging) ?? throw new RenderFailure(ErrorCode.OutputValidationFailed, "The pages did not check out; nothing was kept.");
            foreach (var candidate in PdfRenderCapabilities.Candidates(request.CapabilityId, path, request.OutputDirectory))
            {
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                try { Directory.Move(staging, candidate); }
                catch (IOException) { continue; }
                committed = true;
                return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, shape)],
                    [new("Pages", pages.ToString("N0", CultureInfo.InvariantCulture), Notice: true), .. notices]);
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free folder name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) { try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    /// <summary>
    /// Organize pages' thumbnails: every page drawn <see cref="PdfRenderCapabilities.ThumbnailSide"/> pixels on its longer
    /// side, as "page 001.png" and on, into the empty folder the host made for them in its own temporary folder (never next
    /// to the user's files). Nothing else is written there; the count is the one fact.
    /// </summary>
    private static JobResult Thumbnails(JobRequest request)
    {
        var folder = request.OutputDirectory;
        if (folder is null || !Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any())
            throw new RenderFailure(ErrorCode.InvalidRequest, "Thumbnails go into an empty folder the host made for them.");
        using var doc = OpenPdf(request.Inputs[0]);
        if (doc.PageCount > PdfRenderCapabilities.MaxPages)
            throw new RenderFailure(ErrorCode.LimitExceeded, $"Edit pages takes PDFs of up to {PdfRenderCapabilities.MaxPages} pages; this one has {doc.PageCount:N0}.");
        for (var i = 0; i < doc.PageCount; i++)
        {
            var size = doc.PageSize(i) ?? throw new RenderFailure(ErrorCode.DecodeFailed, $"Page {i + 1} could not be read.");
            var fit = PdfRenderCapabilities.ThumbnailSide / Math.Max(1, Math.Max(size.Width, size.Height));
            var (w, h) = (Math.Max(1, (int)Math.Round(size.Width * fit)), Math.Max(1, (int)Math.Round(size.Height * fit)));
            var pixels = doc.Render(i, w, h) ?? throw new RenderFailure(ErrorCode.DecodeFailed, $"Page {i + 1} could not be drawn.");
            var bytes = ImagePipeline.Encode(new RasterImage(w, h, pixels, 96, 96), ImageFormat.Png);
            using var fs = new FileStream(Path.Combine(folder, PdfRenderCapabilities.ThumbnailName(i)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(bytes);
        }
        return new JobResult(request.Id, true, ErrorCode.None, null, [], [new("Pages", doc.PageCount.ToString(CultureInfo.InvariantCulture))]);
    }

    /// <summary>One page as an encoded picture, decoded again to check it; a page larger than the image limits is drawn smaller.</summary>
    private static byte[] RenderPage(PdfDocument doc, int index, ImageFormat format)
    {
        var size = doc.PageSize(index) ?? throw new RenderFailure(ErrorCode.DecodeFailed, $"Page {index + 1} could not be read.");
        var scale = PdfRenderCapabilities.RenderDpi / 72.0;
        double w = Math.Max(1, size.Width * scale), h = Math.Max(1, size.Height * scale);
        var fit = Math.Min(1, Math.Min(Math.Min(RasterImage.MaxDimension / w, RasterImage.MaxDimension / h), Math.Sqrt(Math.Min(RasterImage.MaxPixels, 60_000_000) / (w * h))));
        var (pw, ph) = (Math.Max(1, (int)Math.Floor(w * fit)), Math.Max(1, (int)Math.Floor(h * fit)));
        var pixels = doc.Render(index, pw, ph) ?? throw new RenderFailure(ErrorCode.DecodeFailed, $"Page {index + 1} could not be drawn.");
        var dpi = PdfRenderCapabilities.RenderDpi * fit;
        var bytes = ImagePipeline.Encode(new RasterImage(pw, ph, pixels, dpi, dpi), format);
        ImagePipeline.Validate(bytes, format, pw, ph);
        return bytes;
    }

    // ---- PDF to PowerPoint ----

    /// <summary>
    /// Every page drawn as a JPEG (as To JPG draws it) and placed on a slide of its own (<see cref="SlideDeckWriter"/>). The
    /// deck is read back before it is kept: every part it should hold and nothing else, each picture a JPEG of the page.
    /// </summary>
    private static JobResult ToPowerPoint(JobRequest request)
    {
        var path = request.Inputs[0];
        using var doc = OpenPdf(path);
        var pages = Math.Min(doc.PageCount, PdfRenderCapabilities.MaxPages);
        var slides = new List<SlideDeckWriter.Slide>(pages);
        long total = 0;
        for (var i = 0; i < pages; i++)
        {
            var size = doc.PageSize(i) ?? throw new RenderFailure(ErrorCode.DecodeFailed, $"Page {i + 1} could not be read.");
            var jpeg = RenderPage(doc, i, ImageFormat.Jpeg);
            if ((total += jpeg.Length) > MaxDeckBytes) throw new RenderFailure(ErrorCode.LimitExceeded, $"The deck would be larger than {MaxDeckBytes >> 20} MB.");
            slides.Add(new(jpeg, size.Width, size.Height));
        }
        var bytes = SlideDeckWriter.Write(slides);
        var notices = new List<JobFact> { new("Pages", pages.ToString("N0", CultureInfo.InvariantCulture), Notice: true) };
        if (doc.PageCount > pages) notices.Add(new("Pages", $"The first {pages} of {doc.PageCount}", Notice: true));
        return CommitFile(request, path, bytes, PdfRenderCapabilities.Candidates(request.CapabilityId, path, request.OutputDirectory),
            check: b => CheckDeck(b, slides), notices);
    }

    /// <summary>The most picture bytes one deck holds (it is built in memory).</summary>
    private const long MaxDeckBytes = 600L << 20;

    private static void CheckDeck(byte[] bytes, List<SlideDeckWriter.Slide> slides)
    {
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes), System.IO.Compression.ZipArchiveMode.Read);
            var expected = SlideDeckWriter.PartNames(slides.Count);
            if (zip.Entries.Count != expected.Count || zip.Entries.Any(e => !expected.Contains(e.FullName)))
                throw new RenderFailure(ErrorCode.OutputValidationFailed, "The deck did not check out; nothing was saved.");
            for (var i = 0; i < slides.Count; i++)
            {
                using var s = zip.GetEntry($"ppt/media/image{i + 1}.jpeg")!.Open();
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                if (!copy.ToArray().AsSpan().SequenceEqual(slides[i].Jpeg))
                    throw new RenderFailure(ErrorCode.OutputValidationFailed, "The deck did not check out; nothing was saved.");
            }
        }
        catch (InvalidDataException) { throw new RenderFailure(ErrorCode.OutputValidationFailed, "The deck did not check out; nothing was saved."); }
    }

    // ---- PDF to Excel ----

    /// <summary>
    /// Every page's text laid out as rows and columns (<see cref="PdfTables"/>) in one sheet, pages one after another with
    /// an empty row between them, written by the same writer as CSV to Excel: every cell is text or a plain number, never a
    /// formula. The workbook is opened again before it is kept.
    /// </summary>
    private static JobResult ToExcel(JobRequest request)
    {
        var path = request.Inputs[0];
        using var doc = OpenPdf(path);
        var pages = Math.Min(doc.PageCount, PdfRenderCapabilities.MaxPages);
        var rows = new List<string[]>();
        for (var i = 0; i < pages; i++)
        {
            var glyphs = doc.Glyphs(i, MaxCharsPerPage) ?? throw new RenderFailure(ErrorCode.DecodeFailed, $"Page {i + 1} could not be read.");
            var table = PdfTables.Rows(glyphs, doc.VerticalRules(i));
            if (table.Count == 0) continue;
            if (rows.Count > 0) rows.Add([]);
            rows.AddRange(table);
            if (rows.Count > MaxExcelRows) throw new RenderFailure(ErrorCode.LimitExceeded, $"More than {MaxExcelRows:N0} rows.");
        }
        if (rows.Count == 0) throw new RenderFailure(ErrorCode.NothingToDo, "The PDF has no text; a scan has only pictures.");
        var width = rows.Max(r => r.Length);
        var (bytes, replaced) = WinTools.Worker.Common.XlsxWriter.Build([.. rows.Select(r => r.Length == width ? r : [.. r, .. Enumerable.Repeat("", width - r.Length)])],
            WinTools.Worker.Common.XlsxWriter.SheetName(Path.GetFileNameWithoutExtension(path)));
        var notices = new List<JobFact>();
        if (doc.PageCount > pages) notices.Add(new("Pages", $"The first {pages} of {doc.PageCount}", Notice: true));
        if (replaced > 0) notices.Add(new("Changed", $"{replaced} characters Excel cannot hold were replaced", Notice: true));
        return CommitFile(request, path, bytes, PdfRenderCapabilities.Candidates(request.CapabilityId, path, request.OutputDirectory),
            check: b =>
            {
                try
                {
                    using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(b), System.IO.Compression.ZipArchiveMode.Read);
                    if (zip.GetEntry("xl/workbook.xml") is null) throw new RenderFailure(ErrorCode.OutputValidationFailed, "The workbook did not check out; nothing was saved.");
                }
                catch (InvalidDataException) { throw new RenderFailure(ErrorCode.OutputValidationFailed, "The workbook did not check out; nothing was saved."); }
            },
            notices);
    }

    /// <summary>The most rows one workbook gets from a PDF.</summary>
    private const int MaxExcelRows = 200_000;

    // ---- PDF to text ----

    private static JobResult ToText(JobRequest request, bool markdown)
    {
        var path = request.Inputs[0];
        using var doc = OpenPdf(path);
        var pages = Math.Min(doc.PageCount, PdfRenderCapabilities.MaxPages);
        var sb = new StringBuilder();
        var any = false;
        for (var i = 0; i < pages; i++)
        {
            // A page PDFium cannot read fails the export, as it fails To PNG: it is never written as an empty page (review, #39).
            var text = Scalars(doc.Text(i, MaxCharsPerPage) ?? throw new RenderFailure(ErrorCode.DecodeFailed, $"Page {i + 1} could not be read."));
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\0", "", StringComparison.Ordinal);
            any |= !string.IsNullOrWhiteSpace(text);
            if (markdown)
            {
                sb.Append(CultureInfo.InvariantCulture, $"## Page {i + 1}\n\n");
                foreach (var line in text.Split('\n')) sb.Append(MarkdownLine(line)).Append('\n');
                sb.Append('\n');
            }
            else
            {
                if (i > 0) sb.Append("\n\n");
                sb.Append(text.TrimEnd());
            }
        }
        if (!any) throw new RenderFailure(ErrorCode.NothingToDo, "The PDF has no text; a scan has only pictures.");
        var content = sb.ToString().TrimEnd('\n').Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        var bytes = new UTF8Encoding(false).GetBytes(content);
        var notices = doc.PageCount > pages ? new List<JobFact> { new("Pages", $"The first {pages} of {doc.PageCount}", Notice: true) } : [];
        return CommitFile(request, path, bytes, PdfRenderCapabilities.Candidates(request.CapabilityId, path, request.OutputDirectory),
            check: b => { if (!new UTF8Encoding(false, true).GetString(b).Equals(content, StringComparison.Ordinal)) throw new RenderFailure(ErrorCode.OutputValidationFailed, "The text did not check out."); },
            notices);
    }

    /// <summary>
    /// A line of the PDF's text as Markdown that shows it as written, so no text becomes markup, a link, a picture or HTML
    /// (review, #39: a PDF's invisible text could carry an image link to a network share, which a previewer would fetch):
    /// <list type="bullet">
    /// <item>no leading spaces (four would make a code block);</item>
    /// <item>every character that makes inline markup escaped with a backslash (the backslash itself, ` * _ [ ] &lt; &gt; ! | ~
    /// and &amp;, so an entity stays as written);</item>
    /// <item>a leading block mark escaped: #, +, -, =, and a number followed by "." or ")".</item>
    /// </list>
    /// </summary>
    internal static string MarkdownLine(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return "";
        var sb = new StringBuilder(t.Length + 8);
        foreach (var c in t)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '!' or '|' or '~' or '&') sb.Append('\\');
            sb.Append(c);
        }
        if (t[0] is '#' or '+' or '-' or '=') sb.Insert(0, '\\');
        var digits = 0;
        while (digits < t.Length && char.IsAsciiDigit(t[digits])) digits++;
        if (digits > 0 && digits < t.Length && t[digits] is '.' or ')') sb.Insert(digits, '\\');
        return sb.ToString();
    }

    /// <summary>Text with every unpaired surrogate (a malformed ToUnicode map, a page cut mid-pair) as U+FFFD, so it encodes as UTF-8 exactly.</summary>
    internal static string Scalars(string text)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var paired = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            if (paired || !char.IsSurrogate(c))
            {
                sb?.Append(c);
                if (paired) { sb?.Append(text[i + 1]); i++; }
                continue;
            }
            sb ??= new StringBuilder(text.Length).Append(text, 0, i);
            sb.Append('\uFFFD');
        }
        return sb?.ToString() ?? text;
    }

    // ---- Images to PDF ----

    private static JobResult ImagesToPdf(JobRequest request)
    {
        var ordered = PdfRenderCapabilities.PageOrder(request.Inputs);
        var first = ordered[0];
        Destination(first, request.OutputDirectory);
        var temp = OutputNaming.TempPath(first, request.Id, request.OutputDirectory);
        var committed = false;
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var writer = new ImagePdfWriter(fs);
                writer.Begin();
                // The new PDF is read again whole to check it: its pictures together stay within what the worker can hold (review, #39).
                if (ordered.Sum(p => new FileInfo(p).Length) > PdfRenderCapabilities.MaxImagesBytes)
                    throw new RenderFailure(ErrorCode.InputTooLarge, $"The pictures add up to more than {PdfRenderCapabilities.MaxImagesBytes >> 20} MB; make a PDF of fewer at a time.");
                foreach (var path in ordered)
                {
                    var bytes = Read(path, ConversionJob.MaxInputBytes);
                    var format = FileSignature.Detect(bytes);
                    if (format == ImageFormat.Unknown) throw new RenderFailure(ErrorCode.ContentMismatch, $"{Path.GetFileName(path)} is not a picture WinTools reads, whatever its name says.");
                    var (image, orientation) = ImagePipeline.Decode(bytes, format);
                    var components = format == ImageFormat.Jpeg && orientation is < 2 or > 8 ? ImagePdfWriter.EmbeddableJpegComponents(bytes) : 0;
                    // A JPEG goes in as it is, but without its metadata (EXIF, GPS, XMP, thumbnails, second pictures): the
                    // product never copies metadata (review, #39). One whose structure cannot be followed is re-encoded.
                    var stripped = components > 0 ? Metadata.Strip(bytes, ImageFormat.Jpeg, orientation: 1) : null;
                    writer.AddPage(Orientation.Apply(image, orientation), stripped, stripped is null ? 0 : components);
                }
                writer.End();
                fs.Flush(flushToDisk: true);
            }
            File.SetAttributes(temp, FileAttributes.Hidden);
            var written = File.ReadAllBytes(temp);
            using (var check = PdfDocument.Open(written, out _))
                if (check is null || check.PageCount != ordered.Count) throw new RenderFailure(ErrorCode.OutputValidationFailed, "The new PDF did not check out; nothing was kept.");
            var zone = ordered.Select(MarkOfTheWeb.ZoneToCarry).Aggregate((int?)null, (a, b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value));
            if (zone is { } z) MarkOfTheWeb.Apply(temp, z);
            File.SetAttributes(temp, FileAttributes.Normal);
            var hash = Convert.ToHexStringLower(SHA256.HashData(written));
            foreach (var candidate in PdfRenderCapabilities.Candidates(request.CapabilityId, first, request.OutputDirectory))
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
            if (!committed) DeleteQuietly(temp);
        }
    }

    // ---- Output ----

    private static DirectoryInfo Destination(string source, string? outputDirectory)
    {
        var dir = new DirectoryInfo(outputDirectory ?? Path.GetDirectoryName(source)!);
        if (!dir.Exists) throw new RenderFailure(outputDirectory is null ? ErrorCode.SourceNotFound : ErrorCode.OutputDirectoryNotWritable, "The destination folder does not exist.");
        for (var d = dir; d is not null; d = d.Parent)
            if (d.LinkTarget is not null) throw new RenderFailure(ErrorCode.OutputDirectoryIsLink, "The destination folder is, or is inside, a symbolic link or junction.");
        return dir;
    }

    private static string Staging(string source, JobRequest request)
    {
        Destination(source, request.OutputDirectory);
        var staging = OutputNaming.TempPath(source, request.Id, request.OutputDirectory);
        switch (ExclusiveDirectory.Create(staging))
        {
            case CreateOutcome.Created: break;
            case CreateOutcome.NotWritable: throw new RenderFailure(ErrorCode.OutputDirectoryNotWritable, "The destination folder is not writable.");
            default: throw new RenderFailure(ErrorCode.Internal, "Something else is using this job's folder in the destination.");
        }
        File.SetAttributes(staging, FileAttributes.Hidden | FileAttributes.Directory);
        return staging;
    }

    private static JobResult CommitFile(JobRequest request, string source, byte[] bytes, IEnumerable<string> candidates, Action<byte[]> check, IReadOnlyList<JobFact> notices)
    {
        Destination(source, request.OutputDirectory);
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
            check(File.ReadAllBytes(temp));
            if (MarkOfTheWeb.ZoneToCarry(source) is { } z) MarkOfTheWeb.Apply(temp, z);
            File.SetAttributes(temp, FileAttributes.Normal);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            foreach (var candidate in candidates)
            {
                try
                {
                    File.Move(temp, candidate, overwrite: false);
                    committed = true;
                    return new JobResult(request.Id, true, ErrorCode.None, null, [new JobOutput(candidate, hash)], notices);
                }
                catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists) { }
            }
            return JobResult.Fail(request.Id, ErrorCode.NoFreeOutputName, $"No free output name after {OutputNaming.MaxCollisionIndex} attempts.");
        }
        finally
        {
            if (!committed) DeleteQuietly(temp);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
