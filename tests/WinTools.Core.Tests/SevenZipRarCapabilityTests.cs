using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Files;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;

namespace WinTools.Core.Tests;

/// <summary>
/// ADR-0030: 7z and RAR join the Archive category with the same three commands as a ZIP, and what the worker reports
/// about them (types, protection, parts, unchecked files, the reason for a refused part) reads in English and Arabic.
/// </summary>
[Collection("Language")]
public sealed class SevenZipRarCapabilityTests
{
    private static readonly CapabilityResolver Resolver = CapabilityResolver.For(customResizeId: null);

    [Fact]
    public void A_7z_or_rar_gets_the_same_archive_commands_as_a_zip()
    {
        Assert.Equal(FileKinds.SevenZip, FileKinds.IdentifyByName(@"C:\p\a.7z"));
        Assert.Equal(FileKinds.Rar, FileKinds.IdentifyByName(@"C:\p\a.part1.RAR"));
        foreach (var path in new[] { @"C:\p\photos.7z", @"C:\p\photos.rar" })
        {
            var menu = RadialMenu.Build(Resolver, [path]);
            Assert.Equal(["Tools", "Archive", "Actions"], menu.Select(g => g.Label));
            Assert.Equal(["Extract to folder", "Test", "Archive info"], menu[1].Commands.Select(c => c.Label));
        }
        Assert.Equal(@"C:\p\photos", ArchiveCapabilities.Candidates(ArchiveCapabilities.ExtractId, [@"C:\p\photos.7z"], null).First());
        Assert.Equal(@"C:\p\backup", ArchiveCapabilities.Candidates(ArchiveCapabilities.ExtractId, [@"C:\p\backup.rar"], null).First());
        // ".7z.001" names no kind: the first file of a split 7z is not offered as an archive.
        Assert.DoesNotContain("archive.extract", Resolver.Resolve([@"C:\p\a.7z.001"]).Select(c => c.Id));
    }

    [Fact]
    public void Only_a_split_archive_is_called_one_in_either_language()
    {
        var extract = ArchiveCapabilities.Catalog.Single(c => c.Id == ArchiveCapabilities.ExtractId);
        try
        {
            UiText.Use("en");
            Assert.Contains("split into several files", FeedbackText.Reason(ErrorCode.SplitArchive, LocalWords.For(extract)), StringComparison.Ordinal);
            // A TAR's sparse entry or a 7z's unknown method is not a split archive.
            Assert.DoesNotContain("split", FeedbackText.Reason(ErrorCode.UnsupportedContent, LocalWords.For(extract)), StringComparison.Ordinal);
            UiText.Use("ar");
            Assert.Contains("مقسّم", FeedbackText.Reason(ErrorCode.SplitArchive, LocalWords.For(extract)), StringComparison.Ordinal);
            Assert.DoesNotContain("مقسّم", FeedbackText.Reason(ErrorCode.UnsupportedContent, LocalWords.For(extract)), StringComparison.Ordinal);
        }
        finally { UiText.Use("en"); }
    }

    [Theory]
    [InlineData("7z archive, solid", "أرشيف 7z، مضغوط ككتلة واحدة")]
    [InlineData("RAR 5 archive", "أرشيف RAR 5")]
    [InlineData("RAR 4 archive, solid", "أرشيف RAR 4، مضغوط ككتلة واحدة")]
    [InlineData("Contents protected with a password", "المحتوى محمي بكلمة مرور")]
    [InlineData("Names and contents protected with a password", "الأسماء والمحتوى محمية بكلمة مرور")]
    [InlineData("No errors in the files that could be checked", "لا أخطاء في الملفات التي أمكن فحصها")]
    [InlineData("No errors: every file's check value matches", "لا أخطاء: قيمة الفحص لكل ملف مطابقة")]
    [InlineData("Password-protected RAR 5 files cannot all be checked for damage", "لا يمكن فحص كل ملفات RAR 5 المحمية بكلمة مرور بحثًا عن التلف")]
    [InlineData("The end of this old RAR archive could not be confirmed; files may be missing", "تعذّر التأكد من نهاية أرشيف RAR القديم هذا؛ قد تكون بعض الملفات مفقودة")]
    [InlineData("One part of an archive split into several files, which WinTools does not extract yet", "جزء من أرشيف مقسّم إلى عدة ملفات، ولا يستخرج WinTools هذا النوع بعد")]
    public void What_info_and_test_say_about_a_7z_or_rar_reads_in_arabic(string english, string arabic)
    {
        try
        {
            UiText.Use("ar");
            Assert.Equal(arabic, UiText.Value(english));
            Assert.Equal("الأجزاء", UiText.TOr("fact.Parts", "Parts"));
        }
        finally { UiText.Use("en"); }
    }
}
