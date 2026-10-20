using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Spike.Interaction.Core.Telemetry;

/// <summary>
/// Minimal JSON-lines logger. Every line carries a monotonic millisecond timestamp (Stopwatch based)
/// for intra-process deltas plus a wall-clock stamp for correlating two processes. Thread-safe.
/// </summary>
public sealed class SpikeLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly DateTime _originWall = DateTime.UtcNow;
    private readonly bool _echo;

    public string Path { get; }

    public SpikeLog(string path, bool echoToConsole)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _echo = echoToConsole;
    }

    public double MsSince(long timestamp) => (timestamp - _origin) * 1000.0 / Stopwatch.Frequency;
    public static double MsBetween(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    public void Event(string name, object? data = null, long? timestamp = null)
    {
        var ts = timestamp ?? Stopwatch.GetTimestamp();
        var t = MsSince(ts);
        var wall = _originWall.AddMilliseconds(t).ToString("HH:mm:ss.fff");
        var line = JsonSerializer.Serialize(new { t = Math.Round(t, 3), wall, ev = name, data }, JsonOptions);
        lock (_gate)
        {
            _writer.WriteLine(line);
            if (_echo) Console.WriteLine(line);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
