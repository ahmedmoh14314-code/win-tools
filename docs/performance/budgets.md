# Performance budgets (V1 engineering budgets, set 2026-10-20)

V1 budgets set by the product owner on 2026-10-20 (resident memory, idle CPU, overlay latency); the others remain hypotheses from the brief. These are engineering budgets, not guarantees. Memory alone never decides the UI framework: correct drag/drop, focus behaviour, reliability and maintainability rank higher as long as the implementation stays inside the envelope. Each becomes a requirement with evidence only once a benchmark file in this folder backs it. Adjust by ADR, not silently.

| Metric | Proposed target | How it will be measured |
|---|---|---|
| Idle CPU | ≤ 0.1 % of one logical core averaged over 60 s (no periodic work) | `dotnet-counters` / Task Manager sampling over 60 s with app idle in tray |
| Resident private bytes after warm-up | target ≤ 100 MB, hard ceiling ≤ 120 MB | Process private bytes, 3 samples after 2 min idle |
| Resident working set after warm-up | target ≤ 150 MB | Same samples |
| Cold start to tray-ready | < 500 ms | `EventSource` timestamp at process start vs tray-ready event |
| Trigger → usable overlay | ≤ 50 ms p95 warm, ≤ 120 ms first activation | High-res timestamp in hook callback vs first `Rendered` event; ETW trace |
| Drop → job started (worker spawned, engine loaded) | < 300 ms p95 | Job timeline events |
| UI thread stall during a job | 0 frames > 16 ms attributable to job work | Dispatcher/compositor instrumentation |
| Installer / portable size | < 30 MB without engines; engines accounted separately | Published artifact size |
| Overlay show/hide leak | 0 growth over 1000 cycles | Working set + GC heap before/after loop |

Budget decisions and the measurement harness are tracked by ADR-0010 (telemetry and diagnostics policy: local instrumentation only).
