using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;

namespace WinTools.Core.Tests;

/// <summary>
/// V2 PR 7.2: Batch rename's rule, previewed in the dialog and applied by the host (prefix, suffix, find and replace,
/// spaces and underscores, case, numbering), and the two new copy commands. Pure: the same names and rule always give
/// the same plan. Written before the code.
/// </summary>
public sealed class BatchRenameTests
{
    private static string[] To(IReadOnlyList<RenameItem> plan) => [.. plan.Select(i => i.To)];

    [Fact]
    public void Each_rule_changes_the_name_and_keeps_the_extension()
    {
        string[] names = ["Holiday photo.JPG", "notes.txt"];
        Assert.Equal(["2026 Holiday photo.JPG", "2026 notes.txt"], To(BatchRename.Plan(names, new(Prefix: "2026 "))));
        Assert.Equal(["Holiday photo (old).JPG", "notes (old).txt"], To(BatchRename.Plan(names, new(Suffix: " (old)"))));
        Assert.Equal(["Holiday pic.JPG", "notes.txt"], To(BatchRename.Plan(names, new(Find: "PHOTO", Replace: "pic")))); // any case
        Assert.Equal(["Holiday_photo.JPG", "notes.txt"], To(BatchRename.Plan(names, new(Spaces: RenameSpaces.ToUnderscores))));
        Assert.Equal(["a b.txt"], To(BatchRename.Plan(["a_b.txt"], new(Spaces: RenameSpaces.ToSpaces))));
        Assert.Equal(["holiday photo.JPG", "notes.txt"], To(BatchRename.Plan(names, new(Case: RenameCase.Lower))));
        Assert.Equal(["HOLIDAY PHOTO.JPG", "NOTES.txt"], To(BatchRename.Plan(names, new(Case: RenameCase.Upper))));
        Assert.Equal(["Holiday Photo.JPG", "Notes.txt"], To(BatchRename.Plan(names, new(Case: RenameCase.Title))));
        Assert.Equal(["صورة 😀 2026.png"], To(BatchRename.Plan(["صورة 😀.png"], new(Suffix: " 2026"))));
    }

    [Fact]
    public void Numbering_follows_the_natural_order_of_the_names()
    {
        var plan = BatchRename.Plan(["img10.png", "img2.png", "img1.png"], new(Number: true, Start: 1, Digits: 2));
        Assert.Equal(["img10 03.png", "img2 02.png", "img1 01.png"], To(plan)); // the plan keeps the input's order
        Assert.Equal(["a_01.txt", "b_02.txt"], To(BatchRename.Plan(["a.txt", "b.txt"], new(Number: true, Spaces: RenameSpaces.ToUnderscores))));
    }

    [Fact]
    public void Names_that_cannot_exist_on_Windows_or_clash_are_marked_and_block_the_rename()
    {
        var invalid = BatchRename.Plan(["a.txt"], new(Suffix: "?"));
        Assert.Equal(RenameStatus.Invalid, invalid[0].Status);
        Assert.False(BatchRename.CanApply(invalid));
        Assert.Equal(RenameStatus.Invalid, BatchRename.Plan(["a.txt"], new(Find: "a", Replace: "NUL"))[0].Status);
        Assert.Equal(RenameStatus.Invalid, BatchRename.Plan(["a.txt"], new(Find: "a", Replace: ""))[0].Status); // an empty name
        var clash = BatchRename.Plan(["a1.txt", "a2.txt"], new(Find: "1", Replace: "2"));
        Assert.Equal([RenameStatus.Clash, RenameStatus.Clash], clash.Select(i => i.Status));
        Assert.False(BatchRename.CanApply(clash));
    }

    [Fact]
    public void A_rule_that_changes_nothing_cannot_be_applied_and_a_case_change_alone_can()
    {
        var same = BatchRename.Plan(["a.txt"], new(Find: "zzz", Replace: "y"));
        Assert.Equal(RenameStatus.Unchanged, same[0].Status);
        Assert.False(BatchRename.CanApply(same));
        var upper = BatchRename.Plan(["a.txt"], new(Case: RenameCase.Upper));
        Assert.Equal(RenameStatus.Renamed, upper[0].Status);
        Assert.True(BatchRename.CanApply(upper));
    }

    [Fact]
    public void A_rule_travels_as_one_parameter_and_an_unreasonable_one_is_refused()
    {
        var rule = new RenameRule(Prefix: "x ", Find: "a", Replace: "b", Spaces: RenameSpaces.ToUnderscores, Case: RenameCase.Title, Number: true, Start: 7, Digits: 3);
        Assert.Equal(rule, RenameRule.Parse(rule.Serialize()));
        Assert.Null(RenameRule.Parse("{not json"));
        Assert.Null(RenameRule.Parse(new RenameRule(Prefix: new string('x', 101)).Serialize()));
        Assert.Null(RenameRule.Parse(new RenameRule(Digits: 9).Serialize()));
        Assert.Null(RenameRule.Parse(new RenameRule(Start: -1).Serialize()));
    }

    [Fact]
    public void General_files_offer_copy_name_copy_folder_path_and_batch_rename()
    {
        var byId = FilesCapabilities.Catalog.ToDictionary(c => c.Id);
        Assert.Equal("Copy name", byId[FilesCapabilities.CopyNameId].Label);
        Assert.Equal("Copy folder path", byId[FilesCapabilities.CopyFolderId].Label);
        Assert.Equal("Batch rename…", byId[FilesCapabilities.RenameId].Label);
        Assert.Equal(DialogKinds.Rename, byId[FilesCapabilities.RenameId].Dialog);
        Assert.Equal(CapabilityOutput.Clipboard, byId[FilesCapabilities.CopyNameId].Produces);
        Assert.Equal("a.png\r\nholiday", FilesCapabilities.ClipboardText(FilesCapabilities.CopyNameId, [@"C:\p\a.png", @"C:\p\holiday\"]));
        Assert.Equal(@"C:\p", FilesCapabilities.ClipboardText(FilesCapabilities.CopyFolderId, [@"C:\p\a.png", @"C:\p\b.png"])); // once
        Assert.Equal("C:\\p\\a.png\r\nC:\\p\\holiday", FilesCapabilities.ClipboardText(FilesCapabilities.CopyPathId, [@"C:\p\a.png", @"C:\p\holiday\"]));
        // Every menu still builds: Tools holds at most seven commands, the rest under "More…".
        var resolver = CapabilityResolver.For(null);
        foreach (var path in new[] { @"C:\p\a.png", @"C:\p\a.pdf", @"C:\p\a.zip", @"C:\p\notes.txt", @"C:\p\holiday\" })
            Assert.All(RadialMenu.Build(resolver, [path]), g => Assert.True(g.Commands.Count <= RadialMenu.RingCapacity - 1));
        Assert.DoesNotContain(FilesCapabilities.RenameId, resolver.Resolve([@"C:\p\holiday\"]).Select(c => c.Id)); // files only
    }
}
