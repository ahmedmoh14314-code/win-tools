using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;

namespace WinTools.Core.Tests;

/// <summary>
/// ADR-0032, the file actions' pure rules: which destination the folder window may answer, how a copied or moved item is
/// named there (never over another), which moves are refused, what the toast says, and where the commands sit.
/// </summary>
[Collection("Language")]
public sealed class FileActionsTests
{
    private static readonly CapabilityResolver Resolver = CapabilityResolver.For(customResizeId: null);
    private static CapabilityDescriptor Command(string id) => FilesCapabilities.Catalog.Single(c => c.Id == id);

    [Theory]
    [InlineData(@"C:\Users\me\Documents", @"C:\Users\me\Documents")]
    [InlineData(@"C:\Users\me\Documents\", @"C:\Users\me\Documents")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"D:", null)]                      // relative to D:'s current folder
    [InlineData(@"\\server\share\photos", @"\\server\share\photos")]
    [InlineData(@"C:\Users\me\مستندات 😀", @"C:\Users\me\مستندات 😀")]
    [InlineData(@"Documents", null)]
    [InlineData(@"\\?\C:\Users", null)]
    [InlineData(@"\\.\PhysicalDrive0", null)]
    [InlineData(@"C:\a\b:stream", null)]
    [InlineData(@"C:\a\nul", null)]
    [InlineData(@"C:\a\.\b", null)]
    [InlineData(@"C:\a\b. ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_a_plain_folder_path_is_a_destination(string? picked, string? expected) => Assert.Equal(expected, TransferPlan.Destination(picked));

    [Fact]
    public void A_file_keeps_its_extension_and_takes_the_next_free_number()
    {
        var names = TransferPlan.Candidates(@"C:\p\report.pdf", @"D:\out").Take(3).ToArray();
        Assert.Equal([@"D:\out\report.pdf", @"D:\out\report (1).pdf", @"D:\out\report (2).pdf"], names);
        Assert.Equal(1 + WinTools.Core.Output.OutputNaming.MaxCollisionIndex, TransferPlan.Candidates(@"C:\p\report.pdf", @"D:\out").Count());
        Assert.Equal(@"D:\README (1)", TransferPlan.Candidates(@"C:\p\README", @"D:\").ElementAt(1));
        Assert.Equal(@"D:\archive.tar (1).gz", TransferPlan.Candidates(@"C:\p\archive.tar.gz", @"D:\").ElementAt(1));
    }

    [Fact]
    public void A_folder_keeps_its_whole_name()
    {
        Assert.Equal([@"D:\v1.2", @"D:\v1.2 (1)"], TransferPlan.Candidates(@"C:\p\v1.2\", @"D:\").Take(2));
        Assert.Equal(@"D:\.git (1)", TransferPlan.Candidates(@"C:\p\.git\", @"D:\").ElementAt(1));
    }

    [Theory]
    [InlineData(@"C:\a\photos\", @"C:\a\photos", true)]
    [InlineData(@"C:\a\photos\", @"C:\a\photos\2024\june", true)]
    [InlineData(@"C:\a\photos\", @"c:\A\PHOTOS\x", true)]       // Windows compares names without case
    [InlineData(@"C:\a\photos\", @"C:\a\photos2", false)]       // a sibling whose name starts the same
    [InlineData(@"C:\a\photos\", @"C:\a", false)]
    [InlineData(@"C:\a\photos.zip", @"C:\a\photos.zip", false)] // a file never goes into itself
    public void A_folder_never_goes_into_itself(string item, string destination, bool refused) =>
        Assert.Equal(refused, TransferPlan.IntoItself(item, destination));

    [Fact]
    public void Already_there_means_the_same_folder()
    {
        Assert.True(TransferPlan.AlreadyThere(@"C:\a\b.txt", @"C:\A"));
        Assert.True(TransferPlan.AlreadyThere(@"C:\a\photos\", @"C:\a"));
        Assert.False(TransferPlan.AlreadyThere(@"C:\a\b.txt", @"C:\a\b"));
        Assert.True(TransferPlan.AlreadyThere(@"D:\b.txt", @"D:\"));
    }

    [Fact]
    public void A_name_shown_in_a_toast_hides_nothing_and_is_cut_whole()
    {
        Assert.Equal("ab?c", TransferPlan.ShowableName("ab\u202Ec"));
        Assert.Equal("a?b", TransferPlan.ShowableName("a\nb"));
        var cut = TransferPlan.ShowableName(new string('x', 78) + "😀😀", 80);
        Assert.Equal(new string('x', 78) + "…", cut); // the emoji's pair is never split
        Assert.True(JobFacts.AreShowable([new JobFact("Destination", TransferPlan.ShowableName("\u2066evil\u2069"))]));
    }

    [Fact]
    public void Copy_to_and_move_to_ask_for_a_folder_and_run_each_item_on_its_own()
    {
        foreach (var id in new[] { FilesCapabilities.CopyToId, FilesCapabilities.MoveToId })
        {
            var c = Command(id);
            Assert.Equal(DialogKinds.Folder, c.Dialog);
            Assert.Equal(CapabilityShape.PerFile, c.Shape);
            Assert.True(c.LongRunning);
            Assert.Equal("actions", c.Category);
            Assert.True(FilesCapabilities.IsTransfer(id));
        }
        foreach (var id in new[] { FilesCapabilities.ShowId, FilesCapabilities.TerminalId })
        {
            var c = Command(id);
            Assert.Equal(CapabilityOutput.Window, c.Produces);
            Assert.Null(c.Dialog);
            Assert.False(c.LongRunning);
            Assert.True(FilesCapabilities.IsWindow(id));
        }
        // Folders too, and anything a module reads.
        foreach (var path in new[] { @"C:\p\holiday\", @"C:\p\a.png", @"C:\p\a.pdf", @"C:\p\clip.mp4", @"C:\p\notes.xyz" })
            Assert.Contains(FilesCapabilities.MoveToId, Resolver.Resolve([path]).Select(c => c.Id));
    }

    [Fact]
    public void A_category_of_long_labels_shows_three_and_more()
    {
        // ADR-0032: with two categories Back takes half the ring, and a fifth sector in Actions was 36 px at 96 DPI.
        static CapabilityDescriptor Cmd(string id) => new(id, id, "a", "test", new InputRule(new HashSet<string> { "png" }), CapabilityShape.PerFile, CapabilityOutput.Facts, false);
        var categories = new CapabilityCategory[] { new("a", "A", "a", 1, MostOnRing: CapabilityCategory.LongLabels) };
        var four = RadialMenu.Build([.. Enumerable.Range(1, 4).Select(i => Cmd($"c{i}"))], categories).Single().Commands;
        Assert.Equal(["c1", "c2", "c3", "c4"], four.Select(c => c.Id));
        var six = RadialMenu.Build([.. Enumerable.Range(1, 6).Select(i => Cmd($"c{i}"))], categories).Single().Commands;
        Assert.Equal(["c1", "c2", "c3"], six.Take(3).Select(c => c.Id));
        Assert.True(RadialMenu.IsMore(six[3]));
        Assert.Equal(["c4", "c5", "c6"], RadialMenu.Overflow(six[3]).Select(c => c.Id));
    }

    [Fact]
    public void A_folder_gets_move_copy_and_its_place_under_more()
    {
        var actions = RadialMenu.Build(Resolver, [@"C:\p\holiday\"]).Single(g => g.Category.Id == "actions").Commands;
        Assert.Equal(["Copy path", "Move to…", "Copy to…", "More…"], actions.Select(c => c.Label));
        Assert.Equal(["Copy name", "Show in folder", "Open in Terminal", "Add to ZIP"], RadialMenu.Overflow(actions[^1]).Select(c => c.Label));
    }

    private static JobItem Moved(string name, params JobFact[] facts) =>
        new(name, new JobResult("r", true, ErrorCode.None, null, [new JobOutput($@"D:\Documents\{name}", "")], facts), 100, 100);

    [Fact]
    public void The_toast_says_what_was_copied_and_where()
    {
        var words = Command(FilesCapabilities.CopyToId).Words;
        var one = FeedbackText.For(words, [Moved("report.pdf", new JobFact("Destination", "to Documents", Notice: true))]);
        Assert.Equal((FeedbackKind.Success, "report.pdf copied", "Copied · to Documents"), (one.Kind, one.Title, one.Detail)); // no "100 bytes → 100 bytes"
        var renamed = FeedbackText.For(words, [Moved("report.pdf", new JobFact("Destination", "to Documents", Notice: true), new JobFact("Saved as", "as report (1).pdf", Notice: true))]);
        Assert.Equal("Copied · to Documents · as report (1).pdf", renamed.Detail);
        var three = FeedbackText.For(words, [Moved("a.txt", new JobFact("Destination", "to Documents", Notice: true)), Moved("b.txt", new JobFact("Destination", "to Documents", Notice: true),
            new JobFact("Saved as", "as b (1).txt", Notice: true)), Moved("c.txt", new JobFact("Destination", "to Documents", Notice: true))]);
        Assert.Equal(("3 items completed", "Copied · to Documents"), (three.Title, three.Detail)); // what every item shares, once
    }

    [Fact]
    public void A_move_with_nothing_to_do_or_a_failure_says_why()
    {
        var words = Command(FilesCapabilities.MoveToId).Words;
        var already = FeedbackText.For(words, [new JobItem("a.txt", JobResult.Fail("r", ErrorCode.NothingToDo, "x"))]);
        Assert.Equal((FeedbackKind.Nothing, "Already in that folder"), (already.Kind, already.Detail));
        var itself = FeedbackText.For(words, [new JobItem("photos", JobResult.Fail("r", ErrorCode.UnsupportedOperation, "x"))]);
        Assert.Equal(("Could not move photos", "A folder cannot go into itself. Nothing was changed."), (itself.Title, itself.Detail));
        var mixed = FeedbackText.For(words, [Moved("a.txt"), new JobItem("b.txt", JobResult.Fail("r", ErrorCode.SourceNotReadable, "x"))]);
        Assert.Equal(FeedbackKind.Partial, mixed.Kind);
        Assert.Contains("open in another program", mixed.Detail);
    }

    [Fact]
    public void Arabic_toasts_speak_arabic()
    {
        try
        {
            UiText.Use(UiText.Arabic);
            var words = LocalWords.For(Command(FilesCapabilities.MoveToId));
            var one = FeedbackText.For(words, [Moved("report.pdf", new JobFact("Destination", "إلى Documents", Notice: true))]);
            Assert.Equal("تم نقل report.pdf", one.Title);
            Assert.Equal("موجود في هذا المجلد بالفعل", words.NothingReason);
            Assert.Equal("لا يمكن وضع مجلد داخل نفسه. لم يتغيّر شيء.", words.Reasons![ErrorCode.UnsupportedOperation]);
            Assert.Equal("عناصر", words.Nouns);
            Assert.Equal("نقل إلى…", RadialMenu.Build(Resolver, [@"C:\p\notes.xyz"])[^1].Commands[1].Label);
            // Batch rename keeps its own translations.
            Assert.Equal("تمت إعادة التسمية", LocalWords.For(Command(FilesCapabilities.RenameId)).SuccessTitle);
        }
        finally { UiText.Use(UiText.English); }
    }

    [Fact]
    public void The_folder_request_carries_no_path_and_crosses_the_pipe()
    {
        var line = WorkerProtocol.Serialize(new FolderRequest("Choose where to copy", 10, 20));
        Assert.Equal(DialogKinds.Folder, WorkerProtocol.Deserialize<DialogEnvelope>(line)!.Kind);
        Assert.Equal(new FolderRequest("Choose where to copy", 10, 20), WorkerProtocol.Deserialize<FolderRequest>(line));
        Assert.Equal(@"D:\Out", WorkerProtocol.Deserialize<FolderResult>(WorkerProtocol.Serialize(new FolderResult(@"D:\Out")))!.Folder);
        Assert.Null(WorkerProtocol.Deserialize<FolderResult>("{\"folder\":null}")!.Folder);
    }
}
