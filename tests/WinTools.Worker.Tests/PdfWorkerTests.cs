using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Core.Output;
using WinTools.Host.Jobs;
using WinTools.Worker.Pdf;

namespace WinTools.Worker.Tests;

/// <summary>Minimal PDFs made here, so the corpus needs no third-party files: N pages of one text line each.</summary>
internal static class PdfSamples
{
    public static byte[] Pages(int n)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + i} 0 R"))}] /Count {n} >>",
        };
        for (var i = 0; i < n; i++)
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents {3 + n + i} 0 R >>");
        for (var i = 0; i < n; i++)
        {
            var text = $"BT /F1 24 Tf 72 700 Td (Page {i + 1}) Tj ET";
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
}

/// <summary>
/// V2 PR 2.1 (ADR-0022): the PDF worker's commands through the bundled qpdf, with the output-safety rules: the source is
/// byte-identical afterwards, nothing temporary is left, a taken name is never overwritten, and what the worker claims
/// is what the host's module believes.
/// </summary>
public sealed class PdfWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static JobRequest Request(string capability, params string[] inputs) => new(Guid.NewGuid().ToString("N"), capability, inputs);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static int PagesOf(string path)
    {
        var run = Qpdf.Run(["--show-npages", path], TimeSpan.FromSeconds(30));
        Assert.True(run.Usable);
        return int.Parse(run.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private string Pdf(string name, int pages) => _dir.Write(name, PdfSamples.Pages(pages));

    private void NoTemporaries() =>
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_dir.Path), e => Path.GetFileName(e).StartsWith("~wintools-", StringComparison.Ordinal));

    [Fact]
    public void The_bundled_qpdf_is_the_pinned_version()
    {
        Assert.True(Qpdf.IsPresent, Qpdf.ExecutablePath);
        var run = Qpdf.Run(["--version"], TimeSpan.FromSeconds(30));
        Assert.StartsWith("qpdf version 12.4.2", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Info_reports_version_pages_and_protection()
    {
        var r = PdfJobs.Run(Request(PdfCapabilities.InfoId, Pdf("a.pdf", 3)));
        Assert.True(r.Success, r.Message);
        Assert.True(JobFacts.AreShowable(r.Facts));
        var facts = r.Facts.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("PDF document", facts["Type"]);
        Assert.Equal("1.4", facts["Version"]);
        Assert.Equal("3", facts["Pages"]);
        Assert.Equal("None", facts["Protection"]);
        Assert.Equal("No", facts["Fast web view"]);
        Assert.True(PdfJobModule.Instance.IsBelievable(Request(PdfCapabilities.InfoId, _dir.Path + @"\a.pdf"), r));
    }

    [Theory]
    [InlineData(PdfCapabilities.RotateRightId, "a (rotated right).pdf", 90)]
    [InlineData(PdfCapabilities.RotateLeftId, "a (rotated left).pdf", 270)]
    [InlineData(PdfCapabilities.Rotate180Id, "a (rotated 180).pdf", 180)]
    public void Rotate_writes_a_new_pdf_with_every_page_turned(string capability, string expectedName, int degrees)
    {
        var source = Pdf("a.pdf", 3);
        var before = Hash(source);
        var request = Request(capability, source);
        var r = PdfJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal(expectedName, Path.GetFileName(r.OutputPath));
        Assert.Equal(3, PagesOf(r.OutputPath!));
        var json = Qpdf.Run(["--json=2", "--json-key=qpdf", r.OutputPath!], TimeSpan.FromSeconds(30));
        Assert.Equal(3, json.Output.Split($"\"/Rotate\": {degrees}").Length - 1); // every page object says it
        Assert.Equal(before, Hash(source));
        NoTemporaries();
        Assert.True(PdfJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void Merge_joins_pdfs_in_the_order_given()
    {
        var a = Pdf("a.pdf", 3);
        var b = Pdf("b.pdf", 2);
        var (ha, hb) = (Hash(a), Hash(b));
        var request = Request(PdfCapabilities.MergeId, a, b);
        var r = PdfJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal("a (merged).pdf", Path.GetFileName(r.OutputPath));
        Assert.Equal(5, PagesOf(r.OutputPath!));
        Assert.Equal((ha, hb), (Hash(a), Hash(b)));
        NoTemporaries();
        Assert.True(PdfJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void Split_writes_one_pdf_per_page_into_a_new_folder()
    {
        var source = Pdf("report.pdf", 12);
        var before = Hash(source);
        var request = Request(PdfCapabilities.SplitId, source);
        var r = PdfJobs.Run(request);
        Assert.True(r.Success, r.Message);
        var folder = r.Outputs.Single().Path;
        Assert.Equal("report (pages)", Path.GetFileName(folder));
        var pages = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(12, pages.Length);
        Assert.Equal("report - page 01.pdf", pages[0]);
        Assert.Equal("report - page 12.pdf", pages[^1]);
        Assert.All(Directory.GetFiles(folder), p => Assert.Equal(1, PagesOf(p)));
        Assert.Equal(FolderManifest.HashOf(folder), r.Outputs.Single().Sha256);
        Assert.Equal(before, Hash(source));
        NoTemporaries();
        Assert.True(PdfJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void A_name_that_looks_like_a_qpdf_pattern_or_option_is_only_a_name()
    {
        // qpdf expands "%d" in Split's output name and reads "@file" as an argument list: neither reaches it from a name.
        var source = Pdf("@report %d.pdf", 2);
        var r = PdfJobs.Run(Request(PdfCapabilities.SplitId, source));
        Assert.True(r.Success, r.Message);
        Assert.Equal(["@report %d - page 1.pdf", "@report %d - page 2.pdf"],
            Directory.GetFiles(r.Outputs.Single().Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Arabic_emoji_and_spaces_in_the_path_work()
    {
        var folder = Path.Combine(_dir.Path, "تقارير 😀 2026");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "ملف مهم.pdf");
        File.WriteAllBytes(source, PdfSamples.Pages(2));
        var r = PdfJobs.Run(Request(PdfCapabilities.RotateRightId, source));
        Assert.True(r.Success, r.Message);
        Assert.Equal(Path.Combine(folder, "ملف مهم (rotated right).pdf"), r.OutputPath);
        Assert.Equal(2, PagesOf(r.OutputPath!));
    }

    [Fact]
    public void A_taken_name_is_never_overwritten()
    {
        var source = Pdf("a.pdf", 1);
        var taken = _dir.Write("a (rotated right).pdf", "not mine to replace"u8.ToArray());
        var r = PdfJobs.Run(Request(PdfCapabilities.RotateRightId, source));
        Assert.True(r.Success, r.Message);
        Assert.Equal("a (rotated right) (1).pdf", Path.GetFileName(r.OutputPath));
        Assert.Equal("not mine to replace", File.ReadAllText(taken));
    }

    [Fact]
    public void A_file_named_pdf_that_is_not_one_is_refused_before_qpdf()
    {
        var fake = _dir.Write("invoice.pdf", "MZ this is a program"u8.ToArray());
        var r = PdfJobs.Run(Request(PdfCapabilities.RotateRightId, fake));
        Assert.False(r.Success);
        Assert.Equal(ErrorCode.ContentMismatch, r.Error);
        Assert.Equal(["invoice.pdf"], _dir.Names());
    }

    [Fact]
    public void A_damaged_pdf_fails_cleanly_and_leaves_nothing()
    {
        var whole = PdfSamples.Pages(3);
        var damaged = _dir.Write("broken.pdf", whole[..200]);
        var r = PdfJobs.Run(Request(PdfCapabilities.RotateRightId, damaged));
        Assert.False(r.Success);
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Equal(["broken.pdf"], _dir.Names());
        Assert.True(PdfJobModule.Instance.IsBelievable(Request(PdfCapabilities.RotateRightId, damaged), r)); // a plain failure is believed
    }

    [Fact]
    public void A_password_protected_pdf_is_refused_and_never_guessed()
    {
        var plain = Pdf("plain.pdf", 2);
        var locked = Path.Combine(_dir.Path, "locked.pdf");
        Assert.True(Qpdf.Run([plain, "--encrypt", "--user-password=u", "--owner-password=o", "--bits=256", "--", locked], TimeSpan.FromSeconds(30)).Usable);
        foreach (var capability in new[] { PdfCapabilities.RotateRightId, PdfCapabilities.SplitId })
        {
            var r = PdfJobs.Run(Request(capability, locked));
            Assert.Equal(ErrorCode.PasswordRequired, r.Error);
        }
        var merge = PdfJobs.Run(Request(PdfCapabilities.MergeId, plain, locked));
        Assert.Equal(ErrorCode.PasswordRequired, merge.Error);
        var info = PdfJobs.Run(Request(PdfCapabilities.InfoId, locked));
        Assert.True(info.Success, info.Message);
        Assert.Contains(info.Facts, f => f is { Label: "Protection", Notice: true });
        Assert.Equal(["locked.pdf", "plain.pdf"], _dir.Names());
    }

    [Fact]
    public void An_output_of_an_internet_file_carries_its_zone()
    {
        var source = Pdf("download.pdf", 2);
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/x.pdf\r\n");
        var rotated = PdfJobs.Run(Request(PdfCapabilities.RotateRightId, source));
        Assert.True(rotated.Success, rotated.Message);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(rotated.OutputPath + ":Zone.Identifier"));
        var split = PdfJobs.Run(Request(PdfCapabilities.SplitId, source));
        Assert.All(Directory.GetFiles(split.Outputs.Single().Path), p => Assert.Contains("ZoneId=3", File.ReadAllText(p + ":Zone.Identifier"), StringComparison.Ordinal));
    }

    [Fact]
    public void The_host_does_not_believe_a_claim_the_disk_contradicts()
    {
        var source = Pdf("a.pdf", 2);
        var request = Request(PdfCapabilities.RotateRightId, source);
        var r = PdfJobs.Run(request);
        Assert.True(PdfJobModule.Instance.IsBelievable(request, r));
        Assert.False(PdfJobModule.Instance.IsBelievable(request, r with { Outputs = [r.Outputs[0] with { Sha256 = new string('0', 64) }] }));
        Assert.False(PdfJobModule.Instance.IsBelievable(request, r with { Outputs = [r.Outputs[0] with { Path = source }] })); // the source itself
        var split = Request(PdfCapabilities.SplitId, source);
        var s = PdfJobs.Run(split);
        File.WriteAllBytes(Path.Combine(s.Outputs[0].Path, "extra.pdf"), PdfSamples.Pages(1));
        Assert.False(PdfJobModule.Instance.IsBelievable(split, s)); // a file the worker did not report
    }

    [Fact]
    public async Task The_real_worker_process_runs_a_job_the_host_believes()
    {
        var source = Pdf("a.pdf", 2);
        var client = new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Pdf.exe"), TimeSpan.FromMinutes(1), PdfJobModule.Instance, parallelism: 1);
        var results = await client.RunAsync([Request(PdfCapabilities.RotateRightId, source), Request(PdfCapabilities.InfoId, source)], CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal("a (rotated right).pdf", Path.GetFileName(results[0].OutputPath));
        Assert.Equal("2", results[1].Facts.Single(f => f.Label == "Pages").Value);
    }
}
