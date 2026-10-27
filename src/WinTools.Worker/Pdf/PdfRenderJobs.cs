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

    public static bool Runs(string? capabilityId) => PdfRenderCapabilities.Catalog.Any(c => c.Id == capabilityId);

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
            var digits = Math.Max(3, pages.ToString(CultureInfo.InvariantCulture).Length);
            for (var i = 0; i < pages; i++)
            {
                var bytes = RenderPage(doc, i, format);
                var file = Path.Combine(staging, $"page {(i + 1).ToString(new string('0', digits), CultureInfo.InvariantCulture)}{ext}");
                using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.Write(bytes);
            }
            if (MarkOfTheWeb.ZoneToCarry(path) is { } z) foreach (var f in Directory.GetFiles(staging)) MarkOfTheWeb.Apply(f, z);
            File.SetAttributes(staging, FileAttributes.Directory);
            var shape = FolderManifest.ShapeOf(staging) ?? throw new RenderFailure(ErrorCode.OutputValidationFailed, "The pages did not check out; nothing was kept.");
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
            var text = doc.Text(i, MaxCharsPerPage) ?? "";
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
    /// A line of the PDF's text as Markdown that shows it as written: no leading spaces (four would make a code block), a
    /// leading mark (#, &gt;, -, +, *, 1.) escaped, and "&lt;" written as an entity, so no text becomes markup or HTML.
    /// </summary>
    internal static string MarkdownLine(string line)
    {
        var t = line.Trim().Replace("<", "&lt;", StringComparison.Ordinal);
        if (t.Length == 0) return "";
        if (t[0] is '#' or '>' or '-' or '+' or '*' or '=' or '|' or '`' or '~' or '_') return "\\" + t;
        var digits = 0;
        while (digits < t.Length && char.IsAsciiDigit(t[digits])) digits++;
        return digits > 0 && digits < t.Length && t[digits] is '.' or ')' ? t.Insert(digits, "\\") : t;
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
                foreach (var path in ordered)
                {
                    var bytes = Read(path, ConversionJob.MaxInputBytes);
                    var format = FileSignature.Detect(bytes);
                    if (format == ImageFormat.Unknown) throw new RenderFailure(ErrorCode.ContentMismatch, $"{Path.GetFileName(path)} is not a picture WinTools reads, whatever its name says.");
                    var (image, orientation) = ImagePipeline.Decode(bytes, format);
                    var components = format == ImageFormat.Jpeg && orientation is < 2 or > 8 ? ImagePdfWriter.EmbeddableJpegComponents(bytes) : 0;
                    writer.AddPage(Orientation.Apply(image, orientation), components > 0 ? bytes : null, components);
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
