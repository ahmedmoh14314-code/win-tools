# Project progress (cross-session state)

Keep this a snapshot, not a diary. Update at the end of every session (workflow step 13).

## Current phase
**0.2.0 Beta, Tangerine parity (owner direction, 2026-10-25): one release with media included; no V2/V3 split.**
- Flow: drag → trigger → a contextual wheel (Convert / Tools / Actions and the module's own groups) → drop. English and Arabic (RTL).
- Mature engines over in-house work; obscure cases are documented, not chased.
- **No 0.2.0 release or tag exists yet.** Remaining work is listed under "Not done".

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
| #28–#30 | Checkpoint docs and release-matrix rows; portable ZIP names; the dialog-focus spike | Merged |
| #31 | PDF 2.2: Protect and Remove protection, the password dialog and the secret channel (ADR-0026) | Merged |
| #33 | Encrypted ZIP extract with the password dialog (ADR-0027) | Merged |
| #34 | General Files 7.2: Batch rename with a preview, Copy name, Folder path | Merged |
| #35 | The WinTools window (Home, How to use, Features, Settings, About), the first-run tour, English and Arabic | Merged |
| #36 | Media: FFmpeg 8.1.3 LGPL, video and audio convert, compress, edit, info (ADR-0028) | Merged |
| #37 | The wheel as Convert / Tools / Actions; the wheel, toasts, dialogs and panels in Arabic (RTL) | Open at this writing |
| #38 | Forms: Trim, Split, a custom subtitle shift, video crop presets | This PR |

## Last known-good commit
- `main` at `fe01125`, the merge of #31, with CI `build-and-test` green.
- #31's own Explorer runs on its final build passed: Protect 2/2, Remove protection 2/2, PDF info 1/1.
- The full matrix below was run on `b67ae8f`, before #31.
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

## Done since #31 (with evidence)
- **Forms** (#38): `FormTests`, `MediaWorkerTests` (Trim, Split, Crop on the bundled FFmpeg), `DialogsProcessTests` (the form opens in both languages). Explorer: Trim video 10/10, Trim audio 10/10, Shift… 10/10, Crop 1:1 3/3.
- **Arabic** (#37): every wheel label fits at 96–192 DPI in both languages (`ArabicWheelTests`, `UiSurfaceTests`); the ring, toasts and info panel checked by eye with `WinTools.Preview --lang ar`.

## Not done (V2 Definition of Done, §9 and §15)
- **PDF:**
  - PDFium rendering, text and image extraction (3.x);
  - Images→PDF;
  - Organize Pages (4.1);
  - crop.
- **Documents:** TXT/Markdown/CSV → PDF (needs the rendering backend); the optional external converter.
- **Archives:**
  - TAR/GZ create;
  - 7z/RAR extract (SharpCompress review);
  - batch extraction.
- **General Files:** MD5; Copy/Move to a folder, Terminal here, Share, a shelf.
- **Settings V2 extras:** module defaults and conflict detection beyond the trigger policy.
- **Subtitles:** ASS/SSA; TXT export.
- **Images:** HEIC/AVIF, SVG, crop, edit, annotate, redact, collage, Images→PDF.
- **Wheel:** keyboard navigation; letter triggers (only after the typing-interference test).
- **Stage 8 hardening:**
  - fuzzing per parser;
  - soak;
  - the full security review;
  - the performance pass (the TAR double read is recorded: Info on a 400 MB TAR.GZ, 1.8 s);
  - the licence CI gate.
- **Stage 9:** the acceptance gate, the installed-artifact matrix, the 0.2.0 Beta installer.

## Decisions made
ADR-0017 to ADR-0025 (see `docs/adr/README.md`). ADR-0018 to ADR-0025 are **Proposed**: they need the owner's acceptance.

## Open questions (each with the experiment or decision that closes it)
- **Flaky test:** `WorkerProcessTests.Jpeg_whose_scan_is_cut_before_a_valid_EOI_fails_instead_of_inventing_pixels` failed once under a full parallel run on 2026-10-25.
  - It then passed 3/3 in isolation, in the next full runs, and in a 15-run loop of its class with the PDF and archive tests (900 tests).
  - The cause is unknown and the failure message was not kept. Capture it the next time it fails.
- **Owner checks:**
  - a drop by hand on "Protect…", then the protected PDF opened in Acrobat with the same password (an Arabic one ideally: SASLprep's bidi rule is not enforced);
  - the Settings window with an Arabic keyboard layout: click into the width box. The dialogs process crashed there (fixed in #31); Settings has the same setting and was not changed;
  - the Explorer behaviour on a OneDrive-synced folder (archives);
  - real Office files saved by Office (Documents, Remove properties): the tests use packages shaped like Office's.
- Owner decisions that are still open: ADR-0002 (Windows baseline), ADR-0008 (packaging), the ADR-0017 gate values.

## Current blockers
None. The remaining V2 items need the dialogs (passwords, rename patterns, offsets) and the PDFium spike.
- The dialog keyboard-focus question (ADR-0019) has a positive spike: `docs/research/2026-10-25-dialog-focus-after-drop.md`, 4 of 4 automated drops. A manual drop by the owner is still pending.

## Next recommended task
1. Encrypted-ZIP extract: it reuses #31's prompt, policy and channel (ADR-0026).
2. PR 3.1: the PDFium spike and ADR.
3. PR 7.2: batch rename.
