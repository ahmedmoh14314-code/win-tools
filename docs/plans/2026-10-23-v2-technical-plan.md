# WinTools V2 ("Files", 0.2 Beta): technical plan for review

- **Status:** proposal for the product owner's review. Planning only: no product code changed.
- **Date:** 2026-10-23.
- **Baseline:** `main` at `8836149` (883614946bab…).
- **Evidence:** local commit `611ff10` on `fix/release-blockers-compress-multiselect`. It holds the Stage 0 reproduction (corpus, measurements and failing tests) and is not pushed or merged.
- **Out of scope:** the sound-level branch (`fix/ui-sound-levels`). The owner accepted the current sound.

---

## 1. Current repository architecture: assessment

| Project | Lines | Role today | Image-specific? |
|---|---|---|---|
| `WinTools.Core` | 2,557 | Capabilities, menu model, interaction rules, file identification, output naming, settings model, feedback text, job contract | **Heavily.** `ImageFormat` appears in 12 of its files. |
| `WinTools.Windows` | 2,598 | Hooks, layered overlay, OLE drop target, toast, info panel, tray, settings store, job object | No: generic Windows integration |
| `WinTools.Host` | 928 | Composition root, coordinator, job runner, worker client, feedback presenter | Little: 1 file |
| `WinTools.Worker` | 1,306 | One worker executable: WIC plus SkiaSharp image pipeline, `ConversionJob` | Entirely |
| `WinTools.Settings` | 285 | WPF settings process | No |

### What is sound and must be kept
- **Isolation:** a disposable worker process per lane; a kill-on-close job object with a per-worker memory cap; a bounded pool (ADR-0005, ADR-0015).
- **Output safety:** the source is opened read-only. Output goes to an exclusive temp file, is validated, then gets a no-overwrite rename. The host re-checks the result by SHA-256.
- **Untrusted input:** content signature over extension, canonical paths only (`PathPolicy`), line-JSON with bounded reads, no shell, `ArgumentList` only.
- **Pure, tested Core rules:** the capability resolver, menu navigator, interaction session, ring geometry and feedback text.
- **The UI already asks the resolver** for the available operations and has no format logic (UX-004).
- **The resident host is light:** idle CPU 0.000 %, about 9 MB private.

### Where the code is image-centric (it worked for V1, but blocks V2)
1. **Format type.** `ImageFormat` is the only file-type type. `FormatHint` (by extension) and `FileSignature` (by content) know only five image formats.
2. **Operation record.** `Operation` carries image fields (`Target: ImageFormat`, `ResizeSpec`, `CompressLevel`, `RotateDegrees`), and `OperationKind` is an image enum.
3. **Menu categories.** `MenuCategory` is a fixed enum (Convert, Compress, Resize, Rotate, Tools).
4. **Job contract.**
   - `ConversionRequest` holds one source path, one output folder and image qualities.
   - `ConversionResult` has a typed `ImageInfo`.
   - `OutputNaming` produces one file named after an `ImageFormat`.
5. **Job execution.** `ConversionJob` reads the whole source into memory, with a 256 MB cap. It produces exactly one output file and validates it as an image.
6. **Job runner.**
   - `JobRunner` runs one batch at a time (a global gate).
   - Every request is one input with one output.
   - There is no cancellation: `CancellationToken.None` is passed throughout.
   - The timeout (60 s) and memory cap (1.5 GB) are tuned for images.
7. **Feedback words** ("images", "Converted to …") are written for images.
8. **The E2E harness** builds its expected menu from `RadialMenu.Catalog`, so it assumes the image menu.

---

## 2. Confirmed root causes: V1 release blockers

### Blocker 1: Compress is not meaningfully effective
- **Root cause:** in `ImagePipeline.Compress` (src/WinTools.Worker/Imaging/ImagePipeline.cs), each preset is one fixed quality: High 90, Balanced 80, Smallest 65. That has several consequences:
  - **No measure of fidelity:** no quality floor.
  - **No adaptive selection:** no candidate search.
  - **No minimum useful saving:** any output 1 byte smaller is written.
  - **Chroma resolution is dropped:** a 4:4:4 source always becomes 4:2:0.
- **Why the old tests missed it:** the existing test (`Jpeg_compression_levels_are_ordered_and_smaller_than_the_source`) uses 400×300 random noise saved at q98. It only proves `high < original`, `balanced < high` and `small < balanced`. Uniform random noise behaves like no real photograph.

### Blocker 2: the first or multi-select drag does nothing
- **2A, modifiers.** `TriggerDetector.OnKey` accepts the trigger only when `leftDown && !ctrl && !shift && !alt`. `LowLevelHooks` re-syncs Ctrl, Shift and Alt from `GetAsyncKeyState` right before evaluating it.
  - So a user who selects with **Ctrl-click and keeps Ctrl held** into the drag and presses Space gets nothing, with no feedback.
  - **Why the tests missed it:** the existing unit test *asserted* this behaviour (`Space_with_a_drag_modifier_is_not_a_trigger`). The batch E2E presses Ctrl+A, **releases Ctrl**, waits about 250 ms, then drags.
  - ADR-0003 had already measured Ctrl+Space as working (Explorer 5/5) with our drop effect COPY regardless, but the strict rule was kept.
- **2B, the data object.** `FileDropTarget.DragEnter` calls `HDrop.TryRead`, which turns any failure (QueryGetData ≠ S_OK, a COM exception, null data) into an empty list. Then:
  - `_hasFiles` becomes false and stays false **for the whole drag**: `DragOver` never reads again, so the effect is NONE everywhere and OLE never delivers a Drop.
  - The coordinator builds an empty menu ("No actions"). It recovers only if the pointer leaves and re-enters the overlay.
  - At drop time, a failed read passes `[]` to the coordinator, even when the paths were known on DragEnter. The drop then runs nothing (`commandsChanged`).
  - There is no diagnostic: the log does not distinguish "no files", "no CF_HDROP" and "read failed".
  - **Why the tests missed it:** every E2E uses Explorer's real, settled data object, and no test injects a failing `IDataObject`.

---

## 3. Stage 0 measurements already collected (commit `611ff10`)

**Corpus.** Generated by code; no third-party or personal files. The images are 1600×1200:
- photographic scenes with gradients, texture, sharp edges and sensor noise;
- noisy, detail-heavy scenes;
- screenshots with flat panels, 1 px rules and glyph-like strokes.

They are encoded the way real files are: a phone JPEG (q93, 4:2:0), a camera JPEG (q97, 4:4:4), a re-saved web JPEG (q75), a noisy JPEG (q92), a screenshot JPEG (q95), lossy WebP (q92), lossless WebP, an unoptimised PNG (stored), and an optimised PNG (zlib 9, all filters).

**Metric.** Luma SSIM: 8×8 windows on a 4-px grid with the standard constants. It is written independently of the product.

**Current main's Compress on this corpus:**

| File | High | Balanced | Smallest |
|---|---|---|---|
| phone.jpg (q93) | **−6.6 %**, SSIM 0.995 | −54.7 %, 0.985 | −67.6 %, 0.979 |
| camera.jpg (q97, 4:4:4) | −71.2 %, **0.983** (4:4:4 lost) | −83.7 %, **0.977** | −88.7 %, 0.972 |
| moderate.jpg (q75) | nothing to do | nothing to do | **−5.4 %** copy written |
| noisy.jpg (q92) | **−4.5 %** copy written | −57.7 %, **SSIM 0.797** | −71.2 %, **0.752** |
| screenshot.jpg (q95) | −21.2 %, 0.991 | −39.9 %, **0.977** | −53.8 %, **0.960** |
| photo-lossy.webp | −23.9 %, 0.991 | −61.7 %, 0.981 | −72.3 %, 0.975 |
| screenshot-lossless.webp | nothing to do | nothing to do | nothing to do |
| unoptimized.png (lossless) | −99.6 %, identical pixels | | |
| optimized.png (lossless) | nothing to do | | |

**SSIM against quality, measured per file.** These curves drive the design:
- **phone.jpg:** SSIM ≥ 0.990 is still met at q88 (−14 %). Below about q85 it holds 0.98–0.97 down to q50.
- **camera.jpg:** keeping 4:4:4, q95 gives −13.6 % at 0.991, and q94 drops to 0.987.
- **noisy.jpg:** collapses from 0.962 at q88 to 0.908 at q85 and 0.80 at q82. Re-encoding destroys its detail.
- **moderate.jpg:** re-encoding at its own quality gains nothing (0 %). Below q60 it is lossy for a small gain.
- **Not monotonic:** noisy.jpg at q95 scores lower than at q92, because the quantisation grid changes.

**Regression tests run on main's code** (the product code is identical to main):

| Test class | Fail on main | Of | What they show |
|---|---|---|---|
| `ModifierTriggerTests` (Core) | **6** | 6 | Ctrl, Shift and Ctrl+Shift with Space give no trigger; Alt+Space is ignored silently |
| `CompressionQualityTests` (Worker) | **14** | 38 | Floors broken (camera Balanced 0.977, noisy 0.80/0.75, screenshot 0.977/0.960); insignificant copies (phone High 6.6 %, noisy High 4.5 %, moderate Smallest 5.4 %); 4:4:4 not kept; no "saved X%" wording |

Still to write in Stage 0, before the fix:
- the `IDataObject` fault-injection tests (2B), which fail by construction on main;
- the Explorer E2E with Ctrl held.

---

## 4. Additional architectural risks found

| # | Risk | Evidence | Consequence for V2 |
|---|---|---|---|
| R1 | The whole source is read into memory, with a 256 MB cap | `ConversionJob.ReadSource`, `MaxInputBytes` | PDFs, archives and folders can be larger. V2 jobs must stream. |
| R2 | One output file per job | `OutputNaming.Candidates`, rename commit | Split, extract pages and images, and archive extraction produce many files or a folder. They need staged multi-output and directory commits. |
| R3 | One input per job | `ConversionRequest.SourcePath` | Merge, Images→PDF and Add-to-archive take a whole selection. They need selection-level jobs. |
| R4 | No parameters | The menu has no input path | Passwords, page ranges, time offsets and rename patterns need dialogs and a secret-safe channel. |
| R5 | No cancellation, and a global one-batch gate | `JobRunner._gate`, `CancellationToken.None` | A long PDF or archive job would block every image drop, and could not be stopped. |
| R6 | Timeout and memory tuned for images | 60 s, 1.5 GB | Per-module limits are needed. |
| R7 | The menu identifies files by extension | `FormatHint` | DOCX, XLSX, PPTX, EPUB and JAR are all ZIP containers. Identification needs a family-level guess at menu time and a content verdict in the worker. |
| R8 | Fixed menu categories, and a ring capacity limit | `MenuCategory` enum; Resize already has 8 sectors | Categories must come from data. More than 8 sectors needs a policy. Mixed selections need common capabilities. |
| R9 | Folders are not handled | UX-009 open | Archives and rename need folders as inputs. |
| R10 | Licence traps in candidate libraries | PDFsharp 6.5 is AGPL; QuestPDF has a revenue-capped licence (research §7) | A dependency-pinning guard is needed. |
| R11 | Native engines need patching often | Chromium-cadence PDFium; qpdf; SharpCompress advisories; .NET TAR CVE fixed in runtime 10.0.9 | A servicing and rebuild cadence is needed; there is no auto-update. |
| R12 | **Outputs do not carry the source's Mark of the Web** | No Zone.Identifier handling anywhere in `src/` | Low impact for images. **Essential for archive extraction**, and recommended for every module. |
| R13 | The E2E harness is coupled to the image menu and to a settle delay | `RadialMenu.Catalog`, the Ctrl+A–release–250 ms path | The harness must take the catalog from the product's modules and cover realistic timing and modifiers. |
| R14 | Installer size grows | PDFium about 4 MB compressed; qpdf plus its runtime about 14 MB; fonts | Track the size per engine; no bundled Office engine. |
| R15 | The Windows baseline is still undecided | ADR-0002 is Proposed | WebView2 (a PDF backend candidate) and the Windows 10 support claim depend on it. |

---

## 5. Proposed V2 architecture

The guiding rule: **generalise only what at least two real V2 modules need; leave everything else module-local.**

```
Host (resident, light)                        Workers (on demand, one process per engine family)
──────────────────────                        ─────────────────────────────────────────────────
Hooks → Session → Overlay (radial)            WinTools.Worker          images (existing; WIC + SkiaSharp)
        ↓                                     WinTools.Worker.Pdf      qpdf (child process) + PDFium (P/Invoke)
CapabilityResolver ← module catalogs (Core)   WinTools.Worker.Archive  System.IO.Compression / Formats.Tar
        ↓                                                              (+ SharpCompress, read-only)
JobScheduler (queue, per-job cancel, progress)WinTools.Worker.Docs     OpenXML SDK, Markdig, PDF backend
        ↓ line-JSON over stdin/stdout         WinTools.Worker.Text     subtitles, hashing, file info
Feedback (toast/info/sound)                   (each: job object, memory cap, timeout, kill on cancel)

Dialogs process (WPF, on demand): password, page ranges, Organize Pages, batch rename preview, subtitle offset.
Settings process (WPF, existing).
```

### Core, in its generic layer
- **Files:**
  - a `FileKind` registry: a stable id, a family (Image, Pdf, OfficeDoc, Text, Subtitle, Archive, Other), extensions and signature probes;
  - `IdentifyByName` at menu time and `IdentifyByContent` in the worker. A ZIP container is refined by `[Content_Types].xml` and `mimetype` entries.
  - `ImageFormat` stays, but only inside the image module.
- **Capabilities:**
  - `CapabilityDescriptor`: `Id`, `Label`, `Category` (a string plus an icon key), `Module`, input rule (kinds, minimum and maximum count, folders allowed, mixed allowed), `Shape` (PerFile | Selection), `Parameters` (None | Choice | Dialog:<kind>), `Produces` (File | Files | Folder | Facts | Clipboard), `LongRunning`.
  - The resolver composes the module catalogs and intersects as it does today.
  - Today's `Operation` becomes the image module's own payload behind an image capability.
- **Jobs:**
  - `JobRequest { Id, CapabilityId, Inputs[], OutputPolicy, ParametersJson, SecretsChannel }`.
  - `JobResult { Outputs[] (path, sha256, kind), Facts[] (label, value, notice), Error, Message }`.
  - `ErrorCode` grows with generic codes (PasswordRequired, WrongPassword, LimitExceeded, Cancelled, PartialOutputDiscarded) plus module codes.
- **Output:**
  - an `OutputPlan` for a single file, several files into a **staging folder**, or a folder;
  - a collision-safe commit for files *and* folders (rename the staging directory to the first free name);
  - Mark-of-the-Web propagation: if a source has ZoneId ≥ 3, every output gets `ZoneId=` only.
- **Settings:** schema 2, with per-module sections (`images`, `pdf`, `archive`, `naming`, `trigger`) and a migration from schema 1.
- **Feedback:** module-supplied verbs and nouns ("Merged 3 PDFs", "Extracted 214 files"); one summariser for all.
- **Safety:** `PathPolicy` (existing), plus an `ArchiveEntryPolicy` and a `LimitsPolicy` per module.

### Modules
Each module is a Core-side catalog (kinds, capabilities, feedback words) plus a worker-side executor. It exposes exactly the items the brief lists:
- supported inputs;
- operations;
- selection constraints;
- parameters;
- batch support;
- whether a dialog is needed;
- the execution contract;
- the outputs.

### Execution
- **One worker executable per engine family.** This keeps native dependencies, memory caps and crash domains apart. It also means the image worker is never touched by PDF or archive engines.
- **The protocol stays line-JSON.** It gains `progress` lines, a `cancel` message (with kill as the fallback) and stdin-only secrets.
- **Passwords are never written to a command line, a log or a file.** For qpdf they go through `--password-file=-` on its stdin.

### UI
- **The radial menu is unchanged as an interaction.** Its categories come from the resolver instead of an enum.
- **A ring holds at most 8 sectors.** Anything beyond goes to a "More…" sector that opens the dialog list.
- **Mixed selections** show only common capabilities (General: Info, SHA-256, Copy path, Rename…; Archive: Add to ZIP).
- **Dialogs** run in a separate on-demand WPF process, like Settings. WPF never enters the resident host.

---

## 6. Module boundaries

| Module | Owns | Shares from Core | Worker |
|---|---|---|---|
| Images | `ImageFormat`, `Operation`/`ResizeSpec`/`CompressLevel`, the pipeline, compression search, image facts | FileKind, capability, job, output, feedback, settings | `WinTools.Worker` (existing) |
| PDF | Page ranges, qpdf argument builder, PDFium bindings, render settings, the organizer model | Same, plus the dialogs | `WinTools.Worker.Pdf` |
| Documents | OOXML text and metadata, TXT/MD/CSV layout, optional external-converter adapter | Same | `WinTools.Worker.Docs` |
| Archives | Format readers and writers, `ArchiveEntryPolicy`, limits, Mark-of-the-Web | Same | `WinTools.Worker.Archive` |
| Subtitles | SRT/VTT/ASS parsers and writers, timing model, encoding detection | Same | `WinTools.Worker.Text` |
| General Files | File info, hashes, rename planner (preview, collisions) | Same; copy-path runs in the host (no parsing) | `WinTools.Worker.Text` (hash, info); rename commit in the dialog process |

---

## 7. Dependency and engine shortlist

Research done on 2026-10-23 from primary sources. Every adoption still needs a dependency-matrix entry, a licence review and a vulnerability scan (rules/dependencies.md).

| Need | Recommended | Version | Licence | Notes |
|---|---|---|---|---|
| PDF structure, security, linearize, repair, JSON info | **qpdf** (CLI, child of the PDF worker) | 12.4.2 (2026-09-26), msvc64 and arm64 builds | Apache-2.0; zlib, jpeg and OpenSSL 3 (Apache-2.0) | Covers merge, split, extract, delete, reorder, rotate, AES-256 encrypt, decrypt with the correct password, linearize, xref repair, object streams and `--optimize-images`. **Has no crop flag.** **Spikes:** `--show-crypto` (expect OpenSSL, or build with native crypto), and Procmon for no network and no child processes. |
| PDF render, text, images, thumbnails, crop box, Images→PDF | **PDFium, non-V8, non-XFA** (bblanchon/pdfium-binaries) | chromium/8111 (2026-10-19); win-x64 3.9 MB compressed; arm64 available | BSD-3 / Apache-2.0; FreeType (use the **FTL** option), libjpeg-turbo, OpenJPEG, lcms2, zlib, libpng, abseil, HarfBuzz, others (all notices required) | Our own thin P/Invoke layer. Docnet.Core is stale; PDFiumCore has no arm64. There is no JavaScript engine in this build. It cannot encrypt, which is qpdf's job. |
| OOXML text and metadata | **DocumentFormat.OpenXml** | 3.5.1 | MIT | **Spike:** DTD and billion-laughs rejection. Run the ZIP pre-scan (entry count, sizes, ratio) before opening. |
| Markdown | **Markdig** | 1.4.2 | BSD-2 | Raw HTML disabled. |
| TXT/MD/CSV → PDF backend | **Spike between:** WebView2 `PrintToPdfAsync` (OS component, no fee; network blocked, scripts off) **and** QuestPDF 2026.9.1 (Community licence: free under USD 1M revenue, then $1,999+) | — | — | Decided by Arabic shaping, bidi quality and isolation. Bundle **OFL fonts** (Noto Sans, Noto Naskh Arabic); Windows fonts may not be redistributed. |
| ZIP, TAR, TAR.GZ, GZIP | **.NET 10 built-ins** | runtime ≥ **10.0.9** (TAR symlink CVE-2026-45491) | MIT | Per-entry extraction through our policy. **Never `ExtractToDirectory` on untrusted input.** .NET 10 does not verify ZIP CRC on read: verify it ourselves. |
| Encrypted ZIP read (**PR 5.3: our own code on .NET's cryptography instead, ADR-0027**); 7z and RAR read | **SharpCompress** (read-only, entry streams) | 0.50.7 (2026-10-20) | MIT, with **unRAR-derived RAR code** (extract only, unRAR notice) | A steady advisory stream (zip-slip, symlink, LZMA2 loop, 7z allocation). Never `WriteToDirectory`; worker only, with a timeout. |
| Subtitle encoding | **In-house:** BOM, then strict UTF-8, then a 1256/1252 score, then user override; `CodePagesEncodingProvider` | .NET | MIT | UTF.Unknown is MPL tri-licence; not needed. |
| Subtitle parsing | **In-house** SRT, VTT and ASS parsers | — | — | SubtitlesParser is unmaintained; SubtitlesParserV2 is LGPL-3. |
| Optional DOCX/XLSX/PPTX → PDF | **The user's installed LibreOffice ≥ 26.2.5 or Microsoft Office**, detected, never bundled | — | User's licence | Private profile, macros off (`DisableMacrosExecution`; Office `AutomationSecurity=ForceDisable`), external links off, one process per job, watchdog. |

### Rejected
| Engine | Reason |
|---|---|
| PDFsharp ≥ 6.5 | Now AGPL or commercial. 6.2.4 is the last MIT release and gets no fixes. |
| MuPDF, Ghostscript, iText | AGPL |
| Poppler | GPL |
| OnlyOffice | AGPL |
| SubtitlesParserV2 | LGPL-3 |
| Squid-Box.SevenZipSharp | LGPL-3 and archived |
| Bundled LibreOffice | 358 MB, MSI needs admin, source obligation, trademark question, frequent parser CVEs (13+ in 2026, including a JDBC RCE) |
| Bundled 7-Zip or UnRAR.dll | Monthly CVE patching burden; not needed alongside SharpCompress |
| Aspose, Syncfusion, MESCIUS | Cost; revisit only with a cost ADR |
| Spire free edition | Truncates output |

---

## 8. Licensing risks and controls
1. **Silent relicensing.** PDFsharp switched to AGPL in a minor release. To control this:
   - pin exact versions in `Directory.Packages.props`;
   - record the SPDX expression of each package in the matrix;
   - add a CI check that fails if any package's licence expression changes or matches `GPL|AGPL|SSPL|BUSL`.
2. **Notices.** PDFium's third-party tree, qpdf's NOTICE, SharpCompress's unRAR clause, OFL fonts, OpenSSL (Apache-2.0) and SkiaSharp's existing notices all go into one `THIRD-PARTY-NOTICES.txt` shipped with the installer. A test checks the file lists every bundled native binary.
3. **FreeType.** Choose the FTL option, which carries a credit clause.
4. **QuestPDF**, only if chosen: needs an eligibility ADR on revenue and entity type. Default to WebView2 if it passes the spike.
5. **RAR.** Extraction only; creating RAR is not possible legally.
6. **External Office engines.** We never redistribute them. The user's own installed copy is used under the user's licence, labelled "uses your installed …".

---

## 9. V2 feature matrix

| Module | Required | Optional if low-risk | Deferred or rejected |
|---|---|---|---|
| **Images** | Everything in V1, unchanged; Stage 0 compression semantics; Mark-of-the-Web propagation | — | SVG and HEIC (ADR-0012) |
| **PDF** | Merge; split (every page / ranges); extract pages; delete pages; reorder and rotate (Organize Pages window); PDF→PNG/JPEG; Images→PDF; extract embedded images; extract existing text; PDF info; password protect (AES-256); remove protection with the correct password; metadata inspect and remove; **lossless optimize** (object streams, recompressed flate, unused resources); linearize; repair (qpdf reconstruction, reported as best effort); thumbnails (organizer); batch where logical (info, metadata, optimize, protect, PDF→images) | Crop (uniform margins in the organizer, through PDFium); compress with image downsampling (only if the spike's fidelity gate passes) | OCR, text editing, signatures, redaction, forms and XFA (separate approval) |
| **Documents** | DOCX/XLSX/PPTX text extraction; document properties inspect and remove (honest scope: core, app and custom properties only); TXT, Markdown and CSV → PDF; batch | DOCX/XLSX/PPTX → PDF via the user's installed LibreOffice or Office | A bundled Office engine; commercial engines |
| **Archives** | ZIP create (files or folder); ZIP extract (Here / To folder); TAR, TAR.GZ and GZIP create and extract; archive info; integrity test (our own CRC); encrypted ZIP **extract** with password; batch extraction; Mark-of-the-Web propagation | 7z and RAR **extract** (SharpCompress, unRAR notice) | Encrypted ZIP **create** (wait for .NET 11 or later, or an audited library); 7z create; RAR create (rejected) |
| **Subtitles** | SRT↔VTT; shift forward or back by a custom offset; re-encode to UTF-8; normalise BOM and line endings; info; malformed-timing detection (report, don't guess); batch | ASS/SSA → SRT (tags stripped, documented loss); ASS shift | Rendering ASS styles |
| **General Files** | File info (real type from content); SHA-256; copy name, path or folder; batch rename (prefix, suffix, numbering, search/replace, spaces↔underscores, case, safe cleanup) with preview, collision and invalid-name checks | MD5, labelled "for comparison only" | Delete, move, extension rewriting that changes meaning |
| **Settings** | Trigger keys extended (Space and F1–F12, with F1 refused if it conflicts with help); modifier policy (§10, Stage 0); conflict detection; Reset to default; module defaults (PDF render DPI, archive limits, naming and collision style); Start with Windows; sounds; animations; output folder | Letter keys A–Z, only if the typing-interference spike passes | Themes |

---

## 10. PR-by-PR roadmap

Every PR must:
- have a narrow scope and its own regression tests;
- build with 0 warnings and pass CI;
- pass an independent evaluator with no blocker or major finding;
- re-run the Images regression suite, and the Explorer E2E where it touches interaction.

| PR | Stage | Scope | Depends on |
|---|---|---|---|
| **0.1** | Stage 0 | **Trigger modifiers:** Ctrl and Shift accepted (our target answers COPY only, so they cannot cause MOVE or LINK); Alt+Space refused with a toast ("Release Alt, then press Space") and the "nothing" sound; ADR-0003 amended | — |
| **0.2** | Stage 0 | **Data-object recovery:** retain the drag's `IDataObject` (OLE allows it during the drag, released on Drop and DragLeave); bounded retry on DragOver (at most 20 reads, about 2 s); the ring shows *Waiting*, not *No actions*, while pending; the drop falls back to the session's paths; diagnostics `drag.data.unavailable`, `.retry`, `.recovered` (HRESULT category, file count only) | — |
| **0.3** | Stage 0 | **Compression semantics** (ADR-0017): a bounded candidate ladder per preset with the SSIM floors 0.990, 0.980 and 0.960, a minimum quality per preset, 4:4:4 kept by High, an 8 % minimum saving or "Already optimized", and "saved X%" feedback; PNG stays lossless; the corpus becomes the permanent test | — |
| **0.4** | Stage 0 | **Release regression matrix in the harness:** see §11; installed-artifact runner | 0.1, 0.2 |
| **1.1** | Stage 1 | **FileKind registry and capability descriptors.** Images are adapted with **no behaviour change**: the menu, ids and outputs stay byte-identical, proven by golden tests | 0.x |
| **1.2** | Stage 1 | **Job contract v2:** multiple inputs and outputs, staging-folder commit, parameters, progress, cancellation, a scheduler (a queue instead of the global gate), per-module limits; Mark-of-the-Web propagation | 1.1 |
| **1.3** | Stage 1 | **Data-driven radial categories**, the "More…" policy, folder inputs (UX-009), the dialogs-process skeleton | 1.1 |
| **1.4** | Stage 1 | **First non-image module as a walking skeleton:** General Files (info, SHA-256, copy path), proving cross-module resolution end to end | 1.2, 1.3 |
| **1.5** | Stage 1 | **Settings schema 2** with migration, the extended trigger keys, conflict detection, Reset | 0.1 |
| **2.1** | Stage 2 | PDF worker plus qpdf: info, merge, split, extract, delete, rotate | 1.x |
| **2.2** | Stage 2 | PDF security and metadata: protect, unprotect, metadata; the password dialog and secret channel | 2.1 |
| **2.3** | Stage 2 | PDF lossless optimize, linearize, repair | 2.1 |
| **3.1** | Stage 3 | PDFium: PDF→PNG/JPEG, text extraction, image extraction | 2.1 |
| **3.2** | Stage 3 | Images→PDF | 3.1 |
| **4.1** | Stage 4 | Organize Pages window (thumbnails, reorder, rotate, delete, extract) | 3.1 |
| **4.2** | Stage 4 | Crop, and the compression spike result (downsampling, or deferral recorded) | 4.1 |
| **5.x** | Stage 5 | **Archives** (moved before Documents, see below): ZIP and TAR family create and extract, info, test, encrypted-ZIP extract; then optional 7z and RAR extract | 1.x |
| **6.x** | Stage 6 | **Documents:** the backend spike ADR, OOXML text and metadata, TXT/MD/CSV → PDF; then the optional external-converter adapter | 1.x, 3.x |
| **7.1** | Stage 7 | Subtitles | 1.x |
| **7.2** | Stage 7 | Batch rename dialog, MD5 option | 1.4 |
| **8.x** | Stage 8 | Cross-module hardening: fuzzing per parser, soak, the full security review, a performance pass, the notices file, the licence CI gate | all |
| **9.x** | Stage 9 | V2 acceptance gate, installer 0.2.0 Beta, the release qualification run | 8.x |

### Deviations from the suggested order, with reasons
1. **Stage 0 is split into four PRs**, so each owner-found defect closes on its own evidence.
2. **General Files moves to Stage 1 (1.4) as the walking skeleton.** It is the cheapest real second module, and it exercises mixed selections, per-module workers and data-driven menus before PDF adds engine risk. Batch rename stays in Stage 7, because it needs the dialog.
3. **Archives before Documents.**
   - Archives use built-in .NET plus one MIT reader, with no engine or licence decision pending, and have the higher everyday value.
   - Documents depend on a backend spike (Arabic) and on the optional external-engine design, so they are better done later with that evidence.

---

## 11. Test strategy

**Kept from V1:** unit tests, integration tests through the real worker, fault injection (worker crash, hang, memory), the Explorer E2E, soak runs, installed-artifact checks. **New in V2:**

### Release regression matrix (harness, PR 0.4; then grown per stage)
| Area | Cases |
|---|---|
| **First use** | First operation after a fresh host; first multi-selection after a fresh host; first operation after an Explorer restart (the harness restarts its own Explorer window process only when the owner allows); after sign-in with Start with Windows (manual protocol) |
| **Multi-selection** | 2, 10 and 100 files × {Ctrl-click, Ctrl+A, Shift-range, rubber band (via UI Automation selection where reliable)} |
| **Timing** | Drag immediately after selecting (no settle delay); repeated in the same session; cold and warm |
| **Modifiers** | Ctrl held through Drag → Space; Ctrl released; Shift held; Alt held (expects the refusal toast and nothing to run) |
| **Data object** (unit, fake `IDataObject`) | Read immediately; first read fails then succeeds; empty then recovered on DragOver; permanent failure stays safe; malformed DROPFILES; drop-time read fails after a good DragEnter; references released on Drop and DragLeave |
| **Paths** | Spaces, Arabic, emoji, near-260 and longer than 260 supported paths, reserved and problematic names where they can be created safely |
| **Installed artifact** | The critical subset runs against `%LOCALAPPDATA%\Programs\WinTools` (`--host`), on every release candidate |

### Corpus per module
Generated where possible; otherwise sources with a licence record (`tests/corpus/README.md`):
- **Images:** the Stage 0 corpus.
- **PDF:** text, scans, mixed content, encrypted (RC4 and AES-256), linearized, damaged xref, huge page dimensions, many pages, embedded files and JavaScript (must be ignored).
- **Office:** DOCX, XLSX and PPTX from LibreOffice and Office samples we create; macros, external links and malformed ZIP or XML.
- **Archives:** a generated malicious set covering traversal, absolute and device paths, links, bombs (ratio and count), nested archives, collisions, reserved names, truncation, wrong passwords.
- **Subtitles:** UTF-8, UTF-16, 1256 and 1252; BOM variations; CRLF and LF; overlapping or negative timings; Arabic and emoji.

### Other test types
- **Fuzzing:** a SharpFuzz-style harness or a seed-mutation runner (offline, time-boxed in CI) for every in-house parser (SRT, VTT, ASS, archive policy, identification) and for the worker entry points.
- **Security tests:** one test per threat-model item (§14). "Escape the output folder" is tested in every module.
- **Performance:** §13 thresholds, a local performance gate per stage, compared with a committed baseline.
- **Owner bugs:** each becomes a named regression test that has been shown to fail on the old code (the policy from 2026-10-23).

---

## 12. Migration strategy (no rewrite)
1. **Unchanged:**
   - interaction (hooks, session, navigator, geometry, overlay rendering, feedback surfaces);
   - worker isolation (job object, lanes, kill);
   - output safety (temp, validate, no-overwrite commit, host hash check);
   - `PathPolicy`, the settings store, signals, the tray.
2. **Becomes generic:**
   - file identification (`ImageFormat` → `FileKind` at the generic layer);
   - the capability descriptor;
   - the job request and result;
   - the output plan (multi-file and folder);
   - the error codes (generic plus module);
   - facts (`ImageInfo` → rows);
   - feedback wording;
   - the menu categories.
3. **Stays image-specific:** `ImageFormat`, `Operation` with `ResizeSpec`/`CompressLevel`, the pipeline, compression search, image limits (50 MP), WIC and SkiaSharp.
4. **How:** an **adapter first**.
   - PR 1.1 maps the existing image catalog onto the generic descriptors with no change to ids, labels, order, outputs or names.
   - Golden tests compare the menu tree for every format and mixed selection, and the output file names, before and after.
   - The image worker keeps its protocol. The host maps the generic `JobRequest` onto today's `ConversionRequest` for the image worker until 1.2 lands. Then the image worker reads v2 requests, while v1 requests stay accepted for one release.
5. **One module at a time.** Each module adds its catalog, worker and dialogs without editing another module. The resolver only composes.
6. **Proof of no regression:**
   - the full image test suite (unit, worker and UI surface tests);
   - the 22-scenario Explorer suite;
   - the soak run and the latency gate on every Stage 1 PR;
   - the installed-artifact subset at every stage end.

---

## 13. Performance and resource strategy

**Baseline:** the UI phase measurements on the development PC (`docs/performance/2026-10-21-ui.md`).

| Metric | V1 baseline | V2 budget | Regression alarm |
|---|---|---|---|
| Idle CPU (60 s, after 2 min) | 0.000 % | ≤ 0.1 % | > 0.02 % |
| Idle host private bytes | 9.2 MB | ≤ 20 MB | > +50 % vs baseline |
| Idle working set | 39.7 MB | ≤ 60 MB | > +25 % |
| Warm trigger → overlay, p95 | 9.1 ms | ≤ 50 ms (budget) | > 2× baseline |
| First activation, max | 14.8 ms | ≤ 120 ms | > 40 ms |
| Start to tray-ready | 226–337 ms | < 500 ms | > 400 ms |
| Worker processes while idle | 0 | 0 | any |
| Worker exit after a batch | — | ≤ 2 s | > 5 s |
| Installer size | 49.4 MB | ≤ 90 MB with PDF and archives | > +20 MB per stage |

**Rules:**
- No engine, DLL or font loads in the resident host. A test asserts the host's referenced assemblies and loaded modules.
- Workers start per job and exit after the batch.
- The scheduler bounds concurrency per module: images as today (2–4, by commit); PDF render 2; archives 1 per archive; documents 1.
- Memory caps per worker: images 1.5 GB (ADR-0015); PDF 1.5 GB; archives 512 MB (streaming); docs 1 GB; text 256 MB.
- Timeouts per item and per module, plus cancellation (cooperative message, then kill).
- Progress for anything flagged long-running.

**Detection:**
- The CI build checks referenced assemblies and the notices file.
- A local `perf-gate` script (committed under `scripts/`): idle, startup, the overlay latency set from the Explorer suite, a batch memory soak, and worker-leftover detection.
- It writes jsonl next to the baseline and fails on any alarm.
- It runs on every Stage 1 PR and at every stage end.

---

## 14. V2 threat-model additions (append to `docs/security/threat-model.md`)

### Common to all modules
- **Every output inherits** a Mark-of-the-Web ZoneId ≥ 3 from its source; if the mark can't be written, the job fails (archives).
- **No engine may:** run macros or scripts, fetch remote resources, launch embedded content, open URLs, or execute extracted files.
- **Workers run** with no network use expected. The PDF worker's qpdf child gets an `ArgumentList` only and secrets on stdin.
- **Content verdicts** never come from the extension.
- **Partial output** is always discarded on failure or cancel.
- **Passwords** are never logged, never on a command line, never persisted, and are zeroed after use where the API allows.

### PDF
- **Parser attacks:** malformed objects, xref loops, filter and decompression bombs (FlateDecode with a declared length versus an actual one), huge page sizes (render cap: pixels per page, as ADR-0015), font and image codec bugs (PDFium CVE cadence: rebuild monthly).
- **Contained:** worker isolation, the memory cap, a timeout per page and per job.
- **JavaScript:** none (the non-V8 build).
- **Embedded files and external references:** never extracted automatically; only listed in info.
- **Encrypted content:** opened only with the user's password.
- **Never** `--remove-restrictions`, because it drops signatures.

### Documents
- **Macros:** never run. OpenXML reads parts only. An external engine runs with macros disabled and a private profile.
- **External links and remote resources:** blocked. LibreOffice CVE-2026-63277 (JDBC RCE) is the reason to require ≥ 26.2.5 and to disable data sources.
- **ZIP and XML bombs:** pre-scan the package (entry count, ratio, size); DTD prohibited; `MaxCharactersInPart`.
- **OOXML relationships** with targets outside the package are never followed.

### Archives
- **Path traversal:** `..`, rooted, drive-relative, UNC and NT device prefixes are rejected. After normalising, a path must stay under the staging folder.
- **Links:** TAR symlinks and hard links, and ZIP Unix symlink modes, are skipped and reported. A reparse point anywhere in the destination path aborts.
- **Special entries:** device, FIFO and character entries are skipped.
- **Bombs:** limits on entries (default 100,000), total bytes (default 20 GB, or free space minus a reserve), per-entry size and ratio (default 200:1, after 1 MB). Bytes are counted as they decompress, not as declared.
- **Nesting:** never extracted automatically (depth 0).
- **Names:** reserved Windows names, trailing dots and spaces, `:` and ADS, control characters, case and NFC/NFD collisions are renamed deterministically and reported. Components over 255 characters fail.
- **Commit:** extract into a staging folder inside the destination, then do a single directory rename to a free name. Cancel or crash leaves only the staging folder, which the next run cleans (marker file plus age).
- **The source archive** is never modified.

---

## 15. Definition of Done for V2

V2 (0.2.0 Beta) is complete only when **all** of the following hold.

**Defects and scope**
1. The Stage 0 owner-found defects are fixed, each with a permanent regression test shown failing on the old code and passing on the new.
2. The complete Images regression suite passes (unit, worker, UI surface and the 22-scenario Explorer suite).
3. Every **Required** item of §9 works, for every module, through the radial menu or its dialog.

**Behaviour**

4. Cross-module resolution is correct for single, mixed and folder selections (golden menu tests).
5. Batch is validated per module: counts, partial failure, summary.
6. Worker isolation holds for every module: a crash, hang or memory overrun in one job never affects the host or other jobs.
7. Cancellation works for every long-running operation, leaving no partial output.
8. Corrupt or malicious content fails safely, as the threat-model tests show. The source is never modified (hash checked in every test).
9. The realistic corpus passes for every module.

**Real-world verification**

10. The first-use, cold-start and multi-selection matrix (§11) passes in real Explorer.
11. The installed-artifact tests pass, including install, upgrade, uninstall and reinstall from an unrelated folder.
12. The performance budgets in §13 hold, with no alarm.

**Review and release**

13. The security review has no blocker or major finding.
14. The dependency and licence review is complete: matrix, notices file and the licence CI gate.
15. CI is green, and the final independent evaluator approves.
16. The final Setup is tested from a clean location, with its SHA-256 recorded.
17. Not claimed: Microsoft Store readiness (a separate milestone).

---

## 16–17. Product-owner decisions genuinely required, with recommended defaults

| # | Decision | Recommended default | Why |
|---|---|---|---|
| D1 | Office → PDF (DOCX/XLSX/PPTX) | **Optional, only through the user's installed LibreOffice or Office**, clearly labelled; no bundled engine in V2 | Bundling is 358 MB, needs admin for the MSI, carries a source obligation and a trademark question, and LibreOffice parser CVEs keep coming. Commercial engines cost $2k–$3.5k per developer per product. |
| D2 | Backend for TXT/MD/CSV → PDF | **WebView2** if the Arabic, isolation and offline spike passes; **QuestPDF** otherwise, with a licence-eligibility ADR | Both handle bidi and shaping. WebView2 has no fee and ships with Windows 11. QuestPDF is free only under $1M revenue. |
| D3 | 7z and RAR extraction | **Include as optional (extract only)** through SharpCompress, gated by the fuzzing and security tests | Common user need; MIT, extract-only RAR is lawful with the unRAR notice. |
| D4 | Encrypted ZIP creation | **Defer.** Offer AES-256 ZIP when .NET 11 (or a vetted library) is adopted; never offer ZipCrypto | .NET 10 cannot do it; SharpZipLib is dormant. |
| D5 | Mark-of-the-Web on outputs | **Always on, not configurable** (all modules) | Security baseline; Explorer and WinRAR propagate it. |
| D6 | PDF compression depth in V2 | **Lossless optimize** required; image downsampling only if its spike passes the fidelity gate | No permissive engine downsamples out of the box; Ghostscript is AGPL. |
| D7 | Modifier policy for the trigger | **Ctrl and Shift accepted; Alt refused with a toast** | Our drop target answers COPY only, so a held Ctrl or Shift cannot make WinTools move or link. Alt+Space is the window-menu shortcut. |
| D8 | Extra trigger keys | **Space and F2–F12** in V2; A–Z only if the typing-interference spike passes; F1 refused | Letters reach Explorer's type-ahead during a drag; that is unproven. |
| D9 | Windows baseline (ADR-0002) | **Windows 11 x64**, plus **Windows 10 22H2 x64** tested in a VM before release | Windows 10 is still widely used; WebView2 and .NET 10 support it. |
| D10 | Engine servicing cadence | **Rebuild and re-test monthly during the beta** (.NET runtime, PDFium, qpdf, SharpCompress); no auto-update in V2 | The native engines publish fixes on a weekly to monthly cadence. |
| D11 | MD5 | **Include, labelled "for comparison only"** | Compatibility with download pages. |
| D12 | Compression semantics (ADR-0017) | **Floors 0.990, 0.980 and 0.960; 8 % minimum saving; High keeps 4:4:4** | Measured on the corpus (§3); see PR 0.3. |
| D13 | Archive limits | **100,000 entries; 20 GB total or free space minus 2 GB; 200:1 ratio after 1 MB; nesting depth 0**; adjustable in Settings within safe bounds | Common defaults in security-conscious extractors. |
| D14 | Uninstall: remove user data | **Keep** settings and logs (as today); add a "remove my settings" option in Settings later | Data-preservation default. |

**Out of scope until separately approved:** OCR, text editing, signatures, redaction, forms and XFA, themes, Store packaging, video and audio (V3).
