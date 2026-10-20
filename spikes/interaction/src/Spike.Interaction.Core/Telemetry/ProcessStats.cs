using System.Diagnostics;
using Spike.Interaction.Core.Native;

namespace Spike.Interaction.Core.Telemetry;

/// <summary>Snapshot of resource usage used for idle-cost and leak checks.</summary>
public sealed record ProcessSnapshot(
    double CpuMs,
    long WorkingSetBytes,
    long PrivateBytes,
    int Handles,
    uint GdiObjects,
    uint UserObjects,
    int Threads,
    long Timestamp,
    int Gen0, int Gen1, int Gen2, long GcHeapBytes, double GcPauseMs)
{
    public static ProcessSnapshot Take()
    {
        using var p = Process.GetCurrentProcess();
        var h = Kernel32.GetCurrentProcess();
        return new ProcessSnapshot(
            p.TotalProcessorTime.TotalMilliseconds,
            p.WorkingSet64,
            p.PrivateMemorySize64,
            p.HandleCount,
            User32.GetGuiResources(h, GR.GDIOBJECTS),
            User32.GetGuiResources(h, GR.USEROBJECTS),
            p.Threads.Count,
            Stopwatch.GetTimestamp(),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalMemory(false), GC.GetTotalPauseDuration().TotalMilliseconds);
    }

    public object ToLogData() => new
    {
        cpuMs = Math.Round(CpuMs, 1),
        workingSetMB = Math.Round(WorkingSetBytes / 1048576.0, 2),
        privateMB = Math.Round(PrivateBytes / 1048576.0, 2),
        handles = Handles,
        gdi = GdiObjects,
        user = UserObjects,
        threads = Threads,
        gen0 = Gen0, gen1 = Gen1, gen2 = Gen2, gcHeapMB = Math.Round(GcHeapBytes / 1048576.0, 2), gcPauseMs = Math.Round(GcPauseMs, 1),
    };

    /// <summary>CPU utilisation (% of one core) between two snapshots.</summary>
    public static double CpuPercentBetween(ProcessSnapshot a, ProcessSnapshot b)
    {
        var wallMs = SpikeLog.MsBetween(a.Timestamp, b.Timestamp);
        return wallMs <= 0 ? 0 : (b.CpuMs - a.CpuMs) / wallMs * 100.0;
    }
}
