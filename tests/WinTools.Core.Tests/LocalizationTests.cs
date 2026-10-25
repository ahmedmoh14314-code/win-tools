using System.Text.RegularExpressions;
using WinTools.Core.Localization;
using WinTools.Core.Settings;

namespace WinTools.Core.Tests;

/// <summary>
/// The product's text in English and Arabic (V2 app shell): both tables are complete, their placeholders agree, the
/// language follows the setting or Windows, and the settings file keeps the language and the tour's state.
/// </summary>
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
}
