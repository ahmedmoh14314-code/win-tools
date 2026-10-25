using WinTools.Core.Capabilities;
using WinTools.Core.Files;
using WinTools.Core.Interaction;

namespace WinTools.Core.Tests;

/// <summary>
/// V2 PR 5.1 (ADR-0023): the archive entry name policy, the first fence against a hostile archive, and the module's place
/// in the menu.
/// </summary>
public sealed class ArchiveCapabilitiesTests
{
    private static readonly CapabilityResolver Resolver = CapabilityResolver.For(customResizeId: null);
    private static string[] Ids(params string[] paths) => [.. Resolver.Resolve(paths).Select(c => c.Id)];

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:evil.txt")]
    [InlineData("//server/share/x")]
    [InlineData("file.txt:hidden")]
    [InlineData("a\u0001b.txt")]
    [InlineData("")]
    [InlineData("...")]
    [InlineData("a/ . /b")]
    public void A_name_that_could_leave_the_folder_or_hide_data_is_refused(string name)
    {
        var verdict = ArchiveEntryPolicy.Judge(name);
        Assert.Null(verdict.RelativePath);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Refusal));
    }

    [Theory]
    [InlineData("photos/2026/a.jpg", "photos\\2026\\a.jpg", false)]
    [InlineData("./notes.txt", "notes.txt", false)]
    [InlineData("a//b.txt", "a\\b.txt", false)]
    [InlineData("صور/😀 رحلة.jpg", "صور\\😀 رحلة.jpg", false)]
    [InlineData("report.", "report", true)]
    [InlineData("name ", "name", true)]
    [InlineData("NUL", "_NUL", true)]
    [InlineData("con.txt", "_con.txt", true)]
    [InlineData("dir/COM1.log", "dir\\_COM1.log", true)]
    [InlineData("what?.txt", "what_.txt", true)]
    [InlineData("a<b>|c*.txt", "a_b__c_.txt", true)]
    public void Ordinary_names_pass_and_odd_ones_are_renamed_the_same_way_every_time(string name, string expected, bool renamed)
    {
        var verdict = ArchiveEntryPolicy.Judge(name);
        Assert.Null(verdict.Refusal);
        Assert.Equal(expected, verdict.RelativePath);
        Assert.Equal(renamed, verdict.Renamed);
        Assert.Equal(verdict, ArchiveEntryPolicy.Judge(name));
    }

    [Fact]
    public void A_folder_entry_is_a_folder_and_very_deep_or_long_names_are_refused()
    {
        Assert.True(ArchiveEntryPolicy.Judge("photos/").IsFolder);
        Assert.NotNull(ArchiveEntryPolicy.Judge(string.Join('/', Enumerable.Repeat("d", ArchiveEntryPolicy.MaxDepth + 1))).Refusal);
        Assert.NotNull(ArchiveEntryPolicy.Judge(new string('x', 256) + ".txt").Refusal);
    }

    [Fact]
    public void The_second_fence_catches_a_path_outside_the_folder()
    {
        Assert.True(ArchiveEntryPolicy.IsInside(@"C:\out\x", @"C:\out\x\a\b.txt"));
        Assert.False(ArchiveEntryPolicy.IsInside(@"C:\out\x", @"C:\out\x2\b.txt")); // a sibling that starts with the same text
        Assert.False(ArchiveEntryPolicy.IsInside(@"C:\out\x", @"C:\out\b.txt"));
    }

    [Fact]
    public void An_archive_bomb_is_judged_by_the_bytes_that_came_out()
    {
        Assert.False(ArchiveLimits.IsBomb(500_000, 100)); // tiny files may compress well
        Assert.False(ArchiveLimits.IsBomb(100 << 20, 1 << 20)); // 100:1
        Assert.True(ArchiveLimits.IsBomb(300 << 20, 1 << 20)); // 300:1 past 1 MB
    }

    [Fact]
    public void Overlapping_entries_cannot_cheat_the_ratio_rule()
    {
        // Evaluator, PR 5.1 (reproduced: 10 entries sharing one stream inflated 2.4 MB to 2.9 GB).
        Assert.True(ArchiveLimits.IsArchiveBomb(2_930_000_000, 2_400_000));
        Assert.False(ArchiveLimits.IsArchiveBomb(300_000_000, 2_400_000)); // 125:1
        Assert.False(ArchiveLimits.IsArchiveBomb(20 << 20, 50 << 10)); // 50 KB of logs to 20 MB: 400:1, but small
    }

    [Fact]
    public void Folders_count_against_the_budget_and_a_file_blocks_a_folder_of_its_name()
    {
        var budget = new ArchiveLimits.Budget(max: 5);
        Assert.True(budget.AddFile(@"a\b\c\x.txt")); // three folders and a file
        Assert.Equal(4, budget.Created);
        Assert.True(budget.AddFolder("d"));
        Assert.False(budget.AddFolder(@"e\f")); // two more would pass 5
        Assert.False(budget.AddFolder(@"a\b\c\x.txt")); // a file has that name
        Assert.False(budget.AddFile(@"a\b")); // a folder has that name
        Assert.True(budget.IsFile(@"a\b\c\x.txt"));
    }

    [Theory]
    [InlineData("COM0.log", "_COM0.log")]
    [InlineData("lpt0", "_lpt0")]
    public void Every_device_name_windows_reserves_is_renamed(string name, string expected) =>
        Assert.Equal(expected, ArchiveEntryPolicy.Judge(name).RelativePath);

    [Fact]
    public void A_success_with_a_notice_says_it_in_the_toast()
    {
        var words = ArchiveCapabilities.Catalog.Single(c => c.Id == ArchiveCapabilities.ExtractId).Words;
        var ok = new Jobs.JobResult("x", true, Jobs.ErrorCode.None, null, [new Jobs.JobOutput(@"C:\p\photos", new string('a', 64))],
            [new Jobs.JobFact("Files", "3"), new Jobs.JobFact("Skipped", "2 entries not extracted (links, devices or unsafe names)", Notice: true)]);
        var message = Feedback.FeedbackText.For(words, [new Feedback.JobItem("photos.zip", ok)]);
        Assert.Contains("2 entries not extracted", message.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zip_gets_its_own_category_and_any_selection_can_be_zipped_from_tools()
    {
        var menu = RadialMenu.Build(Resolver, [@"C:\p\photos.zip"]);
        Assert.Equal(["Archive", "Tools"], menu.Select(g => g.Label));
        Assert.Equal(["Extract to folder", "Test", "Archive info"], menu[0].Commands.Select(c => c.Label)); // TAR and TAR.GZ too since V2 PR 5.2
        Assert.Contains("archive.zip", Ids(@"C:\p\a.png", @"C:\p\b.pdf", @"C:\p\holiday\"));
        Assert.Equal(["Convert", "Compress", "Resize", "Rotate", "Tools"], RadialMenu.Build(Resolver, [@"C:\p\a.png"]).Select(g => g.Label)); // no new sector
        Assert.DoesNotContain("archive.extract", Ids(@"C:\p\a.zip", @"C:\p\b.png"));
    }

    [Fact]
    public void Outputs_are_named_after_the_archive_or_the_selection()
    {
        Assert.Equal([@"C:\p\photos", @"C:\p\photos (1)"], ArchiveCapabilities.Candidates(ArchiveCapabilities.ExtractId, [@"C:\p\photos.zip"], null).Take(2));
        Assert.Equal(@"C:\p\report.zip", ArchiveCapabilities.Candidates(ArchiveCapabilities.ZipId, [@"C:\p\report.pdf"], null).First());
        Assert.Equal(@"C:\p\holiday.zip", ArchiveCapabilities.Candidates(ArchiveCapabilities.ZipId, [@"C:\p\holiday\"], null).First());
        Assert.Equal(@"C:\work\p\p.zip", ArchiveCapabilities.Candidates(ArchiveCapabilities.ZipId, [@"C:\work\p\a.png", @"C:\work\p\b.png"], null).First());
        Assert.Equal(@"D:\out\photos", ArchiveCapabilities.Candidates(ArchiveCapabilities.ExtractId, [@"C:\p\photos.zip"], @"D:\out").First());
    }
}
