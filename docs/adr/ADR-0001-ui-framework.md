# ADR-0001: UI framework for the overlay and tray application

- **Status:** Proposed — WPF decision drafted 2026-10-20 but NOT accepted: evaluator blocker B1 (unexplained 5.0 s WPF render stalls during which drops fall through; 1/300 missed DragEnter without load). See spike-002 §Evaluator.
- **Date:** 2026-10-19
- **Related requirements:** PLAT-001, UX-001, UX-005, UX-006, PERF-001

## Context
The app needs: cross-application OLE drag-and-drop target, transparent/topmost/non-activating overlay windows positioned at the cursor, per-monitor DPI, global input (hooks or hotkeys), tray icon, raw Windows messages, small idle footprint and fast cold start. C#/.NET is preferred. The brief names WPF + Win32 interop as the strong candidate and asks for a comparison with WinUI 3, direct Win32, Windows App SDK and hybrids; Electron is out unless overwhelming evidence appears.

## Decision
**WPF (.NET 10) for the resident application and the radial overlay, with targeted Win32 interop.** One process, one UI stack. Interop is limited to: the overlay's extended styles (`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, topmost), placement in physical pixels from the cursor's monitor, the low-level hooks on their own thread (ADR-0003), tray/single-instance/startup plumbing. WPF owns the OLE drop target, layout, text, animation, DPI and UI Automation.

Binding rules that come with the decision (Spike 002A):
1. Every pixel inside the ring, including the dead zone, has alpha ≥ 1 (alpha 0 is click-through for OLE: DragEnter only after a move, and a release in the dead zone drops into the window underneath, e.g. Explorer). Pixels outside the ring stay alpha 0.
2. One pre-created overlay window, shown/hidden, never activated.
3. The resident process stays allocation-light: engines, image decoding and thumbnails run in the worker (ADR-0005). Under heavy managed GC pressure the WPF dispatcher stalled for seconds and activations were lost; under normal load it is fine.
4. Measure the product's resident private bytes and working set against the budget in CI-style checks; WPF sits at ≈ 95 MB private / 145 MB working set after 300 cycles, inside but close to the targets (100 / 150 MB).

Rejected: WinUI 3 for the overlay (no public transparency API, open overlay bugs; Phase 1B). Rejected for V1: Win32 overlay + WPF shell (two UI stacks; the WPF shell in the same process removes most of the memory advantage, and a separate UI process adds IPC without a requirement that needs it). Kept as the fallback if a future measurement breaks the 120 MB ceiling or WPF's dispatcher latency becomes a measured problem: a Win32/Direct2D overlay host (Spike 001 code shows the interop surface).

## Alternatives considered
- **WPF + Win32 interop**: mature, full `HwndSource` control, known DPI story; heavier base memory than raw Win32.
- **WinUI 3 / Windows App SDK**: modern visuals; historically weaker for transparent overlays, non-activating windows and unpackaged apps. Needs evidence.
- **Direct Win32 (C# P/Invoke or C++) for the overlay + .NET for the rest**: lightest and most controllable; more code to own, custom rendering (Direct2D/Composition).
- **Hybrid**: Win32 overlay window with WPF/Composition content; evaluate only if a pure option fails a hard requirement.
- **Electron**: rejected by the brief on size, memory and native integration grounds.

## Consequences
- Positive: rich rendering, animation, accessibility and DPI handling without our own rendering stack; least interop code; one stack to debug and maintain.
- Negative: ≈ 7× the Win32 host's private bytes (95 vs 9–14 MB) and ≈ 0.4–0.6 s startup (Release, no ReadyToRun); dispatcher latency is sensitive to GC pressure in the resident process.
- Follow-ups: ReadyToRun/trimming measurement against the 500 ms cold-start budget; overlay animations must not move hit regions during a drag; budget check on every checkpoint.

## Evidence / experiments
**Spike 002 (2026-10-20):** `docs/research/2026-10-20-spike-002.md` (dead-zone hit-testing, Win32 vs WPF on identical runs, hooks under GC pressure, Raw Input during Explorer drags, 1500-cycle memory plateau).

Spike 001 (`docs/research/2026-10-19-interaction-spike.md`, §R-E) compared a raw Win32 overlay host and a WPF overlay host sharing the same hooks/session code, Release builds, identical automated scenarios on the same machine (2026-10-20):

| | Win32 (C# P/Invoke, GDI) | WPF |
|---|---|---|
| Question 1 (topmost, non-activating window receiving Explorer-originated OLE drops) | yes: 100/100 self mode, 10/10 Explorer-sourced | yes: 100/100 self mode, 10/10 Explorer-sourced (WPF registers the drop target itself) |
| hook → first paint, warm median / max (100 cycles) | 11.1 / 28.8 ms | 7.2 / 35.9 ms |
| idle working set after 100 cycles | 45.7 MB | 137.3 MB |
| idle private bytes | 14.3 MB | 88.6 MB (exceeds the < 40 MB budget hypothesis in `docs/performance/budgets.md`) |
| threads | 10–14 | 28–34 |
| startup CPU, first 2 s (Release) | ≈1.5 % | ≈5 % |
| first-show cost | 25–60 ms | 22–36 ms |
| interop code owned | window class, painting, hit-testing, DPI, `IDropTarget` by hand | ex-style tweak + pixel positioning only |

Both meet the latency budget with margin. The deciding factors are memory/footprint (≈3× working set, ≈6× private) and ownership of rendering code (WPF gives layout, text, animation and accessibility for free; Win32 means Direct2D/Composition work for anything beyond flat pies). Not measured: WinUI 3 / Windows App SDK (documentation survey still pending), mixed DPI, cold start of a trimmed/ReadyToRun build. Debug-build numbers (31→41 MB vs 131 MB) agree with the Release numbers, so the earlier observation was not a Debug artefact.

## Evaluator review
Pending.
