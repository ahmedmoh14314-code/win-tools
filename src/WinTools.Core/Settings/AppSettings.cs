using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTools.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<OutputPolicy>))]
public enum OutputPolicy { SameFolder, Folder }

/// <summary>
/// The user's preferences, persisted locally as JSON (no secrets, nothing personal). Every field has a safe default,
/// and <see cref="Normalized"/> repairs out-of-range values instead of failing. Schema changes bump
/// <see cref="CurrentSchema"/>; a file written by a newer version is read for its known fields and never overwritten.
/// <para>
/// Schema 2 (V2 PR 1.5) groups the fields in sections (<c>trigger</c>, <c>output</c>, <c>images</c>, <c>ui</c>), so a
/// module adds its own section without touching the others. In memory the record stays flat. A schema 1 file (flat
/// fields) is read as it is and saved as schema 2. An older WinTools reads a schema 2 file as newer: it shows its own
/// defaults and never overwrites it.
/// </para>
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchema = 2;

    /// <summary>The defaults, which Reset restores.</summary>
    public static AppSettings Defaults { get; } = new();

    /// <summary>Keys allowed as the trigger (<see cref="TriggerKeyPolicy"/>): keys whose stray press is harmless in Explorer.</summary>
    public static IReadOnlyDictionary<string, int> TriggerKeys => TriggerKeyPolicy.Allowed;

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
    /// <summary>"System", "Light" or "Dark". V1 ships the dark theme only; the field is kept for a later light theme.</summary>
    public string Theme { get; init; } = "System";
    /// <summary>The radial menu's navigation ticks and the success and failure sounds.</summary>
    public bool UiSounds { get; init; } = true;
    /// <summary>"System" (follow Windows' "Animation effects"), "On" or "Off".</summary>
    public string Animations { get; init; } = "System";

    /// <summary>Whether the UI should show changes at once, given Windows' own setting.</summary>
    public bool ReducedMotion(bool systemReducedMotion) => Normalized().Animations switch
    {
        "On" => false,
        "Off" => true,
        _ => systemReducedMotion,
    };

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
            Animations = Animations is "System" or "On" or "Off" ? Animations : "System",
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

    /// <summary>The settings as a schema 2 file.</summary>
    public string ToJson()
    {
        var s = Normalized();
        return JsonSerializer.Serialize(new FileV2(Math.Max(s.Schema, CurrentSchema), // a newer number is never lowered
            new(s.TriggerKey),
            new(s.OutputPolicy, s.OutputFolder),
            new(s.JpegQuality, s.WebPQuality, s.ResizeWidth, s.ResizeHeight, s.KeepAspectRatio, s.NoUpscale),
            new(s.Theme, s.UiSounds, s.Animations)), Json);
    }

    /// <summary>
    /// Parses settings of schema 1 (flat) or 2 (sections), and the sections a newer schema still has; null when the text
    /// is not valid settings JSON (the caller then keeps defaults). Missing values take their defaults.
    /// </summary>
    public static AppSettings? FromJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            // Sections are read only when there are some and the file does not say schema 1 (evaluator, PR #19: a schema 1
            // file with an object field named like a section lost every value). Anything else is read flat, as V1 did,
            // which also keeps the known fields of a newer flat file.
            if (SchemaOf(doc.RootElement) is <= 1 || !HasSections(doc.RootElement))
                return JsonSerializer.Deserialize<AppSettings>(text, Json)?.Normalized(); // flat
            if (JsonSerializer.Deserialize<FileV2>(text, Json) is not { } f) return null;
            var d = Defaults;
            return new AppSettings
            {
                Schema = Math.Max(f.Schema, CurrentSchema),
                TriggerKey = f.Trigger?.Key ?? d.TriggerKey,
                OutputPolicy = f.Output?.Policy ?? d.OutputPolicy,
                OutputFolder = f.Output?.Folder,
                JpegQuality = f.Images?.JpegQuality ?? d.JpegQuality,
                WebPQuality = f.Images?.WebPQuality ?? d.WebPQuality,
                ResizeWidth = f.Images?.ResizeWidth ?? d.ResizeWidth,
                ResizeHeight = f.Images?.ResizeHeight ?? d.ResizeHeight,
                KeepAspectRatio = f.Images?.KeepAspectRatio ?? d.KeepAspectRatio,
                NoUpscale = f.Images?.NoUpscale ?? d.NoUpscale,
                Theme = f.Ui?.Theme ?? d.Theme,
                UiSounds = f.Ui?.Sounds ?? d.UiSounds,
                Animations = f.Ui?.Animations ?? d.Animations,
            }.Normalized();
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The "schema" field (any casing; a number or a numeric string), or null when there is none.</summary>
    private static int? SchemaOf(JsonElement root)
    {
        foreach (var p in root.EnumerateObject())
        {
            if (!string.Equals(p.Name, "schema", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n)) return n;
            if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m)) return m;
            return null;
        }
        return null;
    }

    /// <summary>A file without a schema number: sections mean schema 2 or later (any section object at the top, any casing).</summary>
    private static bool HasSections(JsonElement root) =>
        root.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object && Sections.Contains(p.Name));

    private static readonly HashSet<string> Sections = new(["trigger", "output", "images", "ui"], StringComparer.OrdinalIgnoreCase);

    private sealed record FileV2(int Schema, TriggerSection? Trigger, OutputSection? Output, ImagesSection? Images, UiSection? Ui);
    private sealed record TriggerSection(string? Key);
    private sealed record OutputSection(OutputPolicy? Policy, string? Folder);
    private sealed record ImagesSection(int? JpegQuality, int? WebPQuality, int? ResizeWidth, int? ResizeHeight, bool? KeepAspectRatio, bool? NoUpscale);
    private sealed record UiSection(string? Theme, bool? Sounds, string? Animations);
}
