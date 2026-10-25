using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Output;

namespace WinTools.Core.Tests;

/// <summary>
/// V2 PR 2.1 (ADR-0022): the PDF module's commands, as the menu offers them. A command is offered only when it can run
/// on the selection: Merge needs two PDFs or more, and nothing PDF-specific reaches a selection with another kind in it.
/// </summary>
public sealed class PdfCapabilitiesTests
{
    private static readonly CapabilityResolver Resolver = CapabilityResolver.For(customResizeId: null);
    private static string[] Ids(params string[] paths) => [.. Resolver.Resolve(paths).Select(c => c.Id)];

    [Theory]
    [InlineData(@"C:\p\report.pdf")]
    [InlineData(@"C:\p\REPORT.PDF")]
    [InlineData(@"C:\p\تقرير 😀.pdf")]
    public void A_pdf_is_a_pdf_by_its_name(string path)
    {
        Assert.Same(FileKinds.Pdf, FileKinds.IdentifyByName(path));
        Assert.Equal(FileFamily.Pdf, FileKinds.Pdf.Family);
        Assert.NotNull(FileKinds.Find("pdf"));
    }

    [Fact]
    public void One_pdf_gets_split_optimize_rotate_and_info_but_not_merge()
    {
        Assert.Equal(["pdf.split", "pdf.rotate-left", "pdf.rotate-right", "pdf.rotate-180", "pdf.optimize", "pdf.linearize", "pdf.repair", "pdf.info",
            "files.info", "files.sha256", "files.copy-path"], Ids(@"C:\p\a.pdf"));
        var menu = RadialMenu.Build(Resolver, [@"C:\p\a.pdf"]);
        Assert.Equal(["Organize", "Optimize", "Rotate", "Tools"], menu.Select(g => g.Label));
        Assert.Equal(["Smaller (lossless)", "Fast web view", "Repair"], menu[1].Commands.Select(c => c.Label));
        Assert.Equal(["PDF info", "File info", "SHA-256", "Copy path"], menu[^1].Commands.Select(c => c.Label));
    }

    [Fact]
    public void Two_pdfs_or_more_can_be_merged()
    {
        Assert.Contains("pdf.merge", Ids(@"C:\p\a.pdf", @"C:\p\b.pdf"));
        Assert.Contains("pdf.merge", Ids([.. Enumerable.Range(0, PdfCapabilities.MaxMergeInputs).Select(i => $@"C:\p\{i}.pdf")]));
        Assert.DoesNotContain("pdf.merge", Ids([.. Enumerable.Range(0, PdfCapabilities.MaxMergeInputs + 1).Select(i => $@"C:\p\{i}.pdf")]));
        var organize = RadialMenu.Build(Resolver, [@"C:\p\a.pdf", @"C:\p\b.pdf"]).Single(g => g.Label == "Organize");
        Assert.Equal(["Merge", "Split into pages"], organize.Commands.Select(c => c.Label));
    }

    [Theory]
    [InlineData(@"C:\p\a.pdf", @"C:\p\b.png")]
    [InlineData(@"C:\p\a.pdf", @"C:\p\notes.txt")]
    [InlineData(@"C:\p\a.pdf", @"C:\p\holiday\")]
    public void A_mixed_selection_gets_no_pdf_command(string first, string second) =>
        Assert.DoesNotContain(Ids(first, second), id => id.StartsWith("pdf.", StringComparison.Ordinal));

    [Fact]
    public void An_image_menu_is_unchanged_by_the_pdf_module()
    {
        Assert.DoesNotContain(Ids(@"C:\p\a.png"), id => id.StartsWith("pdf.", StringComparison.Ordinal));
        Assert.Equal(["Convert", "Compress", "Resize", "Rotate", "Tools"], RadialMenu.Build(Resolver, [@"C:\p\a.png"]).Select(g => g.Label));
    }

    [Fact]
    public void Outputs_are_named_after_the_source_and_the_command()
    {
        Assert.Equal([@"C:\p\a (merged).pdf", @"C:\p\a (merged) (1).pdf"],
            OutputNaming.CandidatesWithExtension(@"C:\p\a.pdf", ".pdf", PdfCapabilities.NameSuffix(PdfCapabilities.MergeId)).Take(2));
        Assert.Equal(@"D:\out\a (rotated right).pdf",
            OutputNaming.CandidatesWithExtension(@"C:\p\a.pdf", ".pdf", PdfCapabilities.NameSuffix(PdfCapabilities.RotateRightId), @"D:\out").First());
        Assert.Equal([@"C:\p\a (pages)", @"C:\p\a (pages) (1)"], OutputNaming.FolderCandidates(@"C:\p\a.pdf", "pages").Take(2));
        Assert.Equal("a - page 007.pdf", PdfCapabilities.PageFileName(@"C:\p\a.pdf", 7, 120));
        Assert.Equal("a - page 1.pdf", PdfCapabilities.PageFileName(@"C:\p\a.pdf", 1, 9));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData(".")]
    [InlineData(@".p\df")]
    [InlineData(".pdf ")]
    public void An_extension_that_could_change_the_folder_or_the_name_is_refused(string extension) =>
        Assert.Throws<ArgumentException>(() => OutputNaming.CandidatesWithExtension(@"C:\p\a.pdf", extension).First());

    [Fact]
    public void Every_rotate_command_has_a_rotation_and_a_suffix()
    {
        foreach (var id in new[] { PdfCapabilities.RotateLeftId, PdfCapabilities.RotateRightId, PdfCapabilities.Rotate180Id })
        {
            Assert.NotNull(PdfCapabilities.RotationOf(id));
            Assert.NotNull(PdfCapabilities.NameSuffix(id));
        }
        Assert.Null(PdfCapabilities.RotationOf(PdfCapabilities.MergeId));
    }
}
