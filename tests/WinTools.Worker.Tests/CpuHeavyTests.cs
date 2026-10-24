namespace WinTools.Worker.Tests;

/// <summary>
/// Tests that run the compression search, which uses every core (Fidelity's parallel bands). Run on their own, after the
/// others: in parallel with them they starved the thread pool on a small CI runner, and a test that waits on a pool
/// callback (the settings signal) timed out (CI, PR #12).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CpuHeavyTests
{
    public const string Name = "CPU-heavy compression";
}
