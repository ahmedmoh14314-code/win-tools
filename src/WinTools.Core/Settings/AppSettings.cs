using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTools.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<OutputPolicy>))]
public enum OutputPolicy { SameFolder, Folder }

/// <summary>
/// The user's preferences, persisted locally as JSON (no secrets, nothing personal). Every field has a safe default,
/// and <see cref="Normalized"/> repairs out-of-range values instead of failing. Schema changes bump
/// <see cref="CurrentSchema"/>; a file written by a newer version is read for its known fields and never overwritten.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchema = 1;

    /// <summary>Keys allowed as the trigger: no modifiers (they change Explorer's drop effect) and nothing Explorer uses during a drag.</summary>
    public static readonly IReadOnlyDictionary<string, int> TriggerKeys = new Dictionary<string, int>
    {
        ["Space"] = 0x20, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
    };

    public int Schema { get; init; } = CurrentSchema;
    public string TriggerKey { get; init; } = "Space";
    public OutputPolicy OutputPolicy { get; init; } = OutputPolicy.SameFolder;
    public string? OutputFolder { get; init; }
    public int JpegQuality { get; init; } = 90;
    public int WebPQuality { get; init; } = 90;
    /// <summary>Default size for the "custom size" resize: fit inside Width×Height (0 = that side is free).</summary>
    public int ResizeWidth { get; init; } = 1600;
    public int ResizeHeight { get; init; } = 1600;
    public bool KeepAspectRatio { get; init; } = true;
    public bool NoUpscale { get; init; } = true;
    /// <summary>Placeholder for the UI phase: "System", "Light" or "Dark".</summary>
    public string Theme { get; init; } = "System";

    public int TriggerVirtualKey => TriggerKeys.TryGetValue(TriggerKey, out var vk) ? vk : 0x20;

    /// <summary>A copy with every field inside its valid range (unknown trigger → Space; bad folder → same folder).</summary>
    public AppSettings Normalized()
    {
        var folderOk = OutputPolicy == OutputPolicy.Folder && OutputFolder is { Length: > 0 } f
            && Files.PathPolicy.IsSupported(Path.Combine(f, "x.png")) && Path.IsPathFullyQualified(f);
        return this with
        {
            Schema = Math.Max(Schema, CurrentSchema),
            TriggerKey = TriggerKeys.ContainsKey(TriggerKey ?? "") ? TriggerKey! : "Space",
            OutputPolicy = folderOk ? OutputPolicy.Folder : OutputPolicy.SameFolder,
            OutputFolder = folderOk ? Path.TrimEndingDirectorySeparator(OutputFolder!) : null,
            JpegQuality = Math.Clamp(JpegQuality, 1, 100),
            WebPQuality = Math.Clamp(WebPQuality, 1, 100),
            ResizeWidth = Math.Clamp(ResizeWidth, 0, 65_535),
            ResizeHeight = Math.Clamp(ResizeHeight, 0, 65_535),
            Theme = Theme is "System" or "Light" or "Dark" ? Theme : "System",
        };
    }

    /// <summary>The resize operation id for the custom size, or null when no side is set.</summary>
    public string? CustomResizeId()
    {
        var s = Normalized();
        var up = s.NoUpscale ? "" : "-upscale";
        return (s.ResizeWidth, s.ResizeHeight) switch
        {
            (0, 0) => null,
            (> 0, 0) => $"resize.width-{s.ResizeWidth}{up}",
            (0, > 0) => $"resize.height-{s.ResizeHeight}{up}",
            _ => $"resize.box-{s.ResizeWidth}x{s.ResizeHeight}{(s.KeepAspectRatio ? "" : "-stretch")}{up}",
        };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses settings; null when the text is not valid settings JSON (the caller then keeps defaults).</summary>
    public static AppSettings? FromJson(string text)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(text, Json)?.Normalized(); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
