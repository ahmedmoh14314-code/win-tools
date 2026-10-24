using System.Text.Json;
using WinTools.Core.Settings;

namespace WinTools.Core.Tests;

/// <summary>
/// V2 PR 1.5: settings schema 2 (sections per area), the migration from schema 1, Reset, and the trigger-key policy that
/// says why a key is refused.
/// </summary>
public sealed class SettingsSchemaTests
{
    private const string V1File = """
        {
          "schema": 1,
          "triggerKey": "F9",
          "outputPolicy": "Folder",
          "outputFolder": "D:\\Exports",
          "jpegQuality": 77,
          "webPQuality": 66,
          "resizeWidth": 1200,
          "resizeHeight": 0,
          "keepAspectRatio": false,
          "noUpscale": false,
          "theme": "Dark",
          "uiSounds": false,
          "animations": "Off"
        }
        """;

    private static readonly AppSettings Custom = new()
    {
        TriggerKey = "F9", OutputPolicy = OutputPolicy.Folder, OutputFolder = @"D:\Exports", JpegQuality = 77, WebPQuality = 66,
        ResizeWidth = 1200, ResizeHeight = 0, KeepAspectRatio = false, NoUpscale = false, Theme = "Dark", UiSounds = false, Animations = "Off",
    };

    [Fact]
    public void A_schema_1_file_keeps_every_value_after_migration()
    {
        var migrated = AppSettings.FromJson(V1File)!;
        Assert.Equal(Custom with { Schema = 2 }, migrated);
        Assert.Equal(2, migrated.Schema);
    }

    [Fact]
    public void Schema_2_is_written_in_sections_and_without_the_flat_fields()
    {
        using var doc = JsonDocument.Parse(Custom.ToJson());
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("schema").GetInt32());
        Assert.Equal("F9", root.GetProperty("trigger").GetProperty("key").GetString());
        Assert.Equal("Folder", root.GetProperty("output").GetProperty("policy").GetString());
        Assert.Equal(@"D:\Exports", root.GetProperty("output").GetProperty("folder").GetString());
        Assert.Equal(77, root.GetProperty("images").GetProperty("jpegQuality").GetInt32());
        Assert.Equal(1200, root.GetProperty("images").GetProperty("resizeWidth").GetInt32());
        Assert.False(root.GetProperty("ui").GetProperty("sounds").GetBoolean());
        Assert.Equal("Off", root.GetProperty("ui").GetProperty("animations").GetString());
        Assert.False(root.TryGetProperty("triggerKey", out _));
        Assert.False(root.TryGetProperty("jpegQuality", out _));
    }

    [Fact]
    public void Schema_2_reads_back_what_it_wrote()
    {
        Assert.Equal(Custom with { Schema = 2 }, AppSettings.FromJson(Custom.ToJson()));
        Assert.Equal(new AppSettings(), AppSettings.FromJson(new AppSettings().ToJson()));
    }

    [Fact]
    public void A_schema_2_file_with_missing_sections_or_odd_values_is_repaired_not_refused()
    {
        var s = AppSettings.FromJson("""{ "schema": 2, "images": { "jpegQuality": 400 }, "trigger": { "key": "F1" } }""")!;
        Assert.Equal(100, s.JpegQuality);
        Assert.Equal("Space", s.TriggerKey);
        Assert.Equal(new AppSettings().WebPQuality, s.WebPQuality);
    }

    [Fact]
    public void Reset_gives_the_defaults()
    {
        Assert.Equal(new AppSettings(), AppSettings.Defaults);
        Assert.Equal("Space", AppSettings.Defaults.TriggerKey);
    }

    [Theory]
    [InlineData("Space", true)]
    [InlineData("F8", true)]
    [InlineData("F12", true)]
    [InlineData("F1", false)]
    [InlineData("F2", false)]
    [InlineData("F5", false)]
    [InlineData("A", false)]
    public void Every_key_offered_or_refused_has_a_reason(string key, bool allowed)
    {
        var verdict = TriggerKeyPolicy.Judge(key);
        Assert.Equal(allowed, verdict.Allowed);
        if (!allowed) Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        Assert.Equal(allowed, AppSettings.TriggerKeys.ContainsKey(key));
    }

    [Fact]
    public void Only_allowed_keys_are_trigger_keys() =>
        Assert.All(AppSettings.TriggerKeys.Keys, k => Assert.True(TriggerKeyPolicy.Judge(k).Allowed));
}
