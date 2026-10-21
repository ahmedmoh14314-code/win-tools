# Project progress (cross-session state)

Keep this a snapshot, not a diary. Update at the end of every session (workflow step 13).

## Current phase
**Functional V1 (2026-10-21).** The functional roadmap runs as one pull request per task; the final UI/UX design has not started.

| Task | State |
|---|---|
| 0. Stabilise main | Done (PR #1 merged) |
| 1. Formats and capability matrix | Done (PR #2 merged) |
| 2. Resize, compress, rotate | Done (PR #3 merged) |
| 3. Metadata, image info, batch, settings, output folder | Done (PR #4 merged) |
| 4. Hierarchical radial menu | Done (PR #5) |
| 5. V1 hardening | Next |
| 6. Final acceptance gate | Pending |

## Last known-good commit
`main` at the merge of PR #5 (CI `build-and-test` green). `main` is protected: pull request and a green `build-and-test` required.

## Work completed
- Formats: PNG, JPEG, WebP, BMP, TIFF in every direction (ADR-0006). SVG and HEIC deferred (ADR-0012).
- Operations: convert; resize (percent and boxes); compress (three levels, PNG lossless); rotate; remove metadata; image info. Batch up to 1000 files through a pool of at most 4 workers (ADR-0005). Settings data layer (ADR-0013).
- The radial menu has two levels, shown one at a time and centred on the pointer: the categories (Convert, Compress, Resize, Rotate, Tools), then the commands of the open category plus Back (ADR-0014). Every operation has a place in the menu. Explorer runs: 71/71 cycles in 22 scenarios, including fast releases and a trigger in a screen corner (`docs/performance/2026-10-21-hierarchical-menu.md`).
- Measurements: `docs/performance/2026-10-21-first-slice.md`, `docs/performance/2026-10-21-batch.md`. Threat model: `docs/security/threat-model.md`. Dependency matrix: `docs/architecture/dependency-matrix.md` (SkiaSharp for WebP, worker only).

## Decisions made
- Native Win32 overlay (ADR-0001); Space trigger through low-level hooks (ADR-0003), configurable in settings (Space, F8–F12).
- Conversion in disposable worker processes, a bounded pool per batch (ADR-0005). WIC with Microsoft codecs by CLSID, SkiaSharp for WebP (ADR-0006).
- Output: temp → validate → no-overwrite rename; the host re-checks the result by hash. Output in the source folder or a configured folder.

## Open questions (each with the experiment or decision that closes it)
- Multi-monitor and mixed DPI; the Desktop as drag source; Windows 10. These need the manual protocol or Windows Sandbox.
- A user-visible failure state (notifications or a tray icon). Today a failure gives only the error sound and a log entry.
- JPEGs with benign codec warnings are refused; not yet measured on a real-world photo set (TASK 5).
- Image limits (100 MP) are high for 4 parallel workers; lower limits and a job-object memory cap (TASK 5).
- Overlay accessibility: its own provider, which must not block the overlay thread.

## Current blockers
None.

## Next recommended task
TASK 5 hardening: security review of hostile input, lower image limits and a worker memory cap, a real-world corpus, performance and leak runs, multi-monitor (or a documented limitation), final evaluator.
