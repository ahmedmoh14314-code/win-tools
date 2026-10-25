using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WinTools.Core.Capabilities;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Host.Jobs;
using WinTools.Worker.Text;

namespace WinTools.Worker.Tests;

/// <summary>
/// V2 PR 6.2 (ADR-0025 decision 6): Remove properties writes a copy of a Word, Excel or PowerPoint file whose core,
/// extended and custom properties are empty, every other part byte for byte the same, and says what it does not remove.
/// The original is never changed; a signed document is refused, since the copy would break its signature. Written
/// before the code.
/// </summary>
public sealed class DocumentCleanTests : IDisposable
{
    private readonly TempFolder _dir = new();
    public void Dispose() => _dir.Dispose();

    private const string App = """<?xml version="1.0" encoding="UTF-8"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"><Application>Microsoft Office Word</Application><Company>Acme Secret Projects</Company><Manager>Big Boss</Manager><Template>\\server\share\private.dotm</Template><Pages>3</Pages></Properties>""";
    private const string Custom = """<?xml version="1.0" encoding="UTF-8"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/custom-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"><property fmtid="{D5CDD505-2E9C-101B-9397-08002B2CF9AE}" pid="2" name="Client"><vt:lpwstr>Confidential Client Ltd</vt:lpwstr></property></Properties>""";
    private const string RootRels = """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/><Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties" Target="docProps/custom.xml"/></Relationships>""";

    private static JobRequest Request(string path) => new(Guid.NewGuid().ToString("N"), DocumentCapabilities.RemovePropertiesId, [path]);

    private string Make(string name, params (string, string)[] parts) { var p = Path.Combine(_dir.Path, name); OoxmlSamples.Write(p, parts); return p; }

    private static (string, string)[] FullDocx(string body = "<w:p><w:r><w:t>مرحبا</w:t></w:r></w:p>") =>
        [.. OoxmlSamples.Docx(body), ("_rels/.rels", RootRels), ("docProps/app.xml", App), ("docProps/custom.xml", Custom)];

    private static Dictionary<string, byte[]> Parts(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); });
    }

    private static string[] Order(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    [Fact]
    public void Properties_are_emptied_in_a_copy_and_every_other_part_is_the_same()
    {
        var docx = Make("report.docx", FullDocx());
        var before = SHA256.HashData(File.ReadAllBytes(docx));
        var request = Request(docx);
        var r = DocumentJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Equal("report (no properties).docx", Path.GetFileName(r.OutputPath));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(docx))); // the original is only read

        var source = Parts(docx);
        var output = Parts(r.OutputPath!);
        Assert.Equal(Order(docx), Order(r.OutputPath!));
        foreach (var name in source.Keys.Where(n => !n.StartsWith("docProps/", StringComparison.Ordinal)))
            Assert.Equal(source[name], output[name]);
        var text = Encoding.UTF8.GetString([.. output["docProps/core.xml"], .. output["docProps/app.xml"], .. output["docProps/custom.xml"]]);
        foreach (var secret in new[] { "تقرير الربع", "Ahmed", "2026-10-24", "Acme", "Big Boss", "private.dotm", "Confidential", "Microsoft Office Word" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.True(DocumentJobModule.Instance.IsBelievable(request, r));

        // The copy is still a Word document: it opens, has the same text and no properties left to show.
        var info = DocumentJobs.Run(new JobRequest(Guid.NewGuid().ToString("N"), DocumentCapabilities.InfoId, [r.OutputPath!]));
        Assert.True(info.Success, info.Message);
        Assert.DoesNotContain(info.Facts, f => f.Label is "Title" or "Author" or "Last saved by" or "Modified");
    }

    [Fact]
    public void Excel_and_PowerPoint_are_cleaned_the_same_way()
    {
        const string core = """<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:creator>Ahmed</dc:creator></cp:coreProperties>""";
        var xlsx = Make("book.xlsx", [.. OoxmlSamples.Xlsx(["a"], """<row r="1"><c r="A1" t="s"><v>0</v></c></row>"""), ("docProps/core.xml", core)]);
        var pptx = Make("deck.pptx", [.. OoxmlSamples.Pptx("one"), ("docProps/core.xml", core)]);
        foreach (var (path, name) in new[] { (xlsx, "book (no properties).xlsx"), (pptx, "deck (no properties).pptx") })
        {
            var r = DocumentJobs.Run(Request(path));
            Assert.True(r.Success, r.Message);
            Assert.Equal(name, Path.GetFileName(r.OutputPath));
            Assert.DoesNotContain("Ahmed", Encoding.UTF8.GetString(Parts(r.OutputPath!)["docProps/core.xml"]), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Properties_found_through_the_package_relationships_are_emptied_wherever_they_live()
    {
        var rels = RootRels.Replace("docProps/core.xml", "meta/c.xml", StringComparison.Ordinal);
        var parts = FullDocx().Where(p => p.Item1 is not ("_rels/.rels" or "docProps/core.xml")).Append(("_rels/.rels", rels))
            .Append(("meta/c.xml", """<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:creator>Hidden Author</dc:creator></cp:coreProperties>""")).ToArray();
        var r = DocumentJobs.Run(Request(Make("moved.docx", parts)));
        Assert.True(r.Success, r.Message);
        Assert.DoesNotContain("Hidden Author", Encoding.UTF8.GetString(Parts(r.OutputPath!)["meta/c.xml"]), StringComparison.Ordinal);
    }

    [Fact]
    public void What_is_not_removed_is_said_comment_and_revision_authors()
    {
        var body = """<w:p><w:ins w:id="1" w:author="Reviewer Name" w:date="2026-10-24T10:00:00Z"><w:r><w:t>added</w:t></w:r></w:ins></w:p>""";
        var docx = Make("tracked.docx", [.. FullDocx(body), ("word/comments.xml", """<w:comments xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:comment w:id="0" w:author="Commenter"/></w:comments>""")]);
        var request = Request(docx);
        var r = DocumentJobs.Run(request);
        Assert.True(r.Success, r.Message);
        Assert.Contains(r.Facts, f => f is { Label: "Kept", Notice: true } && f.Value.Contains("comment", StringComparison.OrdinalIgnoreCase));
        Assert.True(JobFacts.AreShowable(r.Facts));
        Assert.True(DocumentJobModule.Instance.IsBelievable(request, r));
    }

    [Fact]
    public void A_signed_document_is_refused_since_the_copy_would_break_its_signature()
    {
        var docx = Make("signed.docx", [.. FullDocx(), ("_xmlsignatures/sig1.xml", "<Signature/>")]);
        var r = DocumentJobs.Run(Request(docx));
        Assert.Equal(ErrorCode.UnsupportedContent, r.Error);
        Assert.Equal(["signed.docx"], _dir.Names());
    }

    [Fact]
    public void A_document_without_properties_has_nothing_to_remove()
    {
        var parts = OoxmlSamples.Docx("<w:p/>").Where(p => p.Item1 != "docProps/core.xml").ToArray();
        var r = DocumentJobs.Run(Request(Make("bare.docx", parts)));
        Assert.Equal(ErrorCode.NothingToDo, r.Error);
        Assert.Equal(["bare.docx"], _dir.Names());
    }

    [Fact]
    public void An_existing_copy_is_never_overwritten()
    {
        var docx = Make("report.docx", FullDocx());
        _dir.Write("report (no properties).docx", "mine"u8.ToArray());
        var r = DocumentJobs.Run(Request(docx));
        Assert.True(r.Success, r.Message);
        Assert.Equal("report (no properties) (1).docx", Path.GetFileName(r.OutputPath));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir.Path, "report (no properties).docx")));
    }

    [Fact]
    public void A_part_larger_than_it_declares_is_damage_and_nothing_is_kept()
    {
        // The declared size is what the bomb rules checked; a part that inflates past it is not copied on trust.
        var docx = Make("liar.docx", [.. FullDocx(), ("word/media/blob.bin", new string('a', 100_000))]);
        var bytes = File.ReadAllBytes(docx);
        var name = "word/media/blob.bin"u8;
        for (var at = bytes.AsSpan().IndexOf(name); at >= 0;)
        {
            // Central directory record (PK\1\2) holds the uncompressed size at offset 24; a local header (PK\3\4) at 22.
            var central = at >= 46 && bytes.AsSpan(at - 46, 4).SequenceEqual("PK\u0001\u0002"u8);
            var local = at >= 30 && bytes.AsSpan(at - 30, 4).SequenceEqual("PK\u0003\u0004"u8);
            if (central) BitConverter.TryWriteBytes(bytes.AsSpan(at - 46 + 24), 1000);
            if (local) BitConverter.TryWriteBytes(bytes.AsSpan(at - 30 + 22), 1000);
            var next = bytes.AsSpan(at + 1).IndexOf(name);
            at = next < 0 ? -1 : at + 1 + next;
        }
        File.WriteAllBytes(docx, bytes);
        var r = DocumentJobs.Run(Request(docx));
        Assert.False(r.Success);
        Assert.Equal(["liar.docx"], _dir.Names());
    }

    [Fact]
    public void The_menu_offers_remove_properties_under_tools()
    {
        var resolver = CapabilityResolver.For(customResizeId: null);
        var tools = RadialMenu.Build(resolver, [@"C:\p\a.docx"]).Single(g => g.Label == "Tools");
        // The document's own commands first; the General Files commands follow them.
        Assert.Equal(["Document info", "Remove properties"], tools.Commands.Take(2).Select(c => c.Label));
    }
}
