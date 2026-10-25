using System.IO.Compression;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Text;

namespace WinTools.Worker.Tests;

/// <summary>Minimal OOXML packages made here: the parts a reader needs, nothing third-party.</summary>
internal static class OoxmlSamples
{
    public static void Write(string path, params (string Part, string Xml)[] parts)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (part, xml) in parts)
        {
            using var w = new StreamWriter(zip.CreateEntry(part).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
    }

    private const string Core = """<?xml version="1.0" encoding="UTF-8"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>تقرير الربع</dc:title><dc:creator>Ahmed</dc:creator><dcterms:modified xsi:type="dcterms:W3CDTF">2026-10-24T10:00:00Z</dcterms:modified></cp:coreProperties>""";

    public static (string, string)[] Docx(string body, bool macros = false) =>
    [
        ("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>"""),
        ("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>"""),
        ("docProps/core.xml", Core),
        .. (macros ? new[] { ("word/vbaProject.bin", "not run") } : []),
    ];

    public static (string, string)[] Pptx(params string[] slides) =>
    [
        ("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/></Types>"""),
        ("ppt/presentation.xml", """<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"/>"""),
        .. slides.Select((t, i) => ($"ppt/slides/slide{i + 1}.xml", $"""<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{t}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>""")),
    ];

    public static (string, string)[] Xlsx(string[] shared, string sheet) =>
    [
        ("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>"""),
        ("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"/>"""),
        ("xl/sharedStrings.xml", $"""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">{string.Concat(shared.Select(s => $"<si><t>{s}</t></si>"))}</sst>"""),
        ("xl/worksheets/sheet1.xml", $"""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{sheet}</sheetData></worksheet>"""),
    ];
}

/// <summary>
/// V2 PR 6.1 (ADR-0025): Word, Excel and PowerPoint files read in the text worker: their text and their properties. The
/// package passes the archive limits before any part is read; XML is read with DTDs prohibited and nothing resolved;
/// macros are never run, only reported. Written before the code.
/// </summary>
public sealed class DocumentWorkerTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static JobRequest Request(string capability, string path) => new(Guid.NewGuid().ToString("N"), capability, [path]);
    private string Make(string name, (string, string)[] parts) { var p = Path.Combine(_dir.Path, name); OoxmlSamples.Write(p, parts); return p; }

    [Fact]
    public void Word_text_comes_out_paragraph_by_paragraph()
    {
        var docx = Make("report.docx", OoxmlSamples.Docx("<w:p><w:r><w:t>مرحبا</w:t></w:r><w:r><w:t xml:space=\"preserve\"> بالعالم</w:t></w:r></w:p><w:p><w:r><w:t>Second &amp; last</w:t></w:r></w:p>"));
        var request = Request(DocumentCapabilities.ExtractTextId, docx);
        var r = DocumentJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal("report (text).txt", Path.GetFileName(r.OutputPath));
        Assert.Equal("مرحبا بالعالم\r\nSecond & last\r\n", File.ReadAllText(r.OutputPath!, Encoding.UTF8));
        Assert.True(DocumentJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void PowerPoint_text_comes_out_slide_by_slide_in_number_order()
    {
        var pptx = Make("deck.pptx", OoxmlSamples.Pptx("one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"));
        var r = DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, pptx));
        Assert.True(r.Success, r.Message);
        var text = File.ReadAllText(r.OutputPath!, Encoding.UTF8);
        Assert.True(text.IndexOf("nine", StringComparison.Ordinal) < text.IndexOf("ten", StringComparison.Ordinal)); // slide10 after slide9
        Assert.StartsWith("Slide 1\r\none\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Excel_cell_text_comes_out_row_by_row()
    {
        var xlsx = Make("book.xlsx", OoxmlSamples.Xlsx(["Name", "Ahmed"], """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1"><v>42</v></c></row><row r="2"><c r="A2" t="s"><v>1</v></c><c r="B2" t="inlineStr"><is><t>inline</t></is></c></row>"""));
        var r = DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, xlsx));
        Assert.True(r.Success, r.Message);
        Assert.Equal("Sheet 1\r\nName\t42\r\nAhmed\tinline\r\n", File.ReadAllText(r.OutputPath!, Encoding.UTF8));
    }

    [Fact]
    public void Info_names_the_title_author_and_warns_about_macros()
    {
        var docx = Make("report.docx", OoxmlSamples.Docx("<w:p><w:r><w:t>x</w:t></w:r></w:p>", macros: true));
        var facts = DocumentJobs.Run(Request(DocumentCapabilities.InfoId, docx)).Facts;
        Assert.Equal("Word document", facts.Single(f => f.Label == "Type").Value);
        Assert.Equal("تقرير الربع", facts.Single(f => f.Label == "Title").Value);
        Assert.Equal("Ahmed", facts.Single(f => f.Label == "Author").Value);
        Assert.Contains(facts, f => f is { Label: "Macros", Notice: true });
        Assert.True(JobFacts.AreShowable(facts));
    }

    [Fact]
    public void A_dtd_is_refused_so_entity_expansion_never_happens()
    {
        var bomb = """<?xml version="1.0"?><!DOCTYPE w [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>&b;</w:t></w:r></w:p></w:body></w:document>""";
        var parts = OoxmlSamples.Docx("");
        parts[1] = ("word/document.xml", bomb);
        var docx = Make("evil.docx", parts);
        var r = DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, docx));
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        Assert.Equal(["evil.docx"], _dir.Names());
    }

    [Fact]
    public void A_file_named_docx_that_is_not_a_word_document_is_refused()
    {
        var fake = _dir.Write("letter.docx", "MZ a program"u8.ToArray());
        Assert.Equal(ErrorCode.ContentMismatch, DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, fake)).Error);
        var wrong = Make("deck.docx", OoxmlSamples.Pptx("x")); // a presentation named like a document
        Assert.Equal(ErrorCode.ContentMismatch, DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, wrong)).Error);
    }

    [Fact]
    public void A_package_that_is_a_zip_bomb_is_refused_before_any_part_is_parsed()
    {
        var path = Path.Combine(_dir.Path, "bomb.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (part, xml) in OoxmlSamples.Docx("<w:p/>"))
                using (var w = new StreamWriter(zip.CreateEntry(part).Open())) w.Write(xml);
            using var s = zip.CreateEntry("word/media/zeros.bin", CompressionLevel.SmallestSize).Open();
            s.Write(new byte[64 << 20]);
        }
        Assert.Equal(ErrorCode.LimitExceeded, DocumentJobs.Run(Request(DocumentCapabilities.InfoId, path)).Error);
    }

    [Fact]
    public void The_menu_offers_text_and_info_for_office_files()
    {
        var resolver = CapabilityResolver.For(customResizeId: null);
        foreach (var name in new[] { "a.docx", "a.xlsx", "a.pptx" })
        {
            var ids = resolver.Resolve([$@"C:\p\{name}"]).Select(c => c.Id).ToArray();
            Assert.Contains(DocumentCapabilities.ExtractTextId, ids);
            Assert.Contains(DocumentCapabilities.InfoId, ids);
        }
        Assert.Equal(["Convert", "Tools"], RadialMenu.Build(resolver, [@"C:\p\a.docx"]).Select(g => g.Label));
    }
}
