using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Imaging;
using WinTools.Worker.Pdf;

namespace WinTools.Worker.Tests;

/// <summary>
/// PDF to images and text, and images to PDF (ADR-0029), on the bundled PDFium: results open and are believed by the
/// host, sources stay byte-identical, nothing temporary is left, a taken name is never overwritten, and files that are
/// not PDFs, are empty, or need a password are refused with a reason.
/// </summary>
public sealed class PdfRenderTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    /// <summary>A PDF with real text (Helvetica, which every reader has): the given lines on each page.</summary>
    private static byte[] TextPdf(params string[][] pages)
    {
        var n = pages.Length;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{4 + i} 0 R"))}] /Count {n} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (var i = 0; i < n; i++)
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {4 + n + i} 0 R >>");
        foreach (var lines in pages)
        {
            var text = new StringBuilder("BT /F1 18 Tf 72 720 Td 24 TL");
            foreach (var l in lines) text.Append($" ({l}) Tj T*");
            text.Append(" ET");
            objects.Add($"<< /Length {text.Length} >>\nstream\n{text}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) pdf.Append($"{o:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static JobRequest Request(string capability, params string[] inputs) => new(Guid.NewGuid().ToString("N"), capability, inputs);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string[] Names() => [.. Directory.EnumerateFileSystemEntries(_dir.Path).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    private static JobResult RunBelieved(JobRequest request)
    {
        var r = PdfRenderJobs.Run(request);
        Assert.True(r.Success, $"{request.CapabilityId}: {r.Error} {r.Message}");
        Assert.True(PdfRenderJobModule.Instance.IsBelievable(request, r), $"{request.CapabilityId}: the host does not believe it");
        return r;
    }

    [Fact]
    public void Pages_become_PNG_files_in_a_new_folder_at_150_dpi()
    {
        var pdf = _dir.Write("report.pdf", TextPdf(["Hello page 1"], ["Hello page 2"], ["Hello page 3"]));
        var before = Hash(pdf);
        var r = RunBelieved(Request(PdfRenderCapabilities.ToPngId, pdf));
        var folder = r.Outputs.Single().Path;
        Assert.Equal("report (PNG)", Path.GetFileName(folder));
        var pages = Directory.GetFiles(folder).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["page 001.png", "page 002.png", "page 003.png"], pages.Select(p => Path.GetFileName(p)));
        var (image, _) = ImagePipeline.Decode(File.ReadAllBytes(pages[0]), Core.Files.ImageFormat.Png);
        Assert.Equal((1275, 1650), (image.Width, image.Height)); // 8.5 × 11 in at 150 dpi
        Assert.True(image.Bgra.Where((_, i) => i % 4 == 0).Any(b => b < 128), "the text is drawn"); // not a blank page
        Assert.Equal(before, Hash(pdf));
        Assert.Equal(["report (PNG)", "report.pdf"], Names());
    }

    [Fact]
    public void A_one_page_PDF_becomes_one_JPG_file()
    {
        var pdf = _dir.Write("one.pdf", TextPdf(["Only page"]));
        var r = RunBelieved(Request(PdfRenderCapabilities.ToJpgId, pdf));
        Assert.Equal("one.jpg", Path.GetFileName(r.Outputs.Single().Path));
        var (image, _) = ImagePipeline.Decode(File.ReadAllBytes(r.Outputs[0].Path), Core.Files.ImageFormat.Jpeg);
        Assert.Equal((1275, 1650), (image.Width, image.Height));
    }

    [Fact]
    public void Text_is_saved_page_by_page_as_TXT_and_as_Markdown()
    {
        var pdf = _dir.Write("notes.pdf", TextPdf(["First line", "Second line"], ["# not a heading", "1. not a list"]));
        var txt = RunBelieved(Request(PdfRenderCapabilities.ToTextId, pdf));
        Assert.Equal("notes.txt", Path.GetFileName(txt.Outputs[0].Path));
        var text = File.ReadAllText(txt.Outputs[0].Path, Encoding.UTF8);
        Assert.Contains("First line", text, StringComparison.Ordinal);
        Assert.Contains("# not a heading", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Second line", StringComparison.Ordinal) < text.IndexOf("# not a heading", StringComparison.Ordinal));
        var md = RunBelieved(Request(PdfRenderCapabilities.ToMarkdownId, pdf));
        Assert.Equal("notes.md", Path.GetFileName(md.Outputs[0].Path));
        var markdown = File.ReadAllText(md.Outputs[0].Path, Encoding.UTF8);
        Assert.Contains("## Page 1", markdown, StringComparison.Ordinal);
        Assert.Contains("## Page 2", markdown, StringComparison.Ordinal);
        Assert.Contains("\\# not a heading", markdown, StringComparison.Ordinal); // shown as written, not as a heading
        Assert.Contains("1\\. not a list", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# title", "\\# title")]
    [InlineData("    code?", "code?")]
    [InlineData("12) item", "12\\) item")]
    [InlineData("a <b> c", "a \\<b\\> c")]
    [InlineData("![x](\\\\host\\s\\a.png)", "\\!\\[x\\](\\\\\\\\host\\\\s\\\\a.png)")] // no picture fetched from a share (review, #39)
    [InlineData("[click](javascript:x)", "\\[click\\](javascript:x)")]
    [InlineData("&lt; and &copy;", "\\&lt; and \\&copy;")]
    [InlineData("*b* _c_ `d` ~e~ |t|", "\\*b\\* \\_c\\_ \\`d\\` \\~e\\~ \\|t\\|")]
    [InlineData("- item", "\\- item")]
    [InlineData("ends with \\", "ends with \\\\")]
    [InlineData("plain 1. text", "plain 1. text")]
    [InlineData("", "")]
    public void Markdown_lines_show_the_text_as_written(string line, string expected) => Assert.Equal(expected, PdfRenderJobs.MarkdownLine(line));

    [Fact]
    public void An_unpaired_surrogate_becomes_a_replacement_character_not_a_failed_export()
    {
        Assert.Equal("a\uFFFDb", PdfRenderJobs.Scalars("a\uD800b"));
        Assert.Equal("a\uFFFD", PdfRenderJobs.Scalars("a\uD83D")); // a pair cut at the page's character limit
        Assert.Equal("\uFFFDz", PdfRenderJobs.Scalars("\uDC00z"));
        Assert.Equal("ok \uD83D\uDE00", PdfRenderJobs.Scalars("ok \uD83D\uDE00"));
    }

    [Fact]
    public void A_jpeg_goes_into_the_pdf_without_its_metadata()
    {
        // An APP1 XMP segment with a place in it, right after SOI: the product never copies metadata (review, #39).
        var jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "quadrants-64x48.jpg"));
        var xmp = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x>GPS-SECRET-51.5N</x>");
        var segment = new byte[4 + xmp.Length];
        (segment[0], segment[1], segment[2], segment[3]) = (0xFF, 0xE1, (byte)((xmp.Length + 2) >> 8), (byte)(xmp.Length + 2));
        xmp.CopyTo(segment, 4);
        var tagged = _dir.Write("photo.jpg", [.. jpeg[..2], .. segment, .. jpeg[2..]]);
        var r = RunBelieved(Request(PdfRenderCapabilities.ImagesToPdfId, tagged));
        var pdf = File.ReadAllBytes(r.Outputs[0].Path);
        Assert.Equal(-1, pdf.AsSpan().IndexOf("GPS-SECRET"u8));
        Assert.True(pdf.AsSpan().IndexOf("/DCTDecode"u8) > 0); // still embedded as a JPEG, not re-encoded
    }

    [Fact]
    public void The_host_believes_a_page_folder_only_with_exactly_its_pages()
    {
        var pdf = _dir.Write("doc.pdf", TextPdf(["One"], ["Two"], ["Three"]));
        var request = Request(PdfRenderCapabilities.ToPngId, pdf);
        var r = RunBelieved(request);
        var folder = r.Outputs[0].Path;
        // Another file beside the pages (review, #39: a compromised worker could leave anything there).
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "x");
        Assert.False(PdfRenderJobModule.Instance.IsBelievable(request, r));
        File.Delete(Path.Combine(folder, "readme.txt"));
        Assert.True(PdfRenderJobModule.Instance.IsBelievable(request, r));
        // A page that is not a picture, under its right name and size.
        var page = Path.Combine(folder, "page 002.png");
        var size = new FileInfo(page).Length;
        File.WriteAllBytes(page, new byte[size]);
        Assert.False(PdfRenderJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void A_PDF_without_text_says_so_and_writes_nothing()
    {
        // Pages that only draw (a scan is pictures): a line, no text operators.
        var pdf = _dir.Write("scan.pdf", TextPdf([], []));
        var r = PdfRenderJobs.Run(Request(PdfRenderCapabilities.ToTextId, pdf));
        Assert.Equal(ErrorCode.NothingToDo, r.Error);
        Assert.True(PdfRenderJobModule.Instance.IsBelievable(Request(PdfRenderCapabilities.ToTextId, pdf), r));
        Assert.Equal(["scan.pdf"], Names());
    }

    [Fact]
    public void Files_that_are_not_PDFs_empty_or_locked_are_refused_with_a_reason()
    {
        Assert.Equal(ErrorCode.ContentMismatch, PdfRenderJobs.Run(Request(PdfRenderCapabilities.ToPngId, _dir.Write("fake.pdf", "just text"u8.ToArray()))).Error);
        Assert.Equal(ErrorCode.DecodeFailed, PdfRenderJobs.Run(Request(PdfRenderCapabilities.ToPngId, _dir.Write("empty.pdf", []))).Error);
        Assert.Equal(ErrorCode.DecodeFailed, PdfRenderJobs.Run(Request(PdfRenderCapabilities.ToPngId, _dir.Write("cut.pdf", "%PDF-1.4\n1 0 obj\n<<"u8.ToArray()))).Error);
        // A PDF protected by the PDF module's own Protect.
        var plain = _dir.Write("plain.pdf", TextPdf(["secret"]));
        var locked = PdfJobs.Run(new JobRequest(Guid.NewGuid().ToString("N"), PdfCapabilities.ProtectId, [plain],
            Parameters: new Dictionary<string, string> { [PasswordPolicy.ParameterName] = "correct horse" }));
        Assert.True(locked.Success, locked.Message);
        var r = PdfRenderJobs.Run(Request(PdfRenderCapabilities.ToTextId, locked.Outputs[0].Path));
        Assert.Equal(ErrorCode.PasswordRequired, r.Error);
        Assert.DoesNotContain(Names(), n => n.EndsWith(".txt", StringComparison.Ordinal) || n.StartsWith('.') || n.StartsWith('~'));
    }

    [Fact]
    public void Pictures_become_one_PDF_in_name_order_each_page_its_pictures_size()
    {
        var a = _dir.CopySample("quadrants-64x48.png", "scan 10.png");
        var b = _dir.CopySample("photo-960x720.jpg", "scan 2.jpg");
        var c = _dir.CopySample("quadrants-64x48.webp", "scan 3.webp");
        var jpegBytes = File.ReadAllBytes(b);
        var r = RunBelieved(Request(PdfRenderCapabilities.ImagesToPdfId, a, b, c));
        var output = r.Outputs.Single().Path;
        Assert.Equal("scan 2.pdf", Path.GetFileName(output)); // named after the first page: "scan 2" comes before "scan 10"
        var bytes = File.ReadAllBytes(output);
        using var doc = PdfDocument.Open(bytes, out _)!;
        Assert.Equal(3, doc.PageCount);
        var first = doc.PageSize(0)!.Value;
        Assert.Equal(960 * 72.0 / 96, first.Width, 0.5); // the JPEG's 96 dpi (or its own)
        Assert.True(bytes.AsSpan().IndexOf(jpegBytes) >= 0, "an upright 8-bit JPEG is embedded as it is, without loss");
        Assert.Equal(3, Directory.GetFiles(_dir.Path).Length - 1); // the three pictures and the PDF, nothing else
    }

    [Fact]
    public void A_picture_that_is_not_one_stops_the_PDF_and_leaves_nothing()
    {
        var a = _dir.CopySample("quadrants-64x48.png", "a.png");
        var fake = _dir.Write("b.jpg", "not a picture"u8.ToArray());
        var r = PdfRenderJobs.Run(Request(PdfRenderCapabilities.ImagesToPdfId, a, fake));
        Assert.Equal(ErrorCode.ContentMismatch, r.Error);
        Assert.Equal(["a.png", "b.jpg"], Names());
    }

    [Fact]
    public void An_existing_name_is_never_overwritten()
    {
        var pdf = _dir.Write("notes.pdf", TextPdf(["Text"]));
        File.WriteAllText(Path.Combine(_dir.Path, "notes.txt"), "mine");
        var r = RunBelieved(Request(PdfRenderCapabilities.ToTextId, pdf));
        Assert.Equal("notes (1).txt", Path.GetFileName(r.Outputs[0].Path));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir.Path, "notes.txt")));
    }

    [Fact]
    public void Jpeg_frames_that_can_be_embedded_are_told_from_those_that_cannot()
    {
        Assert.Equal(3, ImagePdfWriter.EmbeddableJpegComponents(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "photo-960x720.jpg"))));
        Assert.Equal(0, ImagePdfWriter.EmbeddableJpegComponents([0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x10, 0x04, 0, 0, 0, 0, 0, 0, 0, 0])); // CMYK
        Assert.Equal(0, ImagePdfWriter.EmbeddableJpegComponents([0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x0C, 0x00, 0x10, 0x00, 0x10, 0x03, 0, 0, 0, 0, 0, 0, 0, 0])); // 12-bit
        Assert.Equal(0, ImagePdfWriter.EmbeddableJpegComponents([0xFF, 0xD8, 0xFF, 0xD9]));
    }

    [Fact]
    public void A_PDF_offers_Convert_first_and_keeps_six_groups_and_pictures_offer_To_PDF()
    {
        var resolver = CapabilityResolver.For(null);
        var menu = RadialMenu.Build(resolver, [@"C:\p\a.pdf"]);
        Assert.Equal(["Convert", "Tools", "Organize", "Optimize", "Protect", "Actions"], menu.Select(g => g.Label));
        Assert.Equal(["To PNG", "To JPG", "To TXT", "To MD"], menu[0].Commands.Select(c => c.Label));
        Assert.Equal(["Split into pages", "Rotate right", "Rotate left", "Rotate 180°"], menu.Single(g => g.Label == "Organize").Commands.Select(c => c.Label));
        Assert.Contains("To PDF", RadialMenu.Build(resolver, [@"C:\p\a.jpg", @"C:\p\b.png"])[0].Commands.Select(c => c.Label));
    }

    [Fact]
    public async Task The_real_worker_process_runs_a_job_the_host_believes()
    {
        var pdf = _dir.Write("doc.pdf", TextPdf(["One"], ["Two"]));
        var client = new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.exe"), TimeSpan.FromMinutes(2), PdfRenderJobModule.Instance, parallelism: 1);
        var results = await client.RunAsync([Request(PdfRenderCapabilities.ToTextId, pdf), Request(PdfRenderCapabilities.ToPngId, pdf)], CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(["doc (PNG)", "doc.pdf", "doc.txt"], Names());
    }
}
