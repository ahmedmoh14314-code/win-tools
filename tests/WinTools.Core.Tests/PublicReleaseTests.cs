using WinTools.Core.Capabilities;
using WinTools.Core.Interaction;

namespace WinTools.Core.Tests;

/// <summary>
/// The public release offers images, PDF and Office, and video, and nothing else (owner decision, 2026-11-11): the
/// other commands stay in <see cref="CapabilityResolver.For"/> but never reach the wheel, Home or Features.
/// </summary>
public sealed class PublicReleaseTests
{
    private static readonly IReadOnlySet<string> EveryProgram =
        ExternalPrograms.Set("soffice", [ExternalPrograms.Word, ExternalPrograms.Excel, ExternalPrograms.PowerPoint, ExternalPrograms.Edge]);

    private static string[] Offered(params string[] names) =>
        CapabilityResolver.Public(null, EveryProgram).Resolve(names.Select(n => $@"C:\in\{n}").ToArray()).Select(c => c.Id).ToArray();

    [Fact]
    public void Every_listed_command_exists()
    {
        var all = CapabilityResolver.For(null, EveryProgram).Catalog.Select(c => c.Id).ToHashSet();
        foreach (var (_, ids) in PublicRelease.Families)
            foreach (var id in ids) Assert.Contains(id, all);
    }

    [Fact]
    public void Only_listed_commands_are_offered()
    {
        var listed = PublicRelease.Families.SelectMany(f => f.Ids).ToHashSet();
        var offered = CapabilityResolver.Public(null, EveryProgram).Catalog;
        Assert.All(offered, c => Assert.Contains(c.Id, listed));
        Assert.DoesNotContain(offered, c => c.Module is "files" or "archive" or "subtitles" or "documents");
    }

    [Theory]
    [InlineData("a.zip")]
    [InlineData("a.7z")]
    [InlineData("a.srt")]
    [InlineData("a.mp3")]
    [InlineData("notes.md")]
    [InlineData("page.html")]
    [InlineData("holiday\\")]
    [InlineData("setup.exe")]
    public void Hidden_families_get_no_commands(string name) => Assert.Empty(Offered(name));

    [Fact]
    public void An_image_gets_convert_compress_resize_and_pdf_only()
    {
        Assert.Contains("convert.to-jpeg", Offered("a.heic"));
        Assert.Contains(PdfRenderCapabilities.ImagesToPdfId, Offered("a.heic"));
        var ids = Offered("a.jpg");
        Assert.Contains("compress.balanced", ids);
        Assert.Contains("resize.percent-50", ids);
        Assert.Contains(PdfRenderCapabilities.ImagesToPdfId, ids);
        Assert.DoesNotContain("convert.to-avif", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("adjust.", StringComparison.Ordinal) || id.StartsWith("crop.", StringComparison.Ordinal) || id is "collage" or "metadata.info" or "archive.zip");
    }

    [Fact]
    public void A_pdf_gets_the_conversions_merge_split_smaller_and_rotate()
    {
        var ids = Offered("a.pdf");
        foreach (var id in new[] { OfficeCapabilities.PdfToWordId, PdfRenderCapabilities.ToPowerPointId, PdfRenderCapabilities.ToExcelId, "pdfrender.to-jpg", "pdfrender.to-png", "pdf.split", PdfCapabilities.OptimizeId, PdfCapabilities.RotateRightId })
            Assert.Contains(id, ids);
        foreach (var id in new[] { "pdfrender.to-txt", "pdfrender.to-md", PdfCapabilities.ProtectId, "pdf.unprotect", "pdf.repair", "pdf.linearize", PdfCapabilities.InfoId, PdfCapabilities.OrganizeId })
            Assert.DoesNotContain(id, ids);
        Assert.Contains("pdf.merge", Offered("a.pdf", "b.pdf"));
    }

    [Theory]
    [InlineData("a.docx")]
    [InlineData("a.pptx")]
    [InlineData("a.xlsx")]
    public void An_office_file_gets_to_pdf_only(string name) => Assert.Equal([OfficeCapabilities.ToPdfId], Offered(name));

    [Fact]
    public void A_video_gets_convert_and_three_compress_presets()
    {
        var ids = Offered("a.mp4");
        Assert.Equal([MediaCapabilities.CompressVideo720Id, MediaCapabilities.CompressVideoId, MediaCapabilities.CompressVideoHighId], ids.Where(id => id.StartsWith("media.compress", StringComparison.Ordinal)));
        Assert.Contains("media.video-to-webm", ids);
        Assert.DoesNotContain(ids, id => id is "media.trim" or "media.split" or "media.join" or "media.speed-2x" or "media.frames" or "media.extract-audio" or "media.video-to-gif" or "media.crop-1x1" or "media.video-info");
    }

    [Fact]
    public void Without_office_the_office_conversions_are_not_offered()
    {
        var ids = CapabilityResolver.Public(null, ExternalPrograms.Set(null)).Resolve([@"C:\in\a.pdf"]).Select(c => c.Id).ToArray();
        Assert.DoesNotContain(OfficeCapabilities.PdfToWordId, ids);
        Assert.Contains(PdfRenderCapabilities.ToExcelId, ids); // read by WinTools itself
        Assert.Empty(CapabilityResolver.Public(null, ExternalPrograms.Set(null)).Resolve([@"C:\in\a.docx"]));
    }

    [Fact]
    public void The_exact_size_from_settings_stays_on_the_wheel()
    {
        var custom = new Settings.AppSettings { ResizeWidth = 1000, ResizeHeight = 500 }.CustomResizeId();
        Assert.NotNull(custom);
        Assert.Contains(custom, CapabilityResolver.Public(custom).Resolve([@"C:\in\a.png"]).Select(c => c.Id));
    }

    [Fact]
    public void The_wheel_shows_no_empty_category()
    {
        var menu = RadialMenu.Build(CapabilityResolver.Public(null, EveryProgram), [@"C:\in\a.png"]);
        Assert.All(menu, g => Assert.NotEmpty(g.Commands));
        Assert.DoesNotContain(menu, g => g.Category.Id is "image-edit" or "tools" or "actions");
    }

    [Fact]
    public void Every_family_has_words_in_both_languages()
    {
        foreach (var lang in new[] { Localization.UiText.English, Localization.UiText.Arabic })
        {
            foreach (var (family, _) in PublicRelease.Families)
            {
                Assert.False(string.IsNullOrWhiteSpace(Localization.UiText.Get(lang, "pubfamily." + family)), lang + " " + family);
                Assert.False(string.IsNullOrWhiteSpace(Localization.UiText.Get(lang, "pubfamily." + family + ".text")), lang + " " + family);
            }
            foreach (var id in PublicRelease.Families.Single(f => f.Family == PublicRelease.PdfOffice).Ids)
                Assert.False(string.IsNullOrWhiteSpace(Localization.UiText.Get(lang, "features.cmd." + id)), lang + " " + id);
            foreach (var key in new[] { "cmd.media.compress-high", "features.note.pptx", "features.note.xlsx", "features.note.word", "features.note.optimize", "features.needsOffice" })
                Assert.False(string.IsNullOrWhiteSpace(Localization.UiText.Get(lang, key)), lang + " " + key);
        }
    }
}
