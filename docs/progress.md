# Project progress (cross-session state)

Keep this a snapshot, not a diary. Update at the end of every session (workflow step 13).

## Current phase
**Functional V1 done (2026-10-21).** The functional roadmap ran as one pull request per task. The final UI/UX design has not started.

| Task | State |
|---|---|
| 0. Stabilise main | Done (PR #1 merged) |
| 1. Formats and capability matrix | Done (PR #2 merged) |
| 2. Resize, compress, rotate | Done (PR #3 merged) |
| 3. Metadata, image info, batch, settings, output folder | Done (PR #4 merged) |
| 4. Hierarchical radial menu | Done (PR #5, merged with this file) |
| 5. V1 hardening | Done (PR #6, merged with this file) |
| 6. Final acceptance gate | Done (PR #7, merged with this file) |

## Last known-good commit
`main` at the merge of PR #7 (CI `build-and-test` green). `main` is protected: pull request and a green `build-and-test` required.

## Work completed
- Formats: PNG, JPEG, WebP, BMP, TIFF in every direction (ADR-0006). SVG and HEIC deferred (ADR-0012).
- Operations: convert; resize (percent and boxes); compress (three levels, PNG lossless); rotate; remove metadata; image info. Batch up to 1000 files through a pool of at most 4 workers (ADR-0005). Settings data layer (ADR-0013).
- The radial menu has two levels, shown one at a time and centred on the pointer: the categories (Convert, Compress, Resize, Rotate, Tools), then the commands of the open category plus Back (ADR-0014). Every operation has a place in the menu. Explorer runs: 71/71 cycles in 22 scenarios, including fast releases and a trigger in a screen corner (`docs/performance/2026-10-21-hierarchical-menu.md`).
- Hardening (TASK 5):
  - limits of 50 MP and 1.5 GB per worker, with workers bounded by available commit (ADR-0015);
  - the security review's findings fixed or recorded;
  - a 69-file real-world corpus;
  - leak and stall soak runs (`docs/security/2026-10-21-hardening.md`).
- Acceptance (TASK 6): main verified from a clean state, the Explorer suite re-run on the final build (71/71), every requirement reviewed (`docs/acceptance/2026-10-21-functional-v1.md`).
- Measurements: `docs/performance/2026-10-21-first-slice.md`, `docs/performance/2026-10-21-batch.md`, `docs/performance/2026-10-21-worker-memory.md`, `docs/performance/2026-10-21-soak.md`. Threat model: `docs/security/threat-model.md`. Dependency matrix: `docs/architecture/dependency-matrix.md` (SkiaSharp for WebP, worker only).

## Decisions made
- Native Win32 overlay (ADR-0001); Space trigger through low-level hooks (ADR-0003), configurable in settings (Space, F8–F12).
- Conversion in disposable worker processes, a bounded pool per batch (ADR-0005). WIC with Microsoft codecs by CLSID, SkiaSharp for WebP (ADR-0006).
- Output: temp → validate → no-overwrite rename; the host re-checks the result by hash. Output in the source folder or a configured folder.

## Open questions (each with the experiment or decision that closes it)
- Multi-monitor and mixed DPI; the Desktop as drag source; Windows 10. These need the manual protocol or Windows Sandbox.
- A user-visible failure state (notifications or a tray icon). Today a failure gives only the error sound and a log entry.
- Overlay accessibility: its own provider, which must not block the overlay thread.
- The overlay latency measured at the acceptance gate was higher than at TASK 4 (warm p95 20.1 against 13.6 ms, first activation max 60.5 against 34.0 ms), within budget, with no change to the overlay code; the cause was not investigated. Re-measure on an idle machine.
- Owner decisions: the supported Windows baseline (ADR-0002) and packaging (ADR-0008); the requirement status changes made at the acceptance gate (SCOPE-001 rejected; UX-007, DIST-001 and DIST-003 deferred), pending confirmation.

## Current blockers
None.

## Next recommended task
The UI/UX phase, once the owner starts it: the visual design of the overlay, a user-visible failure state, progress and cancel, a tray icon and settings interface. It has not started. The open items per requirement are in `docs/acceptance/2026-10-21-functional-v1.md`.
