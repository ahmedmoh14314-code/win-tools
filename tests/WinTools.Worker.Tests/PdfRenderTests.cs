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

    [Fact]
    public void Thumbnails_draw_every_page_small_into_the_hosts_empty_folder()
    {
        var pdf = _dir.Write("three.pdf", PdfSamples.Pages(3));
        var folder = Directory.CreateDirectory(Path.Combine(_dir.Path, "thumbs")).FullName;
        var request = new JobRequest(Guid.NewGuid().ToString("N"), PdfRenderCapabilities.ThumbnailsId, [pdf], OutputDirectory: folder);
        var r = PdfRenderJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.True(PdfRenderJobModule.Instance.IsBelievable(request, r));
        Assert.Equal(["page 001.png", "page 002.png", "page 003.png"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
        var first = ImagePipeline.Decode(File.ReadAllBytes(Path.Combine(folder, "page 001.png")), WinTools.Core.Files.ImageFormat.Png);
        Assert.Equal(PdfRenderCapabilities.ThumbnailSide, Math.Max(first.Image.Width, first.Image.Height));
        Assert.Equal(["three.pdf", "thumbs"], Directory.GetFileSystemEntries(_dir.Path).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
        // A folder that is not empty is not the host's fresh one: nothing is drawn into it.
        Assert.Equal(ErrorCode.InvalidRequest, PdfRenderJobs.Run(request with { Id = Guid.NewGuid().ToString("N") }).Error);
        // A picture missing: the host does not believe the count.
        File.Delete(Path.Combine(folder, "page 003.png"));
        Assert.False(PdfRenderJobModule.Instance.IsBelievable(request, r));
    }

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
        Assert.Equal(["To PNG", "To JPG", "To TXT", "To MD", "To PPTX"], menu[0].Commands.Select(c => c.Label));
        Assert.Equal(["Edit pages…", "Split into pages", "Rotate right", "Rotate left", "Rotate 180°"], menu.Single(g => g.Label == "Organize").Commands.Select(c => c.Label));
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

    // ---- PDF to PowerPoint ----

    [Fact]
    public void A_pdf_becomes_a_deck_of_one_picture_slide_per_page_and_the_original_is_untouched()
    {
        var pdf = _dir.Write("report.pdf", TextPdf(["Hello page 1"], ["Hello page 2"], ["Hello page 3"]));
        var before = Hash(pdf);
        var r = RunBelieved(Request(PdfRenderCapabilities.ToPowerPointId, pdf));
        Assert.Equal("report.pptx", Path.GetFileName(r.OutputPath));
        Assert.Equal(before, Hash(pdf));
        Assert.Equal(["report.pdf", "report.pptx"], Names());
        using var zip = System.IO.Compression.ZipFile.OpenRead(r.OutputPath!);
        Assert.Equal(SlideDeckWriter.PartNames(3).Order(StringComparer.Ordinal), zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        var presentation = new StreamReader(zip.GetEntry("ppt/presentation.xml")!.Open()).ReadToEnd();
        Assert.Contains("<p:sldSz cx=\"7772400\" cy=\"10058400\"/>", presentation, StringComparison.Ordinal); // 8.5 × 11 in
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(presentation, "<p:sldId "));
        using var picture = zip.GetEntry("ppt/media/image2.jpeg")!.Open();
        using var copy = new MemoryStream();
        picture.CopyTo(copy);
        var (image, _) = ImagePipeline.Decode(copy.ToArray(), Core.Files.ImageFormat.Jpeg);
        Assert.Equal((1275, 1650), (image.Width, image.Height));
        foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
            Assert.DoesNotContain("External", new StreamReader(e.Open()).ReadToEnd(), StringComparison.Ordinal); // nothing outside the file
    }

    [Fact]
    public void A_deck_the_host_cannot_find_as_written_is_not_believed()
    {
        var pdf = _dir.Write("report.pdf", TextPdf(["one"]));
        var request = Request(PdfRenderCapabilities.ToPowerPointId, pdf);
        var r = RunBelieved(request);
        File.WriteAllBytes(r.OutputPath!, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
        Assert.False(PdfRenderJobModule.Instance.IsBelievable(request, r));
    }

    [Theory]
    [InlineData(612, 792, 7772400, 10058400)] // Letter, as it is
    [InlineData(20, 20, 914400, 914400)] // too small: grown to PowerPoint's 1 inch
    [InlineData(14400, 7200, 51206400, 25603200)] // too large: shrunk to 56 inches, in proportion
    public void The_slide_is_the_first_pages_size_within_powerpoints_limits(double w, double h, long cx, long cy) =>
        Assert.Equal((cx, cy), SlideDeckWriter.SlideSize(w, h));

    [Fact]
    public void A_page_of_another_shape_is_centred_on_the_slide_at_its_own_proportions()
    {
        var (x, y, cx, cy) = SlideDeckWriter.Fit(7772400, 10058400, 792, 612); // landscape page on a portrait slide
        Assert.Equal(7772400, cx);
        Assert.Equal(0, x);
        Assert.Equal((10058400 - cy) / 2, y);
        Assert.Equal(612.0 / 792, (double)cy / cx, 3);
    }

    // ---- Watermark, page numbers, crop margins ----

    private static JobRequest WithForm(string capability, string pdf, string answer) =>
        new(Guid.NewGuid().ToString("N"), capability, [pdf], Parameters: new Dictionary<string, string> { [DialogParameters.Name] = answer });

    private static double Darkness(string pdf, int page)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(pdf), out _)!;
        var pixels = doc.Render(page, 200, 260)!;
        double sum = 0;
        for (var i = 0; i < pixels.Length; i += 4) sum += 255 - pixels[i + 1];
        return sum / (pixels.Length / 4);
    }

    [Fact]
    public void A_watermark_in_any_script_is_drawn_on_every_page_and_the_original_is_untouched()
    {
        var pdf = _dir.Write("report.pdf", TextPdf(["one"], ["two"]));
        var before = Hash(pdf);
        var r = RunBelieved(WithForm(PdfRenderCapabilities.WatermarkId, pdf, Forms.Check(PdfRenderCapabilities.WatermarkId, ["سري للغاية", "40"]).Answer!));
        Assert.Equal("report (watermarked).pdf", Path.GetFileName(r.OutputPath));
        Assert.Equal(before, Hash(pdf));
        Assert.True(Darkness(r.OutputPath!, 0) > Darkness(pdf, 0) + 2);
        Assert.True(Darkness(r.OutputPath!, 1) > Darkness(pdf, 1) + 2);
    }

    [Fact]
    public void A_watermark_without_its_form_or_with_another_spelling_of_it_is_refused()
    {
        var pdf = _dir.Write("report.pdf", TextPdf(["one"]));
        Assert.Equal(ErrorCode.InvalidRequest, PdfRenderJobs.Run(Request(PdfRenderCapabilities.WatermarkId, pdf)).Error);
        Assert.Equal(ErrorCode.InvalidRequest, PdfRenderJobs.Run(WithForm(PdfRenderCapabilities.WatermarkId, pdf, "text=draft;opacity=030")).Error);
        Assert.Equal(["report.pdf"], Names());
    }

    [Fact]
    public void Pages_are_numbered_at_the_bottom_and_the_numbers_are_text()
    {
        var pdf = _dir.Write("report.pdf", TextPdf(["one"], ["two"], ["three"]));
        var r = RunBelieved(Request(PdfRenderCapabilities.PageNumbersId, pdf));
        Assert.Equal("report (numbered).pdf", Path.GetFileName(r.OutputPath));
        using var doc = PdfDocument.Open(File.ReadAllBytes(r.OutputPath!), out _)!;
        Assert.Equal(3, doc.PageCount);
        Assert.Contains("2", doc.Text(1, 1000), StringComparison.Ordinal);
        Assert.Contains("3", doc.Text(2, 1000), StringComparison.Ordinal);
    }

    [Fact]
    public void White_margins_are_cropped_to_what_is_drawn_and_a_blank_pdf_has_nothing_to_crop()
    {
        var pdf = _dir.Write("report.pdf", TextPdf(["Hello page 1"]));
        var r = RunBelieved(Request(PdfRenderCapabilities.CropMarginsId, pdf));
        Assert.Equal("report (cropped).pdf", Path.GetFileName(r.OutputPath));
        using (var doc = PdfDocument.Open(File.ReadAllBytes(r.OutputPath!), out _)!)
        {
            var (w, h) = doc.PageSize(0)!.Value;
            Assert.True(w < 400 && h < 200, $"{w} x {h}");
            Assert.Contains("Hello page 1", doc.Text(0, 1000), StringComparison.Ordinal);
        }
        var blank = _dir.Write("blank.pdf", TextPdf([" "]));
        Assert.Equal(ErrorCode.NothingToDo, PdfRenderJobs.Run(Request(PdfRenderCapabilities.CropMarginsId, blank)).Error);
    }

    [Theory]
    [InlineData(0, 10, 20, 10, 20)]
    [InlineData(90, 10, 20, 580, 10)] // a page turned right: its shown bottom is the content's right edge
    [InlineData(180, 10, 20, 590, 780)]
    [InlineData(270, 10, 20, 20, 790)]
    public void A_shown_point_is_found_on_a_turned_page(int rotation, double u, double v, double x, double y)
    {
        var frame = new PageFrame(0, 0, 600, 800, rotation);
        Assert.Equal((x, y), frame.FromShown(u, v));
    }

    [Fact]
    public void Ink_bounds_find_what_is_not_white()
    {
        var px = new byte[10 * 8 * 4];
        Array.Fill(px, (byte)255);
        Assert.Null(PdfRenderJobs.InkBounds(px, 10, 8));
        px[(3 * 10 + 2) * 4] = 0;
        px[(5 * 10 + 7) * 4 + 1] = 100;
        Assert.Equal((2, 3, 7, 5), PdfRenderJobs.InkBounds(px, 10, 8));
    }

    // ---- Sign ----

    [Fact]
    public void A_drawn_signature_goes_on_the_chosen_page_only_as_vector_ink()
    {
        var pdf = _dir.Write("contract.pdf", TextPdf(["one"], ["two"]));
        var before = Hash(pdf);
        var plan = new SignPlan(2, 0.7, 0.85, 0.35, [new InkStroke([(50, 300), (300, 80), (550, 320), (950, 60)])], null).Format();
        var r = RunBelieved(WithForm(PdfRenderCapabilities.SignId, pdf, plan));
        Assert.Equal("contract (signed).pdf", Path.GetFileName(r.OutputPath));
        Assert.Equal(before, Hash(pdf));
        Assert.True(Darkness(r.OutputPath!, 1) > Darkness(pdf, 1) + 0.3, "page 2 has no signature");
        Assert.Equal(Darkness(pdf, 0), Darkness(r.OutputPath!, 0), 3); // page 1 untouched
    }

    [Fact]
    public void A_typed_signature_goes_on_every_page()
    {
        var pdf = _dir.Write("contract.pdf", TextPdf(["one"], ["two"]));
        var plan = new SignPlan(null, 0.5, 0.5, 0.5, null, "أحمد علي").Format();
        var r = RunBelieved(WithForm(PdfRenderCapabilities.SignId, pdf, plan));
        Assert.True(Darkness(r.OutputPath!, 0) > Darkness(pdf, 0) + 0.3);
        Assert.True(Darkness(r.OutputPath!, 1) > Darkness(pdf, 1) + 0.3);
    }

    [Fact]
    public void A_signature_plan_for_a_page_the_pdf_does_not_have_signs_nothing()
    {
        var pdf = _dir.Write("contract.pdf", TextPdf(["one"]));
        var plan = new SignPlan(3, 0.5, 0.5, 0.3, [new InkStroke([(1, 1), (2, 2)])], null).Format();
        Assert.Equal(ErrorCode.InvalidRequest, PdfRenderJobs.Run(WithForm(PdfRenderCapabilities.SignId, pdf, plan)).Error);
        Assert.Equal(ErrorCode.InvalidRequest, PdfRenderJobs.Run(Request(PdfRenderCapabilities.SignId, pdf)).Error);
        Assert.Equal(["contract.pdf"], Names());
    }
}
