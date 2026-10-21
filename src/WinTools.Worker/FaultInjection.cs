using System.Diagnostics;

namespace WinTools.Worker;

/// <summary>
/// Debug-build-only fault injection for crash and hang tests: <c>WINTOOLS_WORKER_FAULT=crash-after-temp</c> or
/// <c>hang-after-temp</c>, optionally only for sources whose path contains <c>WINTOOLS_WORKER_FAULT_MATCH</c>.
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
    }
}
