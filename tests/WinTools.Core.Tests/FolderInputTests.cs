using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;

namespace WinTools.Core.Tests;

/// <summary>
/// UX-009, V2 PR 1.3: folders in a drop are handled explicitly, never silently. The drop target marks a folder with a
/// trailing separator (the Windows convention), found off the UI thread; from there on a folder is a kind like any
/// other, which a capability accepts or not.
/// </summary>
public sealed class FolderInputTests
{
    [Theory]
    [InlineData(@"C:\photos\holiday\")]
    [InlineData(@"C:\photos\holiday.png\")]                 // a folder named like an image is still a folder
    [InlineData(@"\\server\share\scans\")]
    [InlineData(@"C:\repo\.git\")]                           // a folder name without a stem is fine
    public void A_path_marked_as_a_folder_is_a_folder_and_supported(string path)
    {
        Assert.Same(FileKinds.Folder, FileKinds.IdentifyByName(path));
        Assert.Equal(FileFamily.Folder, FileKinds.Folder.Family);
        Assert.Equal(PathVerdict.Supported, PathPolicy.Explain(path));
    }

    [Theory]
    [InlineData(@"C:\")]                                     // a drive root
    [InlineData(@"\\server\share\")]                         // a share root
    [InlineData(@"C:\photos\\")]                             // two separators: not canonical
    [InlineData(@"C:\photos\holiday.\")]                     // a trailing dot Win32 would strip
    [InlineData(@"C:\photos\holiday \")]                     // a trailing space: without the separator, another folder
    [InlineData(@"\\server\share\a \")]
    [InlineData(@"C:\a\nul\")]                               // device names once the separator is trimmed
    [InlineData(@"C:\x\COM1\")]
    [InlineData(@"C:\x\com1.png")]                           // a file named after a device, any case, any extension
    [InlineData(@"C:\aux\photo.png")]                        // a device name as a folder on the way
    public void Roots_and_odd_folder_forms_are_refused(string path)
    {
        Assert.NotEqual(PathVerdict.Supported, PathPolicy.Explain(path));
    }

    [Fact]
    public void Image_commands_are_never_offered_for_a_folder()
    {
        Assert.Empty(CapabilityResolver.Images(null).Resolve([@"C:\photos\holiday.png\"]));
        Assert.Empty(CapabilityResolver.Images(null).Resolve([@"C:\photos\a.png", @"C:\photos\holiday\"]));
    }

    [Fact]
    public void A_capability_that_accepts_folders_is_offered_for_them()
    {
        var folders = new CapabilityDescriptor("files.copy-path", "Copy path", "tools", "files",
            new InputRule(new HashSet<string> { "folder", "png", "jpeg", "webp", "bmp", "tiff" }), CapabilityShape.Selection, CapabilityOutput.Clipboard, false);
        var resolver = new CapabilityResolver([folders]);
        Assert.Equal(["files.copy-path"], resolver.Resolve([@"C:\photos\holiday\", @"C:\photos\a.png"]).Select(c => c.Id));
    }

    [Fact]
    public void The_ring_says_why_when_folders_get_no_commands()
    {
        // Only when the folders are the reason (V2 PR 1.4: with General Files, folders do get commands in the product).
        var images = CapabilityResolver.Images(null);
        Assert.Equal("Not for folders", RadialMenu.EmptyReason(images, [@"C:\photos\holiday\"]));
        Assert.Equal("Not for folders", RadialMenu.EmptyReason(images, [@"C:\photos\a.png", @"C:\photos\holiday\"]));
        Assert.Null(RadialMenu.EmptyReason(images, [@"C:\photos\notes.txt"]));  // the general "No actions"
        var product = CapabilityResolver.For(null);
        Assert.Null(RadialMenu.EmptyReason(product, [@"C:\photos\holiday\"])); // File info and Copy path are offered
        string[] tooMany = [@"C:\photos\holiday\", .. Enumerable.Range(0, CapabilityResolver.MaxFiles + 1).Select(i => $@"C:\photos\{i}.png")];
        Assert.Null(RadialMenu.EmptyReason(product, tooMany)); // empty for its size, not for the folder
    }

    [Fact]
    public void A_folder_where_a_file_was_expected_has_its_own_reason()
    {
        Assert.Contains("folder", FeedbackText.Reason(ErrorCode.IsAFolder), StringComparison.OrdinalIgnoreCase);
    }
}
