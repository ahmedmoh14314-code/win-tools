using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Interaction;

namespace WinTools.Core.Tests;

/// <summary>
/// V2 PR 1.4, the General Files module as the walking skeleton of a second module (V2 plan §10): File info, SHA-256 and
/// Copy path for any file, folders where that makes sense, resolved together with the image module.
/// </summary>
public sealed class GeneralFilesTests
{
    private static readonly CapabilityResolver Resolver = CapabilityResolver.For(customResizeId: null);
    private static string[] Ids(params string[] paths) => [.. Resolver.Resolve(paths).Select(c => c.Id)];
    // Add to ZIP (V2 PR 5.1) accepts any file or folder too and comes last in Tools.
    private static readonly string[] General = ["files.info", "files.sha256", "files.copy-path", "archive.zip"];

    [Theory]
    [InlineData(@"C:\p\notes.xyz")]
    [InlineData(@"C:\p\README")]
    [InlineData(@"C:\p\setup.exe")]
    public void Any_file_no_module_reads_gets_the_general_commands(string path) => Assert.Equal(General, Ids(path));

    [Fact]
    public void A_file_of_no_known_kind_is_an_ordinary_file()
    {
        Assert.Same(FileKinds.AnyFile, FileKinds.IdentifyByName(@"C:\p\notes.xyz"));
        Assert.Same(FileKinds.AnyFile, FileKinds.IdentifyByName(@"C:\p\README"));
        Assert.Same(FileKinds.Png, FileKinds.IdentifyByName(@"C:\p\a.png"));
        Assert.NotNull(FileKinds.Find("file"));
        Assert.NotNull(FileKinds.Find("folder"));
    }

    [Fact]
    public void A_folder_gets_info_and_copy_path_but_no_hash() => Assert.Equal(["files.info", "files.copy-path", "archive.zip"], Ids(@"C:\p\holiday\"));

    [Fact]
    public void An_image_keeps_v1s_five_categories_and_gets_the_general_commands_under_tools()
    {
        // A sixth category would narrow V1's top ring until "Compress" reaches into the hub gap (UiSurfaceTests): the
        // module shares Tools, as categories are designed to be shared (ADR-0019).
        var menu = RadialMenu.Build(Resolver, [@"C:\p\a.png"]);
        Assert.Equal(["Convert", "Compress", "Resize", "Rotate", "Tools"], menu.Select(g => g.Label));
        Assert.Equal(["Remove metadata", "Image info", "File info", "SHA-256", "Copy path", "Add to ZIP"], menu[^1].Commands.Select(c => c.Label));
    }

    [Fact]
    public void A_mixed_selection_gets_only_what_every_item_allows()
    {
        Assert.Equal(["files.info", "files.copy-path", "archive.zip"], Ids(@"C:\p\a.png", @"C:\p\holiday\"));
        Assert.Equal(General, Ids(@"C:\p\a.png", @"C:\p\notes.txt"));
    }

    [Fact]
    public void A_file_no_module_reads_gets_a_usable_one_category_menu()
    {
        var menu = RadialMenu.Build(Resolver, [@"C:\p\notes.xyz"]);
        Assert.Equal(["Tools"], menu.Select(g => g.Label));
        Assert.True(MenuNavigator.BackSweep(menu.Count, menu[0].Commands.Count) < Math.PI); // commands are reachable
    }

    [Fact]
    public void Copy_path_runs_once_for_the_selection_and_the_rest_once_per_file()
    {
        var byId = FilesCapabilities.Catalog.ToDictionary(c => c.Id);
        Assert.Equal(CapabilityShape.Selection, byId["files.copy-path"].Shape);
        Assert.Equal(CapabilityOutput.Clipboard, byId["files.copy-path"].Produces);
        Assert.Equal(CapabilityShape.PerFile, byId["files.info"].Shape);
        Assert.Equal(CapabilityOutput.Facts, byId["files.sha256"].Produces);
        Assert.True(byId["files.sha256"].LongRunning);
        Assert.All(FilesCapabilities.Catalog, c => Assert.Equal(FilesCapabilities.Module, c.Module));
    }

    [Fact]
    public void Each_module_brings_its_own_feedback_words()
    {
        var image = CapabilityResolver.For(null).Catalog.First(c => c.Id == "convert.to-webp");
        Assert.Equal("Converted to WebP", image.Words.Done);
        Assert.Equal("image", image.Words.Noun);
        var hash = FilesCapabilities.Catalog.First(c => c.Id == "files.sha256");
        Assert.Equal("Hashing", hash.Words.Working);
        Assert.Equal("file", hash.Words.Noun);
    }

    [Fact]
    public void The_copied_text_is_one_path_per_line_and_folders_lose_their_mark() =>
        Assert.Equal("C:\\p\\a.png\r\nC:\\p\\holiday", FilesCapabilities.ClipboardText([@"C:\p\a.png", @"C:\p\holiday\"]));
}
