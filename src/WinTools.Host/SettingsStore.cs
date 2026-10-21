using System.Globalization;
using WinTools.Core.Settings;

namespace WinTools.Host;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> at %APPDATA%\WinTools\settings.json (or a given path).
/// <list type="bullet">
/// <item>Save is atomic: the new file is written and flushed beside the old one, then swapped in with File.Replace (or
/// moved into place the first time), so a crash leaves either the old or the new file, never half of one.</item>
/// <item>A corrupt file is moved aside as settings.corrupt-yyyyMMddHHmmss.json and defaults are used.</item>
/// <item>A file from a newer schema is read for the fields this version knows and is never overwritten.</item>
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
        if (!File.Exists(Path)) { LastLoad = "defaults (no file)"; return new AppSettings(); }
        string text;
        try { text = File.ReadAllText(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastLoad = "defaults (unreadable)"; ReadOnly = true; return new AppSettings(); }
        var parsed = AppSettings.FromJson(text);
        if (parsed is null)
        {
            var aside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, $"settings.corrupt-{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.json");
            try { File.Move(Path, aside); LastLoad = "defaults (corrupt file moved aside)"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastLoad = "defaults (corrupt file kept)"; ReadOnly = true; }
            return new AppSettings();
        }
        if (RawSchema(text) > AppSettings.CurrentSchema) { ReadOnly = true; LastLoad = "newer schema (read-only)"; return parsed; }
        LastLoad = "loaded";
        return parsed;
    }

    public bool Save(AppSettings settings)
    {
        if (ReadOnly) return false;
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        var temp = System.IO.Path.Combine(dir, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
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
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int RawSchema(string text)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("schema", out var v) && v.TryGetInt32(out var n) ? n : 1;
        }
        catch (System.Text.Json.JsonException) { return 1; }
    }
}
