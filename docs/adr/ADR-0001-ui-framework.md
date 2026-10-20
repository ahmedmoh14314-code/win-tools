# ADR-0001: UI technology for the radial drag/drop overlay

- **Status:** Accepted (2026-10-21, owner decision on the Spike 002/002C evidence): native Win32 radial/drop overlay. WPF is not used for the overlay.
- **Date:** 2026-10-19
- **Related requirements:** PLAT-001, UX-001, UX-005, UX-006, PERF-001

## Context
The app needs: cross-application OLE drag-and-drop target, transparent/topmost/non-activating overlay windows positioned at the cursor, per-monitor DPI, global input (hooks or hotkeys), tray icon, raw Windows messages, small idle footprint and fast cold start. C#/.NET is preferred. The brief names WPF + Win32 interop as the strong candidate and asks for a comparison with WinUI 3, direct Win32, Windows App SDK and hybrids; Electron is out unless overwhelming evidence appears.

## Decision
**V1 uses a native Win32 window (C# P/Invoke) for the latency-critical radial/drop overlay**, hosted by a lightweight .NET resident process. The overlay is a pre-created, layered, topmost, non-activating window (`WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`) with its own `IDropTarget`, placed in physical pixels on the cursor's monitor, and shown/hidden, never activated.

**WPF is not selected for this overlay.** The finding is narrow: WPF is not the lowest-risk technology for this particular global drag/drop overlay in a resident process. It is not a claim that WPF is generally unreliable. WPF remains acceptable for ordinary application UI (settings/preferences) later; that UI is deferred and is not in the resident interaction path of the first slice.

Why:
1. The Win32 host passed every interaction test: Spike 001 drop and cancel matrix, 1000/1000 long run, release outside the ring, and fast release with no loss once the overlay existed.
2. It stayed deterministic under the tested pressure: hook→UI max 468 ms under forced blocking GC, no stall ≥ 1 s.
3. It gives direct control of the window, hit-testing and OLE target lifetime.
4. It is substantially lighter: about 9–14 MB private and 41–45 MB working set, against about 86–95 MB and 133–145 MB for WPF, and it starts in 0.16–0.21 s against 0.42–0.59 s.
5. The WPF overlay showed an externally triggered UI Automation / message-thread stall of about 5 s on appearance under heavy managed pressure, during which drops fell through to the window underneath (Spike 002C: 35 stalls in 620 loaded cycles).
6. The only demonstrated WPF workaround removed UI Automation exposure (0 stalls, 200/200 drops), which is not acceptable for a product.
7. Further WPF investigation would not buy enough product value to justify delaying implementation.

Rules carried into the product (Spike 001/002):
- The interaction state machine is framework-independent (Core). After `DragEnter`, a button release does not hide the target; the overlay waits for the terminal OLE `Drop` or `DragLeave`. A long watchdog only guards a dead source.
- No conversion or other heavy work runs on the hook or overlay thread.
- Every pixel inside the ring, including the dead zone, has alpha ≥ 1. Alpha 0 is click-through for OLE.

## Alternatives considered
- **WPF for the overlay (and the app shell):** the richest UI stack and the least interop. It was rejected for the overlay on the Spike 002C evidence above. It remains a candidate for future ordinary windows.
- **Win32 overlay + WPF shell in the same process:** this is the long-term shape if settings UI is ever needed. It is not built in the first slice, which has no settings UI.
- **WinUI 3 / Windows App SDK:** rejected for the overlay (no public transparency API, open overlay bugs; Phase 1B research).
- **Electron:** rejected by the brief on size, memory and native integration.

## Consequences
- Positive: deterministic overlay behaviour under the tested load, small resident footprint, and direct OLE and window control. No UI framework is in the resident path.
- Negative: we own rendering (GDI/layered bitmap now; Direct2D/Composition if richer visuals are needed), hit-testing, DPI handling and the `IDropTarget` implementation.
- Accessibility: the overlay must expose an accessible name and role through its own provider (MSAA/UIA) before V1 ships. That provider must not block the overlay thread. This is not part of the first slice and is recorded as a gap.
- Follow-ups: cold-start and idle measurement of the product host; mixed DPI and multi-monitor; Explorer fast-release acceptance test in the product.

## Evidence / experiments
**Spike 002 and 002C (2026-10-20/21):** `docs/research/2026-10-20-spike-002.md`. Covers dead-zone hit-testing, Win32 vs WPF on identical runs, hooks under GC pressure, the Win32 1500-cycle memory plateau, the WPF 5 s UI Automation stall with the watchdog control and the UIA A/B, and fast release on both hosts.

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
- Spike 002 evaluator (2026-10-20): WPF draft not acceptable (B1: 5 s stalls with drop fall-through).
- 002C (2026-10-21): B1 was shown to be a UI Automation / UI-thread stall that does not depend on the watchdog. Its only demonstrated workaround is unacceptable. The owner accepted the native Win32 overlay on this evidence.
- The first product slice's evaluator re-checks the decision against the product code.
