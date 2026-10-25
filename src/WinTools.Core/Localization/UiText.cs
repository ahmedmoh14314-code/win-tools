using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace WinTools.Core.Localization;

/// <summary>
/// The product's user-facing text in English and Arabic, from the embedded tables <c>Strings.en.json</c> and
/// <c>Strings.ar.json</c>. Every surface asks here instead of holding English literals. A key missing in Arabic falls back
/// to English, and a test keeps the two tables complete. Arabic is written right to left (<see cref="IsRightToLeft"/>).
/// Format names (PDF, PNG, ZIP, MP4) stay as they are in both languages.
/// </summary>
public static class UiText
{
    public const string English = "en", Arabic = "ar";

    private static readonly FrozenDictionary<string, FrozenDictionary<string, string>> Tables = new Dictionary<string, FrozenDictionary<string, string>>
    {
        [English] = Load(English),
        [Arabic] = Load(Arabic),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static volatile string _language = English;

    /// <summary>The language in use: "en" or "ar".</summary>
    public static string Language => _language;

    public static bool IsRightToLeft => _language == Arabic;

    /// <summary>Uses a language: "en", "ar", or "System" (Arabic when Windows' display language is, otherwise English).</summary>
    public static void Use(string? setting, string? systemLanguageTag = null) => _language = Resolve(setting, systemLanguageTag);

    public static string Resolve(string? setting, string? systemLanguageTag) => setting switch
    {
        English or Arabic => setting,
        _ => systemLanguageTag is { } tag && tag.StartsWith(Arabic, StringComparison.OrdinalIgnoreCase) ? Arabic : English,
    };

    /// <summary>The text for a key in the language in use; English when Arabic lacks it; the key itself when neither has it.</summary>
    public static string T(string key) => Get(_language, key) ?? Get(English, key) ?? key;

    /// <summary>The text for a key, with {0}, {1}... filled in.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(key), args);

    /// <summary>The text for a key when there is one, otherwise <paramref name="fallback"/> (for labels a table may not cover yet).</summary>
    public static string TOr(string key, string fallback) => Get(_language, key) ?? Get(English, key) ?? fallback;

    public static string? Get(string language, string key) => Tables.TryGetValue(language, out var t) && t.TryGetValue(key, out var v) ? v : null;

    /// <summary>Every key of a table (for the completeness test).</summary>
    public static IReadOnlyCollection<string> Keys(string language) => Tables.TryGetValue(language, out var t) ? t.Keys : [];

    private static FrozenDictionary<string, string> Load(string language)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"WinTools.Core.Localization.Strings.{language}.json")
            ?? throw new InvalidOperationException($"The {language} strings are not embedded.");
        return (JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? []).ToFrozenDictionary(StringComparer.Ordinal);
    }
}
