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
    public void A_schema_1_file_is_read_flat_even_with_a_field_named_like_a_section()
    {
        // The layout follows the schema number: a hand-edited schema 1 file must not lose its values (evaluator, PR #19).
        var s = AppSettings.FromJson("""{ "schema": 1, "triggerKey": "F9", "jpegQuality": 77, "output": { "x": 1 } }""")!;
        Assert.Equal(("F9", 77), (s.TriggerKey, s.JpegQuality));
    }

    [Fact]
    public void A_newer_schema_number_is_kept_when_written()
    {
        using var doc = JsonDocument.Parse((new AppSettings() with { Schema = 3 }).ToJson());
        Assert.Equal(3, doc.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void Reset_gives_the_defaults()
    {
        Assert.Equal(new AppSettings(), AppSettings.Defaults);
        Assert.Equal("Space", AppSettings.Defaults.TriggerKey);
    }

    // The typing-interference spike (docs/research/2026-10-25-typing-interference.md, ADR-0021). Explorer ignored F2-F12,
    // letters and digits during a drag, but a key held past the drop repeats into Explorer, and with Ctrl, Shift or Alt
    // it is another shortcut (Alt+F4, Shift+F10); neither was measured. Offered: only keys that do nothing in Explorer if
    // they reach it (Space only selects, F7, F9 and F12 do nothing there). F2-F4, F6, F10 and F11 rename, search, focus,
    // open the menu or go full screen; F5 refreshes; F1 opens Help; F8 deletes in Total Commander and Far, which a held
    // key reaches too (second review); letters and digits would open the ring over games and drawing programs.
    [Theory]
    [InlineData("Space", true)]
    [InlineData("F7", true)]
    [InlineData("F8", false)]
    [InlineData("F9", true)]
    [InlineData("F12", true)]
    [InlineData("F1", false)]
    [InlineData("F2", false)]
    [InlineData("F3", false)]
    [InlineData("F4", false)]
    [InlineData("F5", false)]
    [InlineData("F6", false)]
    [InlineData("F10", false)]
    [InlineData("F11", false)]
    [InlineData("A", false)]
    [InlineData("Q", false)]
    [InlineData("1", false)]
    [InlineData("Ctrl", false)]
    public void Every_key_offered_or_refused_has_a_reason(string key, bool allowed)
    {
        var verdict = TriggerKeyPolicy.Judge(key);
        Assert.Equal(allowed, verdict.Allowed);
        if (!allowed) Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        if (!allowed) Assert.NotEqual(TriggerKeyPolicy.Judge("NoSuchKey").Reason, verdict.Reason); // each refusal has its own reason, never the fallback
        Assert.Equal(allowed, AppSettings.TriggerKeys.ContainsKey(key));
    }

    [Fact]
    public void The_window_lists_the_keys_in_a_fixed_order() =>
        Assert.Equal(["Space", "F7", "F9", "F12"], TriggerKeyPolicy.Order);

    [Fact]
    public void Only_allowed_keys_are_trigger_keys() =>
        Assert.All(AppSettings.TriggerKeys.Keys, k => Assert.True(TriggerKeyPolicy.Judge(k).Allowed));
}
