# Project progress (cross-session state)

Keep this a snapshot, not a diary. Update at the end of every session (workflow step 13).

## Current phase
**First product slice in review (2026-10-21):** PNG → JPEG and JPEG → PNG end to end from Windows Explorer. It lives on branch `feat/first-image-conversion-slice` with an open pull request against `main`. The independent evaluator's verdict is MERGE once the minor items are fixed or recorded; they are, in the PR. Not merged; external review pending.

## Last known-good commit
`main`: the Spike 002C documentation commit. Branch `feat/first-image-conversion-slice`: the head of the PR, with CI green on that head.

## Work completed (this phase)
- Product source: `src/WinTools.Core`, `src/WinTools.Windows`, `src/WinTools.Host`, `src/WinTools.Worker`. Tests: `tests/WinTools.Core.Tests` (117), `tests/WinTools.Worker.Tests` (49 in Debug, 47 in Release; the crash and hang tests need the Debug fault injection), and the local Explorer harness `tests/WinTools.EndToEnd`.
- ADR-0001 and ADR-0003 accepted earlier. ADR-0005, 0006, 0007 and 0011 accepted for the slice.
- Measurements: `docs/performance/2026-10-21-first-slice.md`. Threat model: `docs/security/threat-model.md`. Dependency matrix: `docs/architecture/dependency-matrix.md`. CI: `.github/workflows/ci.yml`.

## Decisions made
- The overlay is native Win32 (ADR-0001). The trigger is Space through low-level hooks on a dedicated thread (ADR-0003).
- Conversion runs in a disposable worker per batch (ADR-0005). The engine is WIC with Microsoft codecs by CLSID (ADR-0006).
- Output goes temp → validate (structure, full decode, codec diagnostics) → no-overwrite rename. The host re-checks the result by hash.
- The ring is centred where the trigger was pressed (the pointer position captured by the hook).
- Transparent PNG → JPEG is composited onto white. The JPEG EXIF orientation is applied for PNG output. Metadata is not copied.

## Open questions (each with the experiment or decision that closes it)
- Multi-monitor and mixed DPI; the Desktop as drag source; Windows 10. These need the manual protocol or Windows Sandbox.
- A user-visible failure state (notifications or a tray icon). Today a failure gives only the error sound and a log entry.
- A minimum visible time before a drop may run a command, before destructive or many commands exist (evaluator m12).
- JPEGs with benign codec warnings are now refused. This has not been measured on a real-world photo set.
- Overlay accessibility: its own provider, which must not block the overlay thread.

## Current blockers
None.

## Next recommended task
External review of the PR. After merge: user-visible job feedback (tray or notification) and the next format, each as its own slice.
