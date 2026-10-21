using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WinTools.Host;

/// <summary>
/// Local diagnostic log, one JSON object per line: milliseconds since start, wall clock, event name, data. It records
/// timings, counts, operation ids, error codes and output file names; never file contents, and never full source paths.
/// Default location: %LOCALAPPDATA%\WinTools\logs\host-yyyyMMdd.jsonl. Thread-safe.
/// </summary>
internal sealed class HostLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private readonly long _start = Stopwatch.GetTimestamp();

    public string Path { get; }

    private HostLog(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static HostLog Open(string? path) => new(path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTools", "logs",
        $"host-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jsonl"));

    public void Event(string name, object? data = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            t = Math.Round(Stopwatch.GetElapsedTime(_start).TotalMilliseconds, 3),
            wall = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            ev = name,
            data,
        });
        lock (_gate) _writer.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
