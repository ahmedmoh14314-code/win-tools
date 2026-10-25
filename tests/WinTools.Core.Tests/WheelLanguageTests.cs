using WinTools.Core.Capabilities;
using WinTools.Core.Feedback;
using WinTools.Core.Interaction;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;

namespace WinTools.Core.Tests;

/// <summary>
/// The wheel, its toasts and its dialogs in English and Arabic: every command and group has text in both tables, the
/// English one exactly as the module declares it (English never changes); in Arabic the wheel and the toasts carry no
/// English words but format names.
/// </summary>
[Collection("Language")]
public sealed class WheelLanguageTests
{
    private static readonly CapabilityResolver Resolver = CapabilityResolver.For(null);

    [Fact]
    public void Every_command_and_group_has_its_text_in_both_languages_and_English_is_as_declared()
    {
        foreach (var c in Resolver.Catalog)
        {
            Assert.Equal(c.Label, UiText.Get(UiText.English, "cmd." + c.Id));
            Assert.False(string.IsNullOrWhiteSpace(UiText.Get(UiText.Arabic, "cmd." + c.Id)), c.Id);
        }
        foreach (var g in Resolver.Categories)
        {
            Assert.Equal(g.Label, UiText.Get(UiText.English, "cat." + g.Id));
            Assert.False(string.IsNullOrWhiteSpace(UiText.Get(UiText.Arabic, "cat." + g.Id)), g.Id);
        }
    }

    [Fact]
    public void In_Arabic_the_wheel_speaks_Arabic_and_in_English_it_is_unchanged()
    {
        try
        {
            UiText.Use("ar");
            var menu = RadialMenu.Build(Resolver, [@"C:\p\a.png"]);
            Assert.Equal(["تحويل", "أدوات", "ضغط", "الحجم", "تدوير", "إجراءات"], menu.Select(g => g.Label));
            Assert.Contains("إلى PNG", menu[0].Commands.Select(c => c.Label).Concat(RadialMenu.Build(Resolver, [@"C:\p\a.jpg"])[0].Commands.Select(c => c.Label)));
            Assert.Equal("رجوع", MenuNavigator.BackLabel);
            UiText.Use("en");
            Assert.Equal(["Convert", "Tools", "Compress", "Resize", "Rotate", "Actions"], RadialMenu.Build(Resolver, [@"C:\p\a.png"]).Select(g => g.Label));
            Assert.Equal("Back", MenuNavigator.BackLabel);
        }
        finally { UiText.Use("en"); }
    }

    [Fact]
    public void Arabic_toasts_use_the_commands_name_and_never_an_English_sentence()
    {
        try
        {
            UiText.Use("ar");
            var convert = Resolver.Catalog.Single(c => c.Id == "convert.to-png");
            var words = LocalWords.For(convert);
            var ok = FeedbackText.For(words, [new JobItem("photo.jpg", JobResult.Ok("1", new JobOutput(@"C:\p\photo.png", new string('a', 64))))]);
            Assert.Equal("تم إنشاء photo.png", ok.Title);
            var failed = FeedbackText.For(words, [new JobItem("photo.jpg", JobResult.Fail("1", ErrorCode.DecodeFailed, "x"))]);
            Assert.Equal("تعذّر تنفيذ «إلى PNG»: photo.jpg", failed.Title);
            Assert.Equal("الصورة تالفة أو غير مكتملة.", failed.Detail);
            var pdf = FeedbackText.For(LocalWords.For(Resolver.Catalog.Single(c => c.Id == PdfCapabilities.RotateRightId)),
                [new JobItem("a.pdf", JobResult.Fail("1", ErrorCode.DecodeFailed, "x"))]);
            Assert.Equal("ملف PDF تالف أو غير مكتمل.", pdf.Detail);
        }
        finally { UiText.Use("en"); }
    }

    [Fact]
    public void English_toasts_read_exactly_as_before()
    {
        var convert = Resolver.Catalog.Single(c => c.Id == "convert.to-png");
        var failed = FeedbackText.For(LocalWords.For(convert), [new JobItem("photo.jpg", JobResult.Fail("1", ErrorCode.DecodeFailed, "x"))]);
        Assert.Equal("Could not convert photo.jpg", failed.Title);
        Assert.Equal("The image is damaged or incomplete.", failed.Detail);
    }
    [Fact]
    public void Arabic_counts_what_the_command_handles_and_explains_problems_in_Arabic()
    {
        try
        {
            UiText.Use("ar");
            Assert.Equal("ملفات", LocalWords.For(Resolver.Catalog.Single(c => c.Id == "archive.zip")).Nouns); // photos added to a ZIP are files, not archives
            Assert.Equal("مسارات", LocalWords.For(Resolver.Catalog.Single(c => c.Id == "files.copy-path")).Nouns);
            Assert.Equal("صور", LocalWords.For(Resolver.Catalog.Single(c => c.Id == "convert.to-png")).Nouns);
            // Sentences the password policy and Batch rename state in English, and the image info labels.
            var problems = new[]
            {
                PasswordPolicy.CheckNew("", s => s, s => s).Problem!, PasswordPolicy.CheckExisting("", s => s).Problem!,
                Files.BatchRename.Problem("")!, Files.BatchRename.Problem("a.")!, Files.BatchRename.Problem("con.txt")!, Files.BatchRename.Problem("a?.txt")!,
            };
            foreach (var p in problems) Assert.NotEqual(p, UiText.Say(p));
            foreach (var label in new[] { "Format", "Dimensions", "File size", "Resolution", "Transparency", "Orientation", "Colour profile", "GPS location" })
                Assert.NotEqual(label, UiText.T("fact." + label));
            UiText.Use("en");
            foreach (var p in problems) Assert.Equal(p, UiText.Say(p));
        }
        finally { UiText.Use("en"); }
    }
}
