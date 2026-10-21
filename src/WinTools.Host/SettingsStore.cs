using System.Globalization;
using WinTools.Core.Settings;

namespace WinTools.Host;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> at %APPDATA%\WinTools\settings.json (or a given path).
/// <list type="bullet">
/// <item>Save is atomic: the new file is written and flushed beside the old one, then swapped in with File.Replace (or
/// moved into place the first time), so a crash leaves either the old or the new file, never half of one.</item>
/// <item>A corrupt file is moved aside as settings.corrupt-yyyyMMddHHmmss.json and defaults are used.</item>
/// <item>A file from a newer schema (the "schema" field, any casing, number or numeric string) is never moved aside or
/// overwritten: the fields this version understands are used if they parse, otherwise defaults, and the store is
/// read-only for the session.</item>
/// <item>Save never throws: a path that cannot be written (a folder, a read-only or locked file, a missing drive) makes
/// it return false, and the host runs on with the settings in memory.</item>
/// </list>
/// </summary>
internal sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public static SettingsStore Default() => new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTools", "settings.json"));

    /// <summary>What happened on the last load, for the log.</summary>
    public string LastLoad { get; private set; } = "not loaded";

    public bool ReadOnly { get; private set; }

    public AppSettings Load()
    {
        ReadOnly = false;
        if (Directory.Exists(Path)) { LastLoad = "defaults (the settings path is a folder)"; ReadOnly = true; return new AppSettings(); }
        if (!File.Exists(Path)) { LastLoad = "defaults (no file)"; return new AppSettings(); }
        string text;
        try { text = File.ReadAllText(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastLoad = "defaults (unreadable)"; ReadOnly = true; return new AppSettings(); }
        var parsed = AppSettings.FromJson(text);
        if (RawSchema(text) > AppSettings.CurrentSchema)
        {
            // Written by a newer version: never ours to move or rewrite, even if this version cannot parse all of it.
            ReadOnly = true;
            LastLoad = parsed is null ? "defaults (newer schema not understood; file left untouched)" : "newer schema (read-only)";
            return parsed ?? new AppSettings();
        }
        if (parsed is null)
        {
            var aside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, $"settings.corrupt-{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.json");
            try { File.Move(Path, aside); LastLoad = "defaults (corrupt file moved aside)"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastLoad = "defaults (corrupt file kept)"; ReadOnly = true; }
            return new AppSettings();
        }
        LastLoad = parsed.OutputPolicy == OutputPolicy.SameFolder && RawAsksForFolder(text)
            ? "loaded (output folder invalid; outputs go next to the sources)"
            : "loaded";
        return parsed;
    }

    public bool Save(AppSettings settings)
    {
        if (ReadOnly) return false;
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        var temp = System.IO.Path.Combine(dir, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            if (Directory.Exists(Path)) return false;
            Directory.CreateDirectory(dir);
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs))
            {
                w.Write(settings.Normalized().ToJson());
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(Path)) File.Replace(temp, Path, destinationBackupFileName: null);
            else File.Move(temp, Path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The "schema" field as the file states it (any casing; number or numeric string); 1 when absent or unreadable.</summary>
    private static int RawSchema(string text)
    {
        if (RawProperty(text, "schema") is not { } v) return 1;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return 1;
    }

    private static bool RawAsksForFolder(string text) =>
        RawProperty(text, "outputPolicy") is { } v
        && (v.ValueKind == System.Text.Json.JsonValueKind.String ? string.Equals(v.GetString(), nameof(OutputPolicy.Folder), StringComparison.OrdinalIgnoreCase)
            : v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out var n) && n == (int)OutputPolicy.Folder);

    private static System.Text.Json.JsonElement? RawProperty(string text, string name)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value.Clone();
            return null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
