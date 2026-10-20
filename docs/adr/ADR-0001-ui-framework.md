# ADR-0001: UI framework for the overlay and tray application

- **Status:** Proposed
- **Date:** 2026-10-19
- **Related requirements:** PLAT-001, UX-001, UX-005, UX-006, PERF-001

## Context
The app needs: cross-application OLE drag-and-drop target, transparent/topmost/non-activating overlay windows positioned at the cursor, per-monitor DPI, global input (hooks or hotkeys), tray icon, raw Windows messages, small idle footprint and fast cold start. C#/.NET is preferred. The brief names WPF + Win32 interop as the strong candidate and asks for a comparison with WinUI 3, direct Win32, Windows App SDK and hybrids; Electron is out unless overwhelming evidence appears.

## Decision
Not yet decided. Questions to answer:
1. Can each candidate host a layered, topmost, non-activating window that receives OLE drops while the drag originated in Explorer?
2. Measured cold start, idle memory and trigger-to-first-frame for a minimal overlay in each candidate.
3. Per-monitor-V2 DPI behaviour and mixed-DPI placement.
4. Packaged vs unpackaged constraints (WinUI 3 has historically had limitations with transparent/topmost windows and unpackaged scenarios; verify against current Windows App SDK docs, do not assume).
5. Maintenance risk and tooling maturity on the installed toolchain (.NET SDK 10.0.402 pinned since 2026-10-19).

## Alternatives considered
- **WPF + Win32 interop**: mature, full `HwndSource` control, known DPI story; heavier base memory than raw Win32.
- **WinUI 3 / Windows App SDK**: modern visuals; historically weaker for transparent overlays, non-activating windows and unpackaged apps. Needs evidence.
- **Direct Win32 (C# P/Invoke or C++) for the overlay + .NET for the rest**: lightest and most controllable; more code to own, custom rendering (Direct2D/Composition).
- **Hybrid**: Win32 overlay window with WPF/Composition content; evaluate only if a pure option fails a hard requirement.
- **Electron**: rejected by the brief on size, memory and native integration grounds.

## Consequences
To be written with the decision.

## Evidence / experiments
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
