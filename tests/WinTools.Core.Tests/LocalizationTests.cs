using System.Text.RegularExpressions;
using WinTools.Core.Jobs;
using WinTools.Core.Localization;
using WinTools.Core.Settings;

namespace WinTools.Core.Tests;

/// <summary>
/// The product's text in English and Arabic (V2 app shell): both tables are complete, their placeholders agree, the
/// language follows the setting or Windows, and the settings file keeps the language and the tour's state.
/// </summary>
[Collection("Language")]
public sealed partial class LocalizationTests
{
    [Fact]
    public void Arabic_has_every_English_key_and_nothing_else()
    {
        var en = UiText.Keys(UiText.English).ToHashSet();
        var ar = UiText.Keys(UiText.Arabic).ToHashSet();
        Assert.True(en.Count > 100);
        Assert.Empty(en.Except(ar));
        Assert.Empty(ar.Except(en));
    }

    [Fact]
    public void Every_translation_keeps_the_placeholders_of_the_English_text()
    {
        foreach (var key in UiText.Keys(UiText.English))
            Assert.Equal(Placeholders(UiText.Get(UiText.English, key)!), Placeholders(UiText.Get(UiText.Arabic, key)!));
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    private static string[] Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal)];

    [Fact]
    public void The_language_follows_the_setting_or_else_Windows()
    {
        Assert.Equal("ar", UiText.Resolve("ar", "en-US"));
        Assert.Equal("en", UiText.Resolve("en", "ar-EG"));
        Assert.Equal("ar", UiText.Resolve("System", "ar-SA"));
        Assert.Equal("en", UiText.Resolve("System", "fr-FR"));
        Assert.Equal("en", UiText.Resolve(null, null));
    }

    [Fact]
    public void Text_comes_in_the_language_in_use_and_falls_back_to_the_key()
    {
        try
        {
            UiText.Use("ar");
            Assert.True(UiText.IsRightToLeft);
            Assert.Equal("الإعدادات", UiText.T("nav.settings"));
            Assert.Equal("مفتاح التشغيل: Space", UiText.F("home.trigger", "Space"));
            Assert.Equal("no.such.key", UiText.T("no.such.key"));
            Assert.Equal("Fallback", UiText.TOr("no.such.key", "Fallback"));
            UiText.Use("en");
            Assert.False(UiText.IsRightToLeft);
            Assert.Equal("Settings", UiText.T("nav.settings"));
        }
        finally { UiText.Use("en"); }
    }

    [Fact]
    public void The_settings_file_keeps_the_language_and_whether_the_tour_was_done()
    {
        var s = new AppSettings { Language = "ar", Onboarded = true };
        var back = AppSettings.FromJson(s.ToJson())!;
        Assert.Equal("ar", back.Language);
        Assert.True(back.Onboarded);
        Assert.Equal("System", (AppSettings.Defaults with { Language = "fr" }).Normalized().Language);
        Assert.False(AppSettings.Defaults.Onboarded);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void No_key_is_in_a_table_twice(string language)
    {
        // The table loads with the last copy winning, so an edit to the first copy would be lost without a word.
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream($"WinTools.Core.Localization.Strings.{language}.json")!;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var reader = new System.Text.Json.Utf8JsonReader(bytes);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName) Assert.True(seen.Add(reader.GetString()!), $"{language}: {reader.GetString()} twice");
        Assert.True(seen.Count > 100);
    }

    [Fact]
    public void Counts_take_the_languages_plural_forms()
    {
        try
        {
            UiText.Use("ar");
            Assert.Equal("إعادة تسمية ملف واحد", UiText.Count("rename.heading", 1));
            Assert.Equal("إعادة تسمية ملفين", UiText.Count("rename.heading", 2));
            Assert.Equal("إعادة تسمية 5 ملفات", UiText.Count("rename.heading", 5));
            Assert.Equal("إعادة تسمية 11 ملفًا", UiText.Count("rename.heading", 11));
            Assert.Equal("إعادة تسمية 100 ملف", UiText.Count("rename.heading", 100));
            Assert.Equal("إعادة تسمية 1,003 ملفات", UiText.Count("rename.heading", 1003)); // 3-10 by the last two digits
            UiText.Use("en");
            Assert.Equal("Rename 1 file", UiText.Count("rename.heading", 1));
            Assert.Equal("Rename 2 files", UiText.Count("rename.heading", 2));
            Assert.Equal("Rename 1,000 files", UiText.Count("rename.heading", 1000));
        }
        finally { UiText.Use("en"); }
    }

    [Fact]
    public void Fact_values_that_are_words_are_translated_and_the_rest_kept()
    {
        try
        {
            UiText.Use("ar");
            Assert.Equal("نعم", UiText.Value("Yes"));
            Assert.Equal("XMP، نص أو تعليقات", UiText.Value("XMP, text or comments"));
            Assert.Equal("4032 × 3024 px", UiText.Value("4032 × 3024 px"));
            Assert.Equal("AAC, stereo, 44.1 kHz", UiText.Value("AAC, stereo, 44.1 kHz")); // only whole lists of words
            UiText.Use("en");
            Assert.Equal("XMP, text or comments", UiText.Value("XMP, text or comments"));
        }
        finally { UiText.Use("en"); }
    }

    [Fact]
    public void Every_password_and_rename_problem_reads_in_Arabic()
    {
        string?[] problems =
        [
            PasswordPolicy.CheckNew("").Problem, PasswordPolicy.CheckExisting("").Problem, PasswordPolicy.CheckExisting(new string('a', 2000)).Problem,
            PasswordPolicy.CheckNew("a\nb").Problem, PasswordPolicy.CheckNew("a\tb").Problem, PasswordPolicy.CheckNew("a\uD800b").Problem,
            PasswordPolicy.CheckNew("a\u200Fb").Problem, PasswordPolicy.CheckNew("a\uE000b").Problem, PasswordPolicy.CheckNew("a\uFE0Fb").Problem,
            PasswordPolicy.CheckNew("a\u200Db").Problem, PasswordPolicy.CheckNew(new string('a', 200)).Problem,
            PasswordPolicy.CheckNew("x²", s => s.Replace("²", "2", StringComparison.Ordinal), s => s).Problem,
            PasswordPolicy.CheckNew("ab", _ => "x", _ => "y").Problem, // a canonical difference: marks in another order
            WinTools.Core.Files.BatchRename.Problem(""), WinTools.Core.Files.BatchRename.Problem(new string('a', 300) + ".txt"),
            WinTools.Core.Files.BatchRename.Problem("a?.txt"), WinTools.Core.Files.BatchRename.Problem("a."), WinTools.Core.Files.BatchRename.Problem("con.txt"),
        ];
        try
        {
            UiText.Use("ar");
            for (var i = 0; i < problems.Length; i++)
            {
                var p = problems[i];
                Assert.True(p is not null, $"case {i} found no problem");
                Assert.True(UiText.Get(UiText.Arabic, "say." + p) is not null, $"no Arabic for: {p}");
            }
        }
        finally { UiText.Use("en"); }
    }
}
