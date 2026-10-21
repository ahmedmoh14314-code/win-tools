# First product slice: measurements (2026-10-21)

Machine: the development PC (Windows 11 Pro 26200, 120 DPI primary monitor, 1920×1080), .NET 10.0.13. Host and worker are Release builds, framework-dependent, without ReadyToRun. All input was injected by `tests/WinTools.EndToEnd` into a real Explorer window. The raw logs (per-cycle harness records and host JSONL) are kept locally, not in the repository.

| Budget (`budgets.md`) | Target | Measured | Verdict |
|---|---|---|---|
| Idle CPU, 60 s | ≤ 0.1 % of one core | **0.000 %** average and maximum; 0 of 30 two-second samples above zero | ✔ |
| Resident private bytes | ≤ 100 MB (ceiling 120) | **7.0 MB** idle after start; **16.0 MB** after 100 conversions; 11.6–13.0 MB after shorter runs | ✔ |
| Resident working set | ≤ 150 MB | **30.2 MB** idle; **50.9 MB** after 100 conversions | ✔ |
| Process start → hooks installed and log open | < 500 ms (tray-ready; no tray yet) | **179 ms** to the first log line, measured from outside the process | ✔ (proxy) |
| Trigger → overlay shown and painted (synchronous `UpdateWindow`) | ≤ 50 ms p95 warm, ≤ 120 ms first | warm p50 **3.0–9.2 ms**, p95 **4.0–13.9 ms**; first activation **27–38 ms** | ✔ |
| Trigger → DragEnter on the overlay | (informative) | p50 4.5–18.4 ms, p95 6.3–37.5 ms | — |
| Drop → output validated and renamed (worker started cold every batch) | < 300 ms p95 to job start | whole job **p50 131–177 ms, max 215 ms** for 64×48 images, worker start included | ✔ |
| Overlay show/hide leak | 0 growth over 1000 cycles | not run; private 11.6 → 16.0 MB over 100 conversions, without a forced GC | open |

Threads while idle: 8. Handles: 248. No worker process exists while the host is idle: the worker is started per drop and exits with it.

Not measured: large images (conversion time scales with pixels; the only limits are 100 MP and 256 MB), cold start after reboot, mixed DPI, a 1000-cycle leak run with a forced GC (the Spike 002 method).
