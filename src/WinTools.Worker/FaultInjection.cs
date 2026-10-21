using System.Diagnostics;

namespace WinTools.Worker;

/// <summary>
/// Debug-build-only fault injection for crash, hang and memory tests: <c>WINTOOLS_WORKER_FAULT=crash-after-temp</c>,
/// <c>hang-after-temp</c> or <c>alloc-after-read</c> (commits up to 4 GB of native memory, which the host's job object
/// must refuse), optionally only for sources whose path contains <c>WINTOOLS_WORKER_FAULT_MATCH</c>.
/// Compiled out of Release builds.
/// </summary>
internal static class FaultInjection
{
    [Conditional("DEBUG")]
    public static void At(string point, string sourcePath)
    {
        var match = Environment.GetEnvironmentVariable("WINTOOLS_WORKER_FAULT_MATCH");
        if (!string.IsNullOrEmpty(match) && !sourcePath.Contains(match, StringComparison.OrdinalIgnoreCase)) return;
        var fault = Environment.GetEnvironmentVariable("WINTOOLS_WORKER_FAULT");
        if (fault == "crash-" + point) Environment.FailFast("injected worker crash");
        if (fault == "hang-" + point) Thread.Sleep(Timeout.Infinite);
        if (fault == "alloc-" + point) CommitNative(4L << 30);
    }

    /// <summary>Native memory is outside the GC heap limit, so only the job object can stop this; frees it all if nothing did.</summary>
    private static unsafe void CommitNative(long total)
    {
        var blocks = new List<nint>();
        try
        {
            for (long done = 0; done < total; done += 64 << 20) blocks.Add((nint)System.Runtime.InteropServices.NativeMemory.Alloc(64 << 20));
        }
        finally
        {
            foreach (var b in blocks) System.Runtime.InteropServices.NativeMemory.Free((void*)b);
        }
    }
}
