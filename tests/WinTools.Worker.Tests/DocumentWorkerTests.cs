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
    public void Info_reads_the_app_part_office_writes_with_its_vectors()
    {
        // Word, Excel and PowerPoint write HeadingPairs and TitlesOfParts as vt:vector elements; a property with child
        // elements is not text and must not make the part unreadable.
        const string app = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"><Template>Normal.dotm</Template><TotalTime>1</TotalTime><Pages>3</Pages><Words>120</Words><Application>Microsoft Office Word</Application><HeadingPairs><vt:vector size="2" baseType="variant"><vt:variant><vt:lpstr>Title</vt:lpstr></vt:variant><vt:variant><vt:i4>1</vt:i4></vt:variant></vt:vector></HeadingPairs><TitlesOfParts><vt:vector size="1" baseType="lpstr"><vt:lpstr></vt:lpstr></vt:vector></TitlesOfParts><Company></Company><AppVersion>16.0000</AppVersion></Properties>""";
        var docx = Make("office.docx", [.. OoxmlSamples.Docx("<w:p><w:r><w:t>x</w:t></w:r></w:p>"), ("docProps/app.xml", app)]);
        var r = DocumentJobs.Run(Request(DocumentCapabilities.InfoId, docx));
        Assert.True(r.Success, r.Message);
        Assert.Equal("3", r.Facts.Single(f => f.Label == "Pages").Value);
        Assert.Equal("120", r.Facts.Single(f => f.Label == "Words").Value);
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

    // Evaluator, PR 6.1: each finding as its failing input.

    [Fact]
    public void A_bidi_mark_in_a_title_is_dropped_not_fatal_to_the_whole_info()
    {
        var parts = OoxmlSamples.Docx("<w:p><w:r><w:t>x</w:t></w:r></w:p>");
        parts[2] = ("docProps/core.xml", """<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>تقرير&#x200F; 2026&#x202E;</dc:title></cp:coreProperties>""");
        var docx = Make("rtl.docx", parts);
        var request = Request(DocumentCapabilities.InfoId, docx);
        var r = DocumentJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal("تقرير 2026", r.Facts.Single(f => f.Label == "Title").Value);
        Assert.True(DocumentJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void A_paragraph_holding_a_text_box_keeps_its_own_text_and_line_breaks()
    {
        var docx = Make("box.docx", OoxmlSamples.Docx("""<w:p><w:pPr><w:tabs><w:tab w:val="left" w:pos="720"/></w:tabs></w:pPr><w:r><w:t xml:space="preserve">BEFORE </w:t></w:r><w:r><w:drawing><w:txbxContent><w:p><w:r><w:t>BOX</w:t></w:r></w:p></w:txbxContent></w:drawing></w:r><w:r><w:t>AFTER</w:t><w:tab/><w:t>X</w:t><w:br/><w:t>Line2</w:t></w:r></w:p><w:p><w:r><w:t>NEXT</w:t></w:r></w:p>"""));
        var r = DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, docx));
        Assert.True(r.Success, r.Message);
        Assert.Equal("BEFORE AFTER\tX\r\nLine2\r\nNEXT\r\n", File.ReadAllText(r.OutputPath!, Encoding.UTF8));
    }

    [Theory]
    [InlineData("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="t" Target="http://evil.example/t.dotm" TargetMode='External'/></Relationships>""", "word/_rels/settings.xml.rels")]
    [InlineData("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="t" Target="http://x" TargetMode="External"/></Relationships>""", "word/_rels/document.xml.RELS")]
    public void An_external_link_is_reported_however_it_is_written(string rels, string part)
    {
        var docx = Make("linked.docx", [.. OoxmlSamples.Docx("<w:p/>"), (part, rels)]);
        Assert.Contains(DocumentJobs.Run(Request(DocumentCapabilities.InfoId, docx)).Facts, f => f is { Label: "Links", Notice: true });
    }

    [Fact]
    public void Macros_are_reported_from_the_content_type_whatever_the_part_is_called()
    {
        var parts = OoxmlSamples.Docx("<w:p/>");
        parts[0] = ("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/word/document.xml" ContentType="application/vnd.ms-word.document.macroEnabled.main+xml"/></Types>""");
        var docx = Make("macro.docx", [.. parts, ("word/m.bin", "code")]);
        Assert.Contains(DocumentJobs.Run(Request(DocumentCapabilities.InfoId, docx)).Facts, f => f is { Label: "Macros", Notice: true });
    }

    [Fact]
    public void Excel_cells_keep_their_columns_and_rich_text_is_one_cell()
    {
        var xlsx = Make("cells.xlsx", OoxmlSamples.Xlsx([], """<row r="1"><c r="A1" t="inlineStr"><is><r><t>ab</t></r><r><t>cd</t></r></is></c><c r="B1"><v>2</v></c></row><row r="2"><c r="A2"><v>1</v></c><c r="C2"><v>3</v></c></row>"""));
        var r = DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, xlsx));
        Assert.True(r.Success, r.Message);
        Assert.Equal("Sheet 1\r\nabcd\t2\r\n1\t\t3\r\n", File.ReadAllText(r.OutputPath!, Encoding.UTF8));
    }

    [Fact]
    public void A_deck_of_empty_slides_has_no_text_to_extract()
    {
        var pptx = Make("empty.pptx", OoxmlSamples.Pptx(""));
        Assert.Equal(ErrorCode.NothingToDo, DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, pptx)).Error);
    }

    [Fact]
    public void A_zip_that_only_mentions_the_type_in_a_comment_is_not_a_document()
    {
        var parts = OoxmlSamples.Docx("<w:p><w:r><w:t>x</w:t></w:r></w:p>");
        parts[0] = ("[Content_Types].xml", "<!-- wordprocessingml.document.main+xml --><x/>");
        Assert.Equal(ErrorCode.ContentMismatch, DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, Make("fake.docx", parts))).Error);
    }

    [Fact]
    public void A_password_protected_document_says_so()
    {
        var locked = _dir.Write("locked.docx", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[504]]);
        Assert.Equal(ErrorCode.PasswordRequired, DocumentJobs.Run(Request(DocumentCapabilities.InfoId, locked)).Error);
    }

    [Fact]
    public void Two_parts_whose_names_differ_only_in_case_are_refused()
    {
        var pptx = Make("dup.pptx", [.. OoxmlSamples.Pptx("A"), ("ppt/slides/Slide1.xml", "<p:sld xmlns:p=\"p\"/>")]);
        Assert.Equal(ErrorCode.DecodeFailed, DocumentJobs.Run(Request(DocumentCapabilities.ExtractTextId, pptx)).Error);
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
