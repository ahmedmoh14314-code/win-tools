# Project progress (cross-session state)

Keep this a snapshot, not a diary. Update at the end of every session (workflow step 13).

## Current phase
**V2 ("Files", 0.2.0 Beta) in progress: Stages 0 and 1 done; Archives, Subtitles and Documents partly done; PDF partly done.**
- The plan is `docs/plans/2026-10-23-v2-technical-plan.md`, with one PR per slice.
- **V2 is not complete.** §15's Definition of Done is not met (see "Not done" below).
- **No 0.2.0 release or tag exists.** V3 (Media) has not started.

| PR | Slice | State |
|---|---|---|
| #10–#14 | Stage 0: trigger modifiers, drag-data recovery, compression semantics (ADR-0017), long paths, release regression matrix | Merged |
| #15–#19 | Stage 1: capability descriptors, job contract v2 and scheduler (ADR-0018), categories, More… and the dialogs process (ADR-0019), General Files (ADR-0020), settings schema 2 and the trigger-key policy (ADR-0021) | Merged; full matrix 26/26 |
| #20 | PDF 2.1 + 2.3 with qpdf 12.4.2: info, merge, split, rotate, lossless optimize, linearize, repair (ADR-0022) | Merged |
| #21 | Archives 5.1: ZIP extract, test, info, Add to ZIP (ADR-0023) | Merged |
| #22 | PDF: retry when qpdf could not open a just-written copy | Merged |
| #23 | Subtitles 7.1: SRT/VTT convert, timing, clean up, info (ADR-0024) | Merged |
| #25 | Installer: version as a parameter; portable ZIP | Merged |
| #26 | Documents 6.1: DOCX/XLSX/PPTX text and info (ADR-0025) | Merged |
| #24 | Archives 5.2: TAR, TAR.GZ and GZIP extract, test, info; GZIP decompress (ADR-0023 decision 9) | Merged |
| #27 | Documents 6.2: Remove properties (ADR-0025 decision 6) | Merged |

## Last known-good commit
- `main` at `b67ae8f`, the merge of #24, with CI `build-and-test` green.
- The full release matrix on that build: **36 of 36 rows, 55 of 55 cycles**, with no key left down (`docs/testing/2026-10-25-release-matrix-v2.md`). It covers the 26 V1 and Stage 1 rows and 10 new V2 module rows.
- `main` is protected: a pull request and a green `build-and-test` are required.

## Work completed in V2 (with evidence)
- **Trigger keys** (ADR-0021): Space, F7, F9 and F12.
  - The typing-interference spike (`docs/research/2026-10-25-typing-interference.md`) showed Explorer ignores them during a drag.
  - F1–F6, F8, F10, F11, letters and digits are refused, with reasons.
- **PDF:** qpdf runs from a pinned, PGP-verified official build (`scripts/Get-Engines.ps1`), only in its own temp folder. Notices are in `third-party/`.
- **Archives:**
  - ZIP: a name policy, one budget, per-entry and whole-archive bomb rules, a CRC check, links and devices never followed.
  - TAR: a first pass reads the whole stream before .NET's `TarReader` (checksums, a 1 MB header cap, PAX sizes followed as `TarReader` follows them, end blocks, declared sizes); sparse entries are refused; the GZIP trailer is checked. Three review rounds, the last with no blocker or major, and 1,300 fuzzed archives with no crash.
- **Subtitles:**
  - encodings are read only when certain;
  - malformed timing is reported with its line;
  - the reviewer's probe gave 0 of 688 wrong acceptances.
- **Documents:**
  - text and info read in house, with no DTD, no resolver and no macro run;
  - Info reads the `app.xml` that Office writes (vectors), a case found after the review.
  - Remove properties: a copy whose core, extended and custom properties are empty. Property parts are found by resolving relationships as OPC does; every other part is copied byte for byte and checked. What stays is said: comment and revision authors, linked paths, the thumbnail.
- **Tests at the last checkpoint:** Core 512 of 512, Worker 528 of 528 (12 measurement tests skipped). 0 warnings in Debug and Release.
- **Explorer runs** (the real harness, one scenario at a time) passed for pdf, zip, tgz, srt and docx (text, info, Remove properties), and the image regressions.
- The release matrix (`tests/WinTools.EndToEnd/Run-ReleaseMatrix.ps1`) now has rows for every V2 module.
  - PDF Optimize has no row: the sample is already compact, so the command rightly answers "nothing to do" while the harness expects a file.

## Not done (V2 Definition of Done, §9 and §15)
- **PDF:**
  - protect and unprotect (2.2: needs the password dialog and a secret channel);
  - PDFium rendering, text and image extraction (3.x);
  - Images→PDF;
  - Organize Pages (4.1);
  - crop.
- **Documents:** TXT/Markdown/CSV → PDF (needs the rendering backend); the optional external converter.
- **Archives:**
  - TAR/GZ create;
  - encrypted-ZIP extract (password dialog);
  - 7z/RAR extract (SharpCompress review);
  - batch extraction.
- **General Files:**
  - batch rename (7.2, needs its dialog);
  - copy name and folder;
  - MD5.
- **Settings V2 extras:** module defaults and conflict detection beyond the trigger policy.
- **Subtitles:** custom offset (needs the dialog); ASS/SSA.
- **Stage 8 hardening:**
  - fuzzing per parser;
  - soak;
  - the full security review;
  - the performance pass (the TAR double read is recorded: Info on a 400 MB TAR.GZ, 1.8 s);
  - the licence CI gate.
- **Stage 9:** the acceptance gate, the installed-artifact matrix, the 0.2.0 Beta installer.
- **V3 Media (FFmpeg):** not started. It needs an LGPL-configured build and its licence review first.

## Decisions made
ADR-0017 to ADR-0025 (see `docs/adr/README.md`). ADR-0018 to ADR-0025 are **Proposed**: they need the owner's acceptance.

## Open questions (each with the experiment or decision that closes it)
- **Flaky test:** `WorkerProcessTests.Jpeg_whose_scan_is_cut_before_a_valid_EOI_fails_instead_of_inventing_pixels` failed once under a full parallel run on 2026-10-25.
  - It then passed 3/3 in isolation, in the next full runs, and in a 15-run loop of its class with the PDF and archive tests (900 tests).
  - The cause is unknown and the failure message was not kept. Capture it the next time it fails.
- **Owner checks:**
  - the Explorer behaviour on a OneDrive-synced folder (archives);
  - real Office files saved by Office (Documents, Remove properties): the tests use packages shaped like Office's.
- Owner decisions that are still open: ADR-0002 (Windows baseline), ADR-0008 (packaging), the ADR-0017 gate values.

## Current blockers
None. The remaining V2 items need the dialogs (passwords, rename patterns, offsets) and the PDFium spike.
- The dialog keyboard-focus question (ADR-0019) has a positive spike: `docs/research/2026-10-25-dialog-focus-after-drop.md`, 4 of 4 automated drops. A manual drop by the owner is still pending.

## Next recommended task
1. PR 2.2: the password dialog and secret channel, then PDF protect/unprotect. The encrypted-ZIP extract reuses both.
   - The spike `docs/research/2026-10-25-qpdf-password-stdin.md` shows qpdf takes every argument, passwords included, on stdin through `@-`: AES-256 with a Unicode password, and a wrong password exits 2 with no output.
   - Its constraints: no line break in a password, never `--show-encryption` (it prints the password), and an amendment to ADR-0022.
2. PR 3.1: the PDFium spike and ADR.
3. PR 7.2: batch rename.
