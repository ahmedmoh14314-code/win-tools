# ADR-0004: Radial overlay window implementation

- **Status:** Proposed
- **Date:** 2026-10-19
- **Related requirements:** UX-001, UX-005, UX-006, PERF-001

## Context
The overlay must appear at the cursor during a drag, accept the drop (`RegisterDragDrop` / `IDropTarget`), not steal activation from the drag source, render on top of everything (except elevated/UIPI-protected windows), be per-monitor DPI aware, clamp to the monitor work area near edges and corners, support nested wheels with hit testing, and show/hide thousands of times without leaking. Window styles (`WS_EX_LAYERED`, `WS_EX_TOPMOST`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`), click-through regions, and the choice of rendering stack (WPF visuals, Composition, Direct2D) are the main variables.

## Decision
Not yet decided. Depends on ADR-0001 and the spike for ADR-0003.

## Alternatives considered
- One pre-created hidden window moved and shown on demand (fast; must handle monitor/DPI change events).
- Window created on each activation (simpler lifetime; cold latency risk).
- Per-monitor window pool (complex; only if DPI change handling proves unreliable).

## Consequences
To be written with the decision.

## Evidence / experiments
Spike 001 (`spikes/interaction/`, `docs/research/2026-10-19-interaction-spike.md`), measured 2026-10-19/20, single 1920×1080 monitor at 125 %.
- Pre-created hidden window moved and shown on demand (both hosts): hook → shown median 6–10 ms, hook → first paint median 7–11 ms, max ≈30 ms warm, 25–60 ms on the first show. No focus change in ≈400 shows.
- Win32 host (popup, `SetWindowRgn` circle, GDI double buffer, `RegisterDragDrop`): 100/100 Explorer-style drops in 100 cycles; GDI 4 / USER ≈13 flat; working set 42 → 45 MB over 95 cycles, private 14 MB (Release).
- WPF host (transparent topmost `Window`, `AllowDrop`): 100/100 drops; GDI 8 / USER ≈21 flat; working set 134 → 137 MB, private 88 MB (Release). DragEnter arrives only once the cursor is over a painted pixel: the transparent dead zone is click-through for OLE hit-testing. Decide explicitly whether the dead zone is opaque.
- Edges/corners: both hosts clamp fully inside the work area and receive the drop at all 8 test points; at the 4 corners the cursor starts outside the ring (ring centre is 137 px away, radius 137) so a sector is live only after the user moves. The placement rule must keep the cursor inside the ring (shift the ring and map sectors relative to the ring, or shrink near edges).
- Hide timing: hiding on the hook's button-up loses the drop (OLE sends DragLeave when the window under the cursor changes; negative control 10/10 lost). After a release with the drag inside, keep the window visible until Drop/DragLeave with no short timer (long watchdog only); use a ≈300 ms grace only when no drag is inside; ignore Esc while a drop is pending. Also: a release outside the ring must not leave a 300 ms-visible ring that feels wrong (human check owed).
- "First paint" numbers are host-specific proxies (WM_PAINT done vs first WPF render tick), not DWM presentation; the budget's ETW measurement is owed by the product.
- Not available on this machine: multi-monitor, mixed DPI, 100/150/200 % scaling. Still pending.

## Evaluator review
Pending.
