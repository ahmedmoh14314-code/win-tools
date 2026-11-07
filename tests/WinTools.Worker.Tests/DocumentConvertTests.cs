using System.IO.Compression;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Text;

namespace WinTools.Worker.Tests;

/// <summary>
/// ADR-0033: the documents module's in-house conversions. CSV/TSV to Excel keeps text as text (Arabic, leading zeros,
/// long IDs, "=" cells) and makes only plain numbers numbers; Excel to CSV writes every sheet with data, as values, with
/// a BOM, and guards cells Excel would run; Markdown to HTML never passes raw HTML or a script address through. The
/// original is never changed, nothing is overwritten, a hostile file fails cleanly, and the host believes the result.
/// </summary>
public sealed class DocumentConvertTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private static JobRequest Request(string capability, string path) => new(Guid.NewGuid().ToString("N"), capability, [path]);

    private string Text(string name, string text, Encoding? encoding = null) =>
        _dir.Write(name, [.. (encoding ?? new UTF8Encoding(false)).GetPreamble(), .. (encoding ?? new UTF8Encoding(false)).GetBytes(text)]);

    /// <summary>The workbook's one sheet, numbers as stored.</summary>
    private static List<string[]> Cells(string xlsx, out string sheetXml)
    {
        using var zip = ZipFile.OpenRead(xlsx);
        var sheets = WorkbookReader.Sheets(zip);
        Assert.Single(sheets);
        using var reader = new StreamReader(zip.GetEntry(sheets[0].Part)!.Open());
        sheetXml = reader.ReadToEnd();
        return WorkbookReader.Rows(sheetXml, WorkbookReader.ContextOf(zip), raw: true);
    }

    private static JobResult Run(JobRequest request)
    {
        var r = DocumentJobs.Run(request);
        Assert.True(DocumentJobModule.Instance.IsBelievable(request, r), $"not believable: {r.Error} {r.Message}");
        return r;
    }

    [Fact]
    public void Csv_to_excel_keeps_text_as_text_and_makes_plain_numbers_numbers()
    {
        var csv = Text("data.csv", "الاسم,الرمز,المبلغ,هاتف,ملاحظة\r\nأحمد,007,3.14,12345678901234567,\"قال \"\"مرحبا\"\", ثم ذهب\"\r\nSara,-5,1E5,=1+1,\"two\nlines\"\r\n");
        var before = Hash.Of(csv);
        var r = Run(Request(DocumentCapabilities.ToXlsxId, csv));
        Assert.True(r.Success, r.Message);
        Assert.Equal("data.xlsx", Path.GetFileName(r.OutputPath));
        Assert.Equal(before, Hash.Of(csv));
        var cells = Cells(r.OutputPath!, out var xml);
        Assert.Equal(["الاسم", "الرمز", "المبلغ", "هاتف", "ملاحظة"], cells[0]);
        Assert.Equal(["أحمد", "007", "3.14", "12345678901234567", "قال \"مرحبا\", ثم ذهب"], cells[1]);
        Assert.Equal(["Sara", "-5", "1E5", "=1+1", "two\nlines"], cells[2]);
        // 3.14 and -5 are numbers; 007, the 17-digit phone, 1E5 and =1+1 are text, never a formula.
        Assert.Contains("<c r=\"C2\"><v>3.14</v></c>", xml, StringComparison.Ordinal);
        Assert.Contains("<c r=\"B3\"><v>-5</v></c>", xml, StringComparison.Ordinal);
        Assert.Contains("<c r=\"B2\" t=\"inlineStr\">", xml, StringComparison.Ordinal);
        Assert.Contains("<c r=\"D3\" t=\"inlineStr\">", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
        Assert.Contains("rightToLeft=\"1\"", xml, StringComparison.Ordinal); // mostly Arabic: the sheet opens right to left
    }

    [Fact]
    public void A_semicolon_csv_a_sep_line_and_a_tsv_are_read_with_their_separators()
    {
        var semi = Text("eu.csv", "a;b;c\r\n1,5;2;x\r\n");
        Assert.Equal(["1,5", "2", "x"], Cells(Run(Request(DocumentCapabilities.ToXlsxId, semi)).OutputPath!, out _)[1]);
        var sep = Text("hint.csv", "sep=|\r\na|b\r\n");
        Assert.Equal([["a", "b"]], Cells(Run(Request(DocumentCapabilities.ToXlsxId, sep)).OutputPath!, out _));
        var tsv = Text("t.tsv", "a\tb,c\r\n1\t2\r\n");
        Assert.Equal(["a", "b,c"], Cells(Run(Request(DocumentCapabilities.ToXlsxId, tsv)).OutputPath!, out _)[0]);
    }

    [Fact]
    public void An_arabic_windows_1256_csv_is_read_and_says_so()
    {
        var text = "الاسم,المدينة\r\nأحمد,القاهرة\r\nمحمد,الإسكندرية\r\nعلي,الجيزة\r\n";
        var csv = _dir.Write("legacy.csv", CodePagesEncoding(1256).GetBytes(text));
        var r = Run(Request(DocumentCapabilities.ToXlsxId, csv));
        Assert.True(r.Success, r.Message);
        Assert.Equal(["أحمد", "القاهرة"], Cells(r.OutputPath!, out _)[1]);
        Assert.Contains(r.Facts, f => f is { Label: "Read as", Notice: true });
    }

    private static Encoding CodePagesEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    [Fact]
    public void A_broken_or_oversized_table_fails_cleanly_and_writes_nothing()
    {
        var open = Text("open.csv", "a,\"never closed\r\nb,c\r\n");
        var r = Run(Request(DocumentCapabilities.ToXlsxId, open));
        Assert.Equal(ErrorCode.DecodeFailed, r.Error);
        var wide = Text("wide.csv", string.Join(',', Enumerable.Repeat("x", Csv.MaxColumns + 1)));
        Assert.Equal(ErrorCode.LimitExceeded, Run(Request(DocumentCapabilities.ToXlsxId, wide)).Error);
        var empty = Text("empty.csv", "\r\n,,\r\n");
        Assert.Equal(ErrorCode.NothingToDo, Run(Request(DocumentCapabilities.ToXlsxId, empty)).Error);
        var binary = _dir.Write("binary.csv", [0x4D, 0x5A, 0x00, 0x01, 0x02]);
        Assert.Equal(ErrorCode.UnsupportedContent, Run(Request(DocumentCapabilities.ToXlsxId, binary)).Error);
        Assert.Equal(["binary.csv", "empty.csv", "open.csv", "wide.csv"], _dir.Names());
    }

    [Fact]
    public void An_existing_workbook_is_never_overwritten()
    {
        var csv = Text("data.csv", "a,b\r\n");
        var taken = _dir.Write("data.xlsx", [1, 2, 3]);
        var r = Run(Request(DocumentCapabilities.ToXlsxId, csv));
        Assert.Equal("data (1).xlsx", Path.GetFileName(r.OutputPath));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(taken));
    }

    [Fact]
    public void Characters_xml_cannot_hold_are_replaced_and_said()
    {
        var csv = Text("ctl.csv", "a\u0001b,c\r\n");
        var r = Run(Request(DocumentCapabilities.ToXlsxId, csv));
        Assert.True(r.Success, r.Message);
        Assert.Equal(["a�b", "c"], Cells(r.OutputPath!, out _)[0]);
        Assert.Contains(r.Facts, f => f is { Label: "Changed", Notice: true });
    }

    private const string Styles = """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="&quot;Total &quot;0.00"/></numFmts><cellXfs count="4"><xf numFmtId="0"/><xf numFmtId="14"/><xf numFmtId="164"/><xf numFmtId="20"/></cellXfs></styleSheet>""";

    private static (string, string)[] Workbook(params (string Name, string SheetData)[] sheets) =>
    [
        ("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>"""),
        ("xl/workbook.xml", $"""<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>{string.Concat(sheets.Select((s, i) => $"<sheet name=\"{s.Name}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"))}</sheets></workbook>"""),
        ("xl/_rels/workbook.xml.rels", $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{string.Concat(sheets.Select((s, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{sheets.Length - i}.xml\"/>"))}</Relationships>"""),
        ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>مرحبا</t></si><si><r><t>rich </t></r><r><t>text</t></r></si></sst>"""),
        ("xl/styles.xml", Styles),
        // Parts named in reverse: the workbook's order, not the part numbers, decides.
        .. sheets.Select((s, i) => ($"xl/worksheets/sheet{sheets.Length - i}.xml", $"""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{s.SheetData}</sheetData></worksheet>""")),
    ];

    private string Xlsx(string name, params (string Name, string SheetData)[] sheets)
    {
        var p = Path.Combine(_dir.Path, name);
        OoxmlSamples.Write(p, Workbook(sheets));
        return p;
    }

    [Fact]
    public void Excel_to_csv_writes_values_dates_in_iso_and_guards_cells_excel_would_run()
    {
        var book = Xlsx("book.xlsx", ("Data", """
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="inlineStr"><is><t>a, "quoted"</t></is></c></row>
            <row r="3"><c r="A3"><v>0.30000000000000004</v></c><c r="B3" s="1"><v>46323</v></c><c r="C3" s="2"><v>12.5</v></c><c r="D3" t="b"><v>1</v></c><c r="E3" s="3"><v>0.5</v></c></row>
            <row r="4"><c r="A4" t="str"><f>CONCAT("=","cmd")</f><v>=cmd|' /C calc'!A0</v></c><c r="B4"><f>1+1</f><v>2</v></c><c r="C4" t="e"><v>#DIV/0!</v></c><c r="D4"><v>-5</v></c></row>
            """));
        var before = Hash.Of(book);
        var request = Request(DocumentCapabilities.ToCsvId, book);
        var r = Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal("book.csv", Path.GetFileName(r.OutputPath));
        Assert.Equal(before, Hash.Of(book));
        var bytes = File.ReadAllBytes(r.OutputPath!);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n");
        Assert.Equal("مرحبا,rich text,\"a, \"\"quoted\"\"\",,", lines[0]);
        Assert.Equal(",,,,", lines[1]); // the missing row 2 is kept, as wide as the rest
        Assert.Equal("0.3,2026-10-28,12.5,TRUE,12:00:00", lines[2]);
        Assert.Equal("'=cmd|' /C calc'!A0,2,#DIV/0!,-5,", lines[3]);
        Assert.Contains(r.Facts, f => f is { Label: "Changed", Notice: true });
    }

    [Fact]
    public void Several_sheets_with_data_become_a_folder_of_csv_files_in_workbook_order()
    {
        var book = Xlsx("book.xlsx", ("Sales", """<row r="1"><c r="A1"><v>1</v></c></row>"""), ("Empty", ""), ("a/b:c", """<row r="1"><c r="A1"><v>2</v></c></row>"""), ("CON", """<row r="1"><c r="A1"><v>3</v></c></row>"""));
        var request = Request(DocumentCapabilities.ToCsvId, book);
        var r = Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal("book (CSV)", Path.GetFileName(r.OutputPath));
        Assert.Equal("3", r.Facts.Single(f => f.Label == "Sheets").Value);
        Assert.Equal(["Sales.csv", "_CON.csv", "a_b_c.csv"], Directory.GetFiles(r.OutputPath!).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("﻿2\r\n"u8.ToArray(), File.ReadAllBytes(Path.Combine(r.OutputPath!, "a_b_c.csv")));
        // A folder that changed after the worker wrote it is not believed.
        File.WriteAllText(Path.Combine(r.OutputPath!, "extra.csv"), "﻿x");
        Assert.False(DocumentJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void A_workbook_with_no_data_is_nothing_to_do_and_a_csv_round_trips()
    {
        var empty = Xlsx("empty.xlsx", ("Sheet1", ""));
        Assert.Equal(ErrorCode.NothingToDo, Run(Request(DocumentCapabilities.ToCsvId, empty)).Error);
        var csv = Text("round.csv", "name,code,amount\r\nأحمد,007,3.5\r\n\"x,y\",\"line\nbreak\",-2\r\n");
        var xlsx = Run(Request(DocumentCapabilities.ToXlsxId, csv)).OutputPath!;
        var back = Run(Request(DocumentCapabilities.ToCsvId, xlsx));
        Assert.True(back.Success, back.Message);
        Assert.Equal(Encoding.UTF8.GetBytes("﻿name,code,amount\r\nأحمد,007,3.5\r\n\"x,y\",\"line\nbreak\",-2\r\n"), File.ReadAllBytes(back.OutputPath!));
    }

    [Fact]
    public void Markdown_to_html_renders_arabic_tables_and_lists()
    {
        var md = Text("notes.md", "# عنوان\n\nفقرة **عريضة** و [رابط](https://example.com).\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n");
        var before = Hash.Of(md);
        var r = Run(Request(DocumentCapabilities.ToHtmlId, md));
        Assert.True(r.Success, r.Message);
        Assert.Equal("notes.html", Path.GetFileName(r.OutputPath));
        Assert.Equal(before, Hash.Of(md));
        var html = File.ReadAllText(r.OutputPath!);
        Assert.Contains("<h1 id=", html, StringComparison.Ordinal);
        Assert.Contains("عنوان</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>عريضة</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com\">", html, StringComparison.Ordinal);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("unicode-bidi: plaintext", html, StringComparison.Ordinal);
        Assert.Contains(DocumentCapabilities.HtmlPolicy, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_html_scripts_and_script_addresses_never_reach_the_page()
    {
        var md = Text("evil.md", """
            <script>alert(1)</script>

            <img src=x onerror="alert(2)">

            [click](javascript:alert(3)) [tab](java	script:alert(4)) ![img](data:image/svg+xml;base64,PHN2Zz4=) [ok](other.md#part) [mail](mailto:a@b.c)

            Text {onclick="alert(5)"}
            <https://example.com/auto>
            """);
        var r = Run(Request(DocumentCapabilities.ToHtmlId, md));
        Assert.True(r.Success, r.Message);
        var html = File.ReadAllText(r.OutputPath!);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html.Replace("[click](javascript", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"java", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"other.md#part\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"mailto:a@b.c\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com/auto\"", html, StringComparison.Ordinal);
        Assert.True(MarkdownPage.IsInert(html));
    }

    [Theory]
    [InlineData("<p onclick=\"x\">a</p>")]
    [InlineData("<a href=\"javascript:x\">a</a>")]
    [InlineData("<script>x</script>")]
    [InlineData("<iframe src=\"https://e\"></iframe>")]
    [InlineData("<p style=\"x\">a</p>")]
    public void A_page_with_anything_active_is_not_inert(string fragment)
    {
        var page = $"<html><head><meta http-equiv=\"Content-Security-Policy\" content=\"{DocumentCapabilities.HtmlPolicy}\"></head><body>{fragment}</body></html>";
        Assert.False(MarkdownPage.IsInert(page));
        Assert.True(MarkdownPage.IsInert(page.Replace(fragment, "<p title=\"a onclick=b\">fine javascript: text</p>", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Deeply_nested_markdown_converts_or_fails_cleanly_in_the_worker_process()
    {
        // In the real worker process: whatever Markdig does with 20,000 levels, the host gets an answer and the original stays.
        var md = Text("deep.md", new string('>', 20_000) + " x\n" + string.Concat(Enumerable.Repeat("[", 20_000)) + "\n" + string.Concat(Enumerable.Repeat("*a ", 20_000)) + "\n");
        var before = Hash.Of(md);
        var client = new WorkerClient(Path.Combine(AppContext.BaseDirectory, "WinTools.Worker.Text.exe"), TimeSpan.FromMinutes(1), DocumentJobModule.Instance, parallelism: 1);
        var request = Request(DocumentCapabilities.ToHtmlId, md);
        var r = (await client.RunAsync([request], CancellationToken.None))[0];
        Assert.True(r.Success || r.Error is ErrorCode.LimitExceeded, $"{r.Error} {r.Message}"); // never a crash (it was one before the review of this test)
        Assert.True(DocumentJobModule.Instance.IsBelievable(request, r));
        Assert.Equal(before, Hash.Of(md));
    }

    [Fact]
    public void Csv_parsing_follows_rfc_4180()
    {
        var t = Csv.Parse("a,\"b,c\",\"d\"\"e\"\r\n\"multi\r\nline\",x\ny\rz", ',');
        Assert.Equal([["a", "b,c", "d\"e"], ["multi\r\nline", "x"], ["y"], ["z"]], t.Rows);
        Assert.Equal(';', Csv.DetectSeparator("a;b;\"c,d\"\r\n", out _));
        Assert.Equal(',', Csv.DetectSeparator("a,b;c\r\n", out _));
        Assert.True(Csv.IsPlainNumber("0.5"));
        Assert.True(Csv.IsPlainNumber("-12"));
        Assert.False(Csv.IsPlainNumber("007"));
        Assert.False(Csv.IsPlainNumber("1,000"));
        Assert.False(Csv.IsPlainNumber("1e5"));
        Assert.False(Csv.IsPlainNumber("1234567890123456")); // 16 digits: Excel would lose the last
        Assert.False(Csv.IsPlainNumber(".5"));
        Assert.False(Csv.IsPlainNumber("5."));
        Assert.Equal("﻿a,\"b,c\",\" d\"\r\n,,\r\n"u8.ToArray()[3..], Csv.Write([["a", "b,c", " d"], [""]])[3..]);
    }

    [Theory]
    [InlineData(14, null, 1)]
    [InlineData(20, null, 2)]
    [InlineData(164, "yyyy-mm-dd", 1)]
    [InlineData(164, "\"Due \"0.00", 0)]
    [InlineData(164, "[h]:mm:ss", 0)]
    [InlineData(164, "hh:mm", 2)]
    [InlineData(164, "0.00%", 0)]
    [InlineData(164, "[Red]d/m/yyyy", 1)]
    public void Date_formats_are_told_from_number_formats(int id, string? code, int kind) =>
        Assert.Equal(kind, WorkbookReader.DateKind(id, code));

    [Fact]
    public void Sheet_and_file_names_are_made_safe()
    {
        Assert.Equal("Sheet1", XlsxWriter.SheetName("History"));
        Assert.Equal("ab", XlsxWriter.SheetName("[a]:*?/\\b"));
        Assert.Equal(31, XlsxWriter.SheetName(new string('x', 40)).Length);
        Assert.Equal("تقرير", XlsxWriter.SheetName("تقرير"));
        Assert.Equal(["a.csv", "A (2).csv", "Sheet 3.csv", "_nul.csv"], ConvertJobs.SheetFileNames(["a", "A", "...", "nul"]));
        Assert.Equal("XFD7", XlsxWriter.Reference(16_383, 6));
        Assert.Equal("AA1", XlsxWriter.Reference(26, 0));
    }
}
