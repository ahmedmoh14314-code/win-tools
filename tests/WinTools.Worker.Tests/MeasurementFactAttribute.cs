namespace WinTools.Worker.Tests;

/// <summary>
/// A measurement, not a check: it prints numbers for the performance and calibration records and asserts nothing, so it
/// runs only when asked (WINTOOLS_MEASURE=1), never in CI or an ordinary test run. Some take minutes (48 MP images).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MeasurementFactAttribute : FactAttribute
{
    public MeasurementFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WINTOOLS_MEASURE") != "1") Skip = "Measurement: set WINTOOLS_MEASURE=1 to run.";
    }
}
