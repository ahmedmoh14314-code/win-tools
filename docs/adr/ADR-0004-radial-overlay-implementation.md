# ADR-0004: Radial overlay window implementation

- **Status:** Proposed (the overall overlay decision). The 2026-10-23 amendment on the drop target's data is decided and implemented.
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

## Amendment: the drop target's data (2026-10-23, V2 Stage 0, PR 0.2)
**Defects (code review plus fault-injection tests, V2 plan blocker 2B):**
- A CF_HDROP read that failed at DragEnter (busy source, format not rendered yet, unusable memory) left the drag dead. `_hasFiles` stayed false for the whole drag, so the effect was NONE everywhere and OLE never delivered a Drop. The ring said "No actions".
- A failed read at the drop passed no files, even when DragEnter had read them, so nothing ran.
- No log said which of these happened.
- **Security:** the paths were read with `DragQueryFileW`, which trusts the DROPFILES block. A block whose file-list offset points past its end, or whose list is unterminated, made the process read out of bounds. The fault-injection test crashed the test host with an access violation (0xC0000005 in `DragQueryFileW`). Any drag source could crash the resident host.

**Decision (`FileDropTarget`, `HDrop`, `DropFiles`):**
- **Retry, bounded and throttled.** After a failed read, the target keeps the drag's `IDataObject` (OLE allows this between DragEnter and DragLeave/Drop) and reads it again from DragOver. Nothing blocks or loops: every read happens inside a callback OLE makes anyway. The limits:
  - Reads are at least 100 ms apart, counted from the end of the previous read. DragEnter is throttled too.
  - At most 20 reads per drag, about 2 s. Re-entering the ring does not reset the count.
  - Each read is a synchronous call into the source on the UI thread. One read slower than 200 ms, or 500 ms of reads in total, ends the retries, so a slow source cannot hold the UI again and again.
  - While a read is pending, the ring keeps its waiting state, not "No actions", and the effect is NONE.
  - When the files arrive, the menu is built, starting from the pointer's current position. The arm delay counts from that moment, so commands are seen before a drop can run one.
  - When the retries run out, the ring says "No actions" once. A pending re-entry never shows "No actions": it shows the waiting ring.
- **The drag's files.** Files read once belong to the drag until the target is reset. Reset happens when the ring is shown or hidden, and when the button is released outside the ring, because a later DragEnter then needs a new press and is another drag.
  - Until then, re-entry uses the drag's files without reading again.
  - A failed read at the drop falls back to them.
  - If the drop's own read succeeds, its files are used, as before; the coordinator still re-resolves them.
- **COM lifetime.** Each call's data object arrives as a runtime-callable wrapper whose count went up by one for that call. The target gives exactly that one count back with `Marshal.ReleaseComObject`:
  - at the end of the call;
  - for an object kept for a retry, when the read resolves, or on DragLeave, Drop or reset;
  - for a `pUnkForRelease` wrapper, after `ReleaseStgMedium`.

  It never uses `FinalReleaseComObject`. Before this change the wrappers lived until a garbage collection, which in an idle resident host can be hours (evaluator finding). Holding the object between DragEnter and DragLeave/Drop is the documented pattern (AddRef in DragEnter, Release in DragLeave/Drop).
- **Bounded parsing.** DROPFILES is parsed in place under `GlobalLock`, within `GlobalSize`. The block is rejected as malformed when any of these hold:
  - the header is short;
  - the offset points into the header or past the end;
  - the list is unterminated;
  - a path is longer than 32,767 characters;
  - there are more than 10,000 paths;
  - the block is larger than 64 MB.

  ANSI blocks are decoded with `MultiByteToWideChar(CP_ACP)`. `DragQueryFileW` is no longer used.

  A list whose final empty path is missing is accepted when every path is terminated inside the block: some sources omit it, and `DragQueryFileW` used to read past the end to find it.
- **Read failures are data, not exceptions.** Any exception from the source is caught: an RCW maps a failed HRESULT to `COMException`, `ArgumentException`, `OutOfMemoryException` and others.
- **Diagnostics** are logged as `drag.data.unavailable|retry|recovered|gaveUp|sessionFallback|dropUnavailable`. They record the status, the HRESULT, the read number and a file count, never paths. `drag.enter` gains `pending`, and `drag.files` logs a late menu.

**Evidence:**
- `tests/WinTools.Worker.Tests/DropDataTests.cs` (29 tests) uses fake `IDataObject`s backed by real moveable HGLOBALs and a manual clock.
  - On the behaviour before the fix (the test seam only), 18 of 24 tests failed, and a second run crashed the test host in `DragQueryFileW` (access violation) on the malformed-block tests.
  - The four tests added after the evaluator review failed on the first version of the fix: slow read, read budget, re-entry not resetting the limit, missing final terminator.
  - All 29 pass.
- **Not covered by unit tests:**
  - The fakes are managed objects, so the tests prove that the target lets go of its reference, but not the RCW count arithmetic. Real Explorer drags exercise that.
  - The coordinator's pending, late-menu and release-grace paths (`InteractionCoordinator`) need the real overlay and are covered only by the Explorer runs.
- Real-Explorer regression runs: see PR #11.

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
