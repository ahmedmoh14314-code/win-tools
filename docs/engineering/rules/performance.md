# Performance rules

- **Budgets are defined before implementation** in `docs/performance/budgets.md` and measured, not guessed. The numbers there are hypotheses until a benchmark file backs them.
- **Idle means idle**: no filesystem scanning, no polling timers, no preloaded engines. Global input hooks do minimal work on the hook thread and never block.
- **On-demand loading**: engines, large assemblies and worker processes start only when a job needs them. Measure first-job latency separately from warm latency.
- **Radial latency is the headline metric**: time from trigger to first painted frame, measured with high-resolution timestamps and `EventSource`/ETW traces, not by eye.
- **Instrument**: `EventSource` events for activation, overlay show, job start/finish; BenchmarkDotNet for pure logic; `dotnet-counters` for idle CPU and memory.
- **Report regressions** against the last checkpoint numbers in `docs/performance/`.
- Allocation-light code (`Span<T>`, pooled buffers) only where profiling shows it matters.
