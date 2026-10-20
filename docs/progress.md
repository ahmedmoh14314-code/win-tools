# Project progress (cross-session state)

Keep this a snapshot, not a diary. Update at the end of every session (workflow step 13).

## Current phase
**Phase 1B — reference review + Spike 001 closure: done (2026-10-20).** Evaluator verdict: CONDITIONAL GO for the framework-independent part of the first slice once a plan is approved; overlay/tray/App host blocked on ADR-0001/0003/0005. No product code, no conversion libraries. Phase 2 (Spike 002: overlay framework + input robustness) in progress.

## Last known-good commit
Public repository: the initial snapshot commit (exported from the reviewed private engineering tree). Earlier checkpoints are kept in the private archive.

## Work completed (this phase)
- Reference reviews in `docs/research/references/` (Action Wheel MIT, FileConverter GPL research-only + 579 issues, DragShift/Gradial/AutoHotPie MIT, WinUI 3 feasibility, input-hook robustness, Tangerine public behaviour) with a verified licensing ledger; nothing copied.
- Decision matrix: `docs/research/reference-implementations.md`. Proposal: `docs/architecture/product-architecture-proposal.md`.
- Spike 001 closure (`2026-10-19-interaction-spike.md` §R-K): hardened Explorer harness (location-based identity, re-verify before every injection, close own view only); Explorer release outside the ring clean; release timing, early trigger, swallow, right button (rejected); 1000/1000 cycles with one gen0 GC; external idle CPU 0.077 %.
- Requirement ledger: 7 new requirements from the review (UX-007..009, SAFE-004/005, SEC-003, DIST-003); 34 total.
- Repository prepared for publication: rules in `docs/engineering/rules/`, entry document `CONTRIBUTING.md`, no local tooling configuration tracked.

## Decisions made
- Drag modifiers, middle and right buttons are not triggers (measured + DragShift evidence).
- Post-release rule: wait for OLE Drop/DragLeave after DragEnter; grace only without DragEnter; Esc ignored while pending.
- Engines only in an on-demand worker; output via temp → validate → exclusive create; WIC codecs by CLSID/Microsoft vendor.
- WinUI 3 rejected for the V1 overlay (no public transparency API, open bugs).
- No polling anywhere in the idle path, including hook liveness.

## Open questions (each with the experiment or decision that closes it)
- ADR-0001 framework: owner sets an idle private-bytes ceiling; Spike 002(a) (WPF dead zone alpha ≥ 1: DragEnter without movement, fall-through) decides WPF vs hybrid.
- ADR-0005 trigger detection: E1 (hook entry latency under GC churn, in the chosen stack), E2 (Raw Input during an Explorer drag).
- Capability at DragEnter: spike row (HDROP + bounded sniff, placeholders).
- Memory: 1000-cycle private-bytes growth (≈2 MB native) misses the 0-growth budget; explain.
- Not testable here: multi-monitor/mixed DPI, Windows 10, Desktop source (do in Windows Sandbox), elevated source (M12), feel pass (human).

## Current blockers
None. Product work waits for Spike 002 (ADR-0001, ADR-0005).

## Next recommended task
Time-boxed Spike 002 follow-up: root-cause the 5 s WPF render stall (B1) with ETW and a PostMessage hand-off, and run the fast-release race test on both hosts. If WPF fails inside the time-box, adopt a Win32 overlay with a synchronous layered bitmap. In parallel, plan the framework-independent slice.
