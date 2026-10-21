# Functional V1 acceptance (TASK 6, 2026-10-21)

This gate checks the functional V1 the product owner specified: TASKs 0–5 merged, verified from a clean state, every requirement reviewed against its evidence. The final UI/UX design has not started and is not part of this gate.

## Verified from a clean state
`main` at `7721a36` (merge of PR #6), checked out into a new worktree with no build output, on Windows 11 Pro 26200 (120 DPI, one monitor).

| Check | Result |
|---|---|
| `dotnet restore` + build, Debug | 0 warnings, 0 errors |
| Core tests, Debug / Release | 223/223 / 223/223 |
| Worker and process tests, Debug / Release | 184/184 / 177/177 (the 7 fault-injection tests are Debug-only by design) |
| Build, Release | 0 warnings, 0 errors |
| `dotnet list package --vulnerable --include-transitive` | No vulnerable packages in any project |
| Requirements ledger (`scripts/Validate-Requirements.ps1`) | OK |
| CI `build-and-test` on the merge | Green |
| Explorer suite on this build | See below |

## Explorer suite on the final build
All 22 scenarios of the suite (`docs/performance/2026-10-21-hierarchical-menu.md`) were run on the Release build of this commit, each from its own shell, with the same criteria as at TASK 4. **71 of 71 cycles passed.** Per-cycle records, without local paths, are in `2026-10-21-functional-v1-explorer-runs.jsonl`.
- Every producing cycle had a valid output and a clean folder, and its sources were unchanged by hash.
- No cycle left the button or Space down, and every host was alive at the end of its run.
- One attempt at `png-rotate-right`, and one at `jpg-strip`, were refused by the foreground guard before anything was injected: the PC was in use and another window was in front. Both passed when run again with the screen free. No criterion was changed.

| Metric (host log, all 22 runs) | This gate | TASK 4 run | Budget |
|---|---|---|---|
| Trigger → overlay shown, warm (n = 49) | p50 12.3 ms, p95 20.1 ms, max 23.6 ms | p95 13.6 ms | ≤ 50 ms p95 |
| Trigger → overlay shown, first activation of a host (n = 22) | p50 33.6 ms, p95 45.7 ms, max 60.5 ms | max 34.0 ms | ≤ 120 ms |
| Trigger → first OLE DragEnter on the overlay (n = 71) | p50 27.1 ms, p95 112.8 ms, max 127.9 ms | p95 88.8 ms | — |
| Host private bytes at the end of a run | 9.4–16.0 MB | 9.5–16.2 MB | ≤ 100 MB |
| Host working set at the end of a run | 39.2–50.6 MB | 39.2–50.6 MB | ≤ 150 MB |

The latencies are within budget but higher than in the TASK 4 run. The overlay code has not changed since then: TASK 5 changed only the settings size limit and the project file in the host. These runs were made between periods of use, with other programs open; the cause was not investigated further. The highest first activation (60.5 ms) was the corner scenario's, which moves the Explorer window just before.

## Other evidence gathered at this gate
- **Offline (PLAT-002):**
  - No network API in the product code.
  - During 1000 conversions, 10 `netstat` samples found 0 TCP/UDP endpoints owned by any of the 10 WinTools processes seen.
  - Not done: a run with the network adapter disabled, which would change system settings.
- **No elevation (PLAT-003):**
  - Both manifests request `asInvoker` with `uiAccess=false`.
  - No code path uses ShellExecute, a verb or elevation.
  - Every run here used a non-elevated token (`TokenElevation = 0`).
- **Size (PERF-002):** a framework-dependent Release publish of the host, worker included, is 14.1 MB. The WebP engine (`libSkiaSharp.dll` and `SkiaSharp.dll`) is 13.4 MB of that, and the product's own files are 0.7 MB. Budget: under 30 MB without engines.

## Requirements
After this gate: 14 verified, 15 in progress, 6 deferred and 3 rejected, of 38. Each one's evidence or rationale is in `docs/product/requirements.json`.

| Status | Count | Requirements |
|---|---|---|
| verified | 14 | UX-004, UX-006, SAFE-002, ARCH-003, PERF-001, PERF-002, SEC-001, SEC-002, DEP-001, SEC-003, OPS-001, OPS-002, OPS-003, OPS-004 |
| in-progress | 15 | PLAT-001, PLAT-002, PLAT-003, UX-001, UX-002, UX-005, SAFE-001, SAFE-003, ARCH-001, ARCH-002, ARCH-004, UX-008, UX-009, SAFE-004, SAFE-005 |
| deferred | 6 | UX-003, SCOPE-003, DIST-001, DIST-002, UX-007, DIST-003 |
| rejected | 3 | SCOPE-001, SCOPE-002, SCOPE-004 |

What is still open in each requirement in progress:

| Requirement | What is missing |
|---|---|
| PLAT-001 | The supported Windows baseline (ADR-0002, owner decision); a Windows 10 run |
| PLAT-002 | A run with the network adapter disabled |
| PLAT-003 | A run under an account outside Administrators; installing (packaging is deferred) |
| UX-001 | The Desktop as drag source |
| UX-002 | ADR-0003 is Accepted (Space with low-level hooks), but the ledger cites no evidence for its two criteria yet. Its open points are UIPI and the RegisterHotKey-during-drag alternative |
| UX-005 | Manual protocol at 100/125/150/200 % and on two monitors with mixed DPI |
| SAFE-001 | User cancel: none in V1. Cancellation inside the pool is tested |
| SAFE-003 | A user-visible, actionable failure state (UI phase) |
| ARCH-001 | The boundaries are enforced by project references and a test. `docs/architecture/product-architecture-proposal.md` describes them, but as a proposal: not yet brought up to date with what was built |
| ARCH-002 | A fake adapter in tests (deliberately not built: one engine, YAGNI) |
| ARCH-004 | Progress reporting and user cancel |
| UX-008 | A test with a non-HDROP data object |
| UX-009 | An ADR and an actionable message for folders |
| SAFE-004 | The placeholder attribute check with an explicit reason, and its test |
| SAFE-005 | Showing the reason to the user (UI phase) |

## Known limitations of functional V1
- **Windows:** only Windows 11 was tested; the supported baseline (ADR-0002) is an open owner decision.
- **Interaction not covered here:**
  - multi-monitor and mixed DPI (one monitor here);
  - the Desktop as drag source;
  - DPI settings other than 120.

  These stay on the manual protocol.
- **Interface:**
  - No user-visible failure state: a failure gives the error sound and a log entry.
  - No progress display, and no way to cancel a running batch.
  - No tray icon, no settings interface, no keyboard alternative.

  These belong to the UI phase.
- **Images:**
  - Images over 50 MP are refused (ADR-0015).
  - Multi-page TIFF and animated images are refused.
  - SVG and HEIC are deferred (ADR-0012).
  - Resize and rotate refuse 16-bit images instead of reducing them to 8 bits.
- **Drag sources:** folders and virtual files (zip folders, Outlook) are not processed. A folder named like an image fails with an error sound.
- **Placeholders:** converting a OneDrive placeholder downloads it. Dragging alone never does.
- **Residual security risks** are listed in `docs/security/threat-model.md`. Among them: crafted pixel data with valid checksums, the same-user junction race, and colour-profile text kept by Remove metadata.
