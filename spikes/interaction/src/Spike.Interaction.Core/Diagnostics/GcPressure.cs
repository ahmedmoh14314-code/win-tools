using System.Diagnostics;
using Spike.Interaction.Core.Telemetry;

namespace Spike.Interaction.Core.Diagnostics;

/// <summary>
/// Spike 002B, experiment 1: models "the rest of the application" producing managed garbage on another thread
/// while the dedicated hook thread stays lightweight. Never touches the hook thread.
///   churn   : ~150 MB/s of 64 KB arrays kept alive in a 128 MB ring (objects survive into gen1/gen2).
///   extreme : churn + a 3-million-object graph with old-to-young reference churn + a forced blocking,
///             compacting gen2 collection every 2 s (worst case for runtime suspension).
/// </summary>
public sealed class GcPressure : IDisposable
{
    private readonly string _mode;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _allocatedMb, _induced;
    private double _maxInducedMs;

    public static GcPressure? Start(string mode, SpikeLog log)
    {
        if (string.IsNullOrEmpty(mode) || mode == "none") return null;
        if (mode is not ("churn" or "extreme")) throw new ArgumentException($"Unknown --gc-pressure mode '{mode}'");
        log.Event("gcPressure.start", new { mode });
        return new GcPressure(mode);
    }

    private GcPressure(string mode)
    {
        _mode = mode;
        _thread = new Thread(Run) { IsBackground = true, Name = "GcPressure", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Run()
    {
        var ring = new byte[2048][];
        var i = 0;
        List<object>? graph = null;
        if (_mode == "extreme")
        {
            graph = new List<object>(3_000_000);
            for (var k = 0; k < 3_000_000; k++) graph.Add(new int[4]);
        }
        var clock = Stopwatch.StartNew();
        long lastInduced = 0;
        while (!_stop)
        {
            for (var b = 0; b < 16; b++) { ring[i] = new byte[64 * 1024]; i = (i + 1) % ring.Length; }
            _allocatedMb++;
            if (graph is not null)
            {
                for (var k = 0; k < 2000; k++) graph[Random.Shared.Next(graph.Count)] = new int[4];
                if (clock.ElapsedMilliseconds - lastInduced >= 2000)
                {
                    lastInduced = clock.ElapsedMilliseconds;
                    var t0 = Stopwatch.GetTimestamp();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    _induced++;
                    if (ms > _maxInducedMs) _maxInducedMs = ms;
                }
            }
            Thread.Sleep(5);
        }
    }

    public object Summary() => new
    {
        mode = _mode,
        allocatedMB = Interlocked.Read(ref _allocatedMb),
        inducedFullGcs = Interlocked.Read(ref _induced),
        maxInducedFullGcMs = Math.Round(_maxInducedMs, 1),
        totalPauseMs = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
        gen0 = GC.CollectionCount(0),
        gen1 = GC.CollectionCount(1),
        gen2 = GC.CollectionCount(2),
    };

    public void Dispose() => _stop = true;
}
