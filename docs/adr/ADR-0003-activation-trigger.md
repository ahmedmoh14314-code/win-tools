# ADR-0003: Activation and input strategy during a drag

- **Status:** Accepted with amendments (2026-10-20, confirmed 2026-10-21): Space + low-level hooks on a dedicated thread, minimal allocation-free callback, asynchronous PostMessage hand-off. Raw Input is an investigated fallback and is not implemented in V1. No Raw Input latency figure is claimed (withdrawn, evaluator M2). Open: UIPI behaviour, the RegisterHotKey-during-drag alternative.
- **Date:** 2026-10-19
- **Related requirements:** UX-001, UX-002, UX-003, UX-006

## Context
The wheel must be summoned while an OLE drag is in progress and the mouse button is held. During a drag the shell interprets Ctrl (copy), Shift (move), Alt or Ctrl+Shift (link) and Esc (cancel). The drag source's `IDropSource::QueryContinueDrag` and the target's `IDropTarget::DragOver` receive key state. Keyboard focus during drag belongs to the drag loop, so ordinary window-level key handling is not available; options are low-level keyboard hooks, `RegisterHotKey`, raw input, or mouse-button-based triggers (middle/X buttons). Each option has latency, reliability, interference and accessibility trade-offs. The trigger must eventually be user-configurable.

## Decision
**Trigger:** a non-modifier key, **Space** by default, fixed in V1 (UX-003 configurable trigger stays deferred). Ctrl/Shift/Alt alone, the middle button and the right button are excluded (they change or cancel Explorer's drag; measured). Ctrl+Space and X1 work and remain candidates for configuration later.

**Input mechanism (Spike 002B):**
- **Low-level keyboard and mouse hooks — ADOPT (primary).** Installed together (all or nothing) on a dedicated thread that only pumps messages and posts compact events to the UI thread. Callbacks: no allocation, no I/O, no locks shared with the UI thread, `try/catch` around everything, return immediately. The trigger key is not swallowed in V1 (no measured need); if swallowing is ever added, down, auto-repeat and up are swallowed as a pair, and Esc never.
- **Raw Input — investigated fallback, not implemented in V1.** Spike 002B showed keyboard Raw Input (`RIDEV_INPUTSINK`) keeps arriving during a foreign OLE drag and distinguishes injected input. It cannot suppress keys. Its latency was not established (the measurement included queueing behind overlay work and is withdrawn). It would be the basis of a future hook-health detector or a degraded activation path; it enters scope only with a demonstrated need.
- **Hook health (future reliability requirement, not built in V1):** Windows gives no notification when it removes a low-level hook. V1 keeps the resident process allocation-light so the hook thread is not starved, logs hook installation failures, and does not re-install blindly or on a timer. Detection and recovery (e.g. a Raw Input comparison, a check at trigger time, a re-check on unlock/resume) are a recorded future requirement.

## Alternatives considered
Measured in Spike 001 (2026-10-20, Windows 11, Explorer and shell-default drag sources, `docs/research/2026-10-19-interaction-spike.md`):

| Candidate | Explorer drag survives trigger? | Drop received | hook→overlay shown (median) | Notes |
|---|---|---|---|---|
| Space | yes (invisible to OLE by design) | 10/10 Explorer, 100/100 self | ≈9–10 ms | Space goes to the foreground app's queue as a normal key; the spike did not swallow it (`--swallow` exists but was not measured). Needs a check of what Explorer does with a stray Space during drag (nothing observed in the runs). |
| Ctrl+Space | yes | 5/5 Explorer, 20/20 self | ≈8–10 ms | Ctrl briefly flips Explorer's cursor to "copy" while held; effect returned by our target is COPY regardless. |
| X1 (side button) | yes | 5/5 Explorer, 20/20 self | ≈9 ms | Not available on every mouse. |
| Middle button | **no**: shell drop source cancels the drag on a second button press | 0/5 Explorer, 0/20 self | ≈9 ms (overlay shows, drag already dead) | **Rejected** as a trigger for Explorer/shell drags. |
| Ctrl / Shift / Alt alone | not tested as triggers: they are Explorer's Copy/Move/Link modifiers | – | – | Excluded by the brief. |
| Space while Shift is held | detector ignores (modifiers must match) | – | – | Product decision: strict match (current) vs "trigger key with any modifiers". |
| Hover/dwell, right button | not tested | – | – | Right button is expected to cancel like the middle button (same source logic); dwell adds latency by construction. |

Not measured: false-positive rate in daily use, conflicts with other apps' Space handling during drag, keyboard layouts (Space is layout-independent; X buttons are hardware-dependent), behaviour from elevated sources (UIPI; manual protocol M12 pending).

## Consequences
- Known Windows limitations: no notification when Windows removes a hook; the default `LowLevelHooksTimeout` is undocumented (1000 ms cap); hooks and Raw Input do not see input while an elevated window is foreground (UIPI) — documented, not worked around (no UIAccess); the trigger press that reveals a removed hook is lost.
- Measured robustness: 0 lost activations at the hook level under heavy and extreme GC pressure; no removal observed in 314 runtime suspensions up to ≈ 550 ms, but hook-entry delay reached 489 ms under forced blocking GC and removal beyond the timeout was not tested. The GC risk is reduced (allocation-light resident process, conversion in the worker), not eliminated.
- AV/EDR: public EDR rules flag unsigned, recently built executables calling `SetWindowsHookEx(WH_KEYBOARD_LL)`, including managed callers; sign the binaries, install per-user into a stable folder, keep the hook DLL-free (in-process, no injection). Not tested against a real EDR.
- Event threading: hook thread → posted message → UI thread state machine.
- Test obligations (when hook health is built): debug-only fault injection that stalls the hook callback beyond the timeout.

## Evidence / experiments
**Spike 002 (2026-10-20):** `docs/research/2026-10-20-spike-002.md` (dead-zone hit-testing, Win32 vs WPF on identical runs, hooks under GC pressure, Raw Input during Explorer drags, 1500-cycle memory plateau).

Spike 001 (`spikes/interaction/`, results in `docs/research/2026-10-19-interaction-spike.md`).
- Documentation: `IDropSource::QueryContinueDrag` only receives MK_CONTROL/SHIFT/ALT and mouse buttons; a plain key such as Space is invisible to the drag source. A low-level keyboard hook on a dedicated thread sees it with < 1 ms typical callback cost (worst observed 2.75 ms).
- Measured 2026-10-20 with real Explorer windows driven by UI Automation and with the shell's default drop source: see the table above. Esc while the overlay is shown cancels cleanly (Explorer 5/5, self 5/5: DragLeave, DRAGDROP_S_CANCEL, nothing dropped). Explorer's folder was unchanged after ≈70 Explorer-sourced drags.
- Required state-machine rule discovered: the hook sees the button-up before the drag loop does. After a release with the drag inside the overlay, wait for OLE Drop/DragLeave with no short timer (OLE guarantees the terminal call once DragEnter arrived; a long watchdog only guards a dead source); a short grace applies only when no drag is inside; Esc after the release is ignored while a drop is pending. Hiding earlier loses the drop onto whatever is underneath (negative control: 10/10 lost; corrected rule: 30/30 received). Implemented in the spike's `InteractionSession`; unit-tested.
- Esc with Explorer as source: overlay hides and DragLeave follows within 4 ms; the shell-side cancel (DRAGDROP_S_CANCEL) is observed in self mode and inferred for Explorer.
- Key swallowing: the spike's `--swallow` flags the trigger key whenever the left button is down, which would eat Space system-wide in any app while a button is held. Product rule must be "swallow only when the overlay consumed the key", and Esc must never be swallowed (the shell relies on it to cancel). `--swallow` was not measured.
- Hook robustness (evaluator finding): a managed hook thread is suspended during GC; a pause longer than `LowLevelHooksTimeout` gets the hook silently removed and the trigger goes dead with no symptom. Detection/re-install (or Raw Input) and AV/EDR heuristics on global keyboard hooks must be designed before this ADR is accepted (tracked with ADR-0005).
- Still open before acceptance: human "feel" pass; what other foreground apps do with an unswallowed Space during their own drags (automatable via UIA selection checks); elevated sources (M12); right button; early trigger (before Explorer enters `DoDragDrop`) and trigger over another app's window.

## Evaluator review
Spike 002 evaluator (2026-10-20): acceptable with amendments (Space trigger, dedicated hook thread, allocation-free hand-off, Raw Input demoted, M2 latency claim withdrawn). Amendments applied 2026-10-21.
