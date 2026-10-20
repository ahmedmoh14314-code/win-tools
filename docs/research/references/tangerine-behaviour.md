# Tangerine (macOS) — public behavioural inventory

| | |
|---|---|
| **Product** | Tangerine: File Converter ("Tangerine for Mac") |
| **Vendor** | Leap Studio (maker: "thameem", X handle @th2049) |
| **Official URLs** | Site <https://tangerineformac.com/> · Press kit <https://tangerineformac.com/press> · Privacy <https://tangerineformac.com/privacy> · Mac App Store <https://apps.apple.com/us/app/tangerine-file-converter/id6813034065?mt=12> |
| **Secondary URLs** | Product Hunt <https://www.producthunt.com/products/tangerine-zero-click-file-converter> (+ `/reviews`) · MacGeneration review (FR, 2026-10-02) <https://www.macg.co/logiciels/2026/10/tangerine-un-convertisseur-de-fichiers-toujours-portee-de-main-sur-mac-311381> · Launch video entry <https://whatships.com/videos/tangerine/> (X post <https://x.com/th2049/status/2097015553508725184>) · Directory listings: chatgate.ai/post/tangerine, productcool.com/product/tangerine-4, visalytica.com/tool/tangerine |
| **Versions seen** | 1.4 (press kit, App Store 24 Sep 2026) → 2.0 → 2.0.1 (App Store, mid-Oct 2026) |
| **Date of research** | 2026-10-20 |
| **Scope** | **Public behaviour only.** No download, no binary inspection, no decompilation. Everything below comes from the vendor's website/press kit/App Store text, public screenshots and the launch video frame, and third-party reviews/listings. Nothing here describes implementation internals. |

## 0. Identification — which "Tangerine"?

Several unrelated products share the name: an old Mac BPM analyser (Potion Factory, 2006), a Canadian bank, a music album, and an "AI zero-click converter" CLI described on aiindigo.com (installs via `curl`, tray icon, watches folders — **not** a radial menu, almost certainly a content-farm fabrication). The right product is the one that matches the brief's description — *drag a file in Finder, press a key, a wheel of actions appears at the cursor, drop on an action* — and is identified by: the vendor site tangerineformac.com, the Mac App Store id **6813034065** (seller LEAP STUDIO), the press-kit screenshots showing a radial wheel around a dragged Finder file, and the French MacGeneration review describing "une petite roue" on Shift-drag. All sources cross-reference each other (site ↔ App Store ↔ Product Hunt ↔ X post), so this is one product.

Evidence levels used in the table: **D** = documented by the vendor (site, press kit, App Store text, release notes); **R** = reported by a third-party reviewer/user; **M** = observed in public media (press-kit screenshots, Product Hunt gallery, launch-video frame); **I** = inferred by me (flagged, not fact).

## 1. Inventory table

| # | Topic | What Tangerine does (public evidence) | Level | Source |
|---|---|---|---|---|
| 1.1 | Activation while dragging | Start an ordinary Finder drag, then **tap** a modifier key. "Start dragging, then tap a key." The key is tapped, not held: "Release the key and keep dragging; the wheel stays open until you drop or cancel." Older text (App Store, PH, v1.x) says "**hold** Shift while dragging". | D | site home; App Store description; PH |
| 1.2 | Trigger keys (current, v2.x) | **Shift** → conversion wheel; **Command** → file-tools wheel; **Control** → file-actions wheel. "Change the keys in Settings." | D | site home; press kit; App Store 2.0 notes ("drag files and tap Control") |
| 1.3 | Trigger keys (older, v1.x) | Shift → formats; **Option+Shift** (App Store, MacG) or "Option" (PH, listings) → tools. Screenshot caption: "Hold shift + option then drag the file." The modifier for tools changed between 1.x and 2.x; sources disagree because they were written at different times. | D/M | App Store desc; MacG; PH; press screenshot `advanced-tools.png` |
| 1.4 | Visual on activation | A translucent ring of 8–9 equal wedge segments appears **centred on the pointer**, overlapping the Finder window and the desktop (topmost). The hub shows the dragged file's thumbnail and its size ("2.2 MB", "830 KB"). Finder's own drag image and filename badge remain visible on top. In the video frame the hub shows the **name of the highlighted segment** ("REDACT") as a label. | M | press `conversion-wheel.png`, `advanced-tools.png`; PH gallery image 1 (video frame) |
| 1.5 | Selection and release | Move the dragged file over a wedge and **drop** it: "Drop the file on the format you want, and the converted copy is saved right beside the original." MacG: dropping on a format converts "à la volée". Keyboard alternative: arrow keys to choose, Enter to apply, Escape to cancel. | D/R | App Store desc; site; MacG |
| 1.6 | Cancellation of the wheel | Escape cancels. Dropping outside the wheel presumably performs the normal Finder drop (I); not documented. | D / I | site |
| 1.7 | Keyboard-only path | "Press Enter on a file to open the format menu, then use Command or Control to switch wheels." Arrow keys / Enter / Escape. | D | site |
| 1.8 | Touch | "On a touch screen, drag to move or hold a file briefly before dragging to convert." (iPad-style wording; shown on the simulated web demo.) | D | site |
| 1.9 | Hierarchy / nesting | **No nested wheels observed or documented.** Hierarchy is flat: three *separate* top-level wheels (Convert / Tools / Actions) selected by which modifier you tap; you "switch wheels with Command or Control" while the wheel is open. Each wheel is a single ring of ≤9 wedges. No "back" navigation exists because there is no depth. | M/D | screenshots; site |
| 1.10 | Groups inside a wheel | None visible. The conversion wheel for a PNG shows 8 targets side by side (JPG, WEBP, HEIC, TIFF, AVIF, BMP, PDF, DOCX). How the app handles formats beyond ~9 wedges (e.g. 275 "conversion options") is **not** documented — possibly per-type pruning (I). | M / I | `conversion-wheel.png` |
| 1.11 | Context dependence | Wheel contents depend on the file type: "the wheel fills with tools for that exact kind of file"; MacG: formats "s'adaptent au type de fichier". Image tools wheel: Compress, Metadata, Edit, Annotate, Add BG, Crop, Redact (+1 hidden). Video tools wheel: Compress, Metadata, Mute, Trim, Crop, Speed, Snapshot, Split, Redact. | D/M | App Store desc; MacG; screenshots |
| 1.12 | Single vs multiple files | "Drag several compatible files at once to convert or process them in one go." Batch is explicitly supported for *compatible* files. 2.0 adds "percentage-based compression for single files and batches." | D | App Store desc; 2.0 notes |
| 1.13 | Mixed types / folders | Conversion/tools: not documented for mixed selections (I: probably rejected or reduced to the common set). **File-actions wheel** (Control) explicitly "works with folders, mixed selections, and files that have no conversion tools." | D / I | site; press kit |
| 1.14 | Batch progress / result | "Each job opens a small progress window and reveals the finished files in Finder." One job per drop; a batch is one job. | D | App Store desc |
| 1.15 | Conversion categories | Images: JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP (+PDF export; SVG input only; GIF). Video: MP4, MOV, MKV, WebM, AVI, WMV, GIF (+MP3 extract). Audio: MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA. PDF → DOCX, Pages, PPTX, XLSX, JPG, PNG, EPUB, TXT, MD ("all pages; images at 300 DPI"). Documents: DOCX, Pages, MD, TXT, EPUB (RTF/RTFD in; HTML out). Slides: PPT/PPTX → PDF/JPG/PNG. Spreadsheets: XLSX, ODS, CSV, TSV. Subtitles: SRT, VTT → TXT. Archives: ZIP, TAR, GZIP (RAR input only). Marketing count: "275 conversion options" (site, v2) / "188" (v1 listings). | D | site "Supported formats"; App Store |
| 1.16 | Tools (second wheel) | "26 tools" / "25 advanced tools": compress (incl. **target file size** and percentage), crop, resize, trim, split, merge, metadata view/edit/remove, redact, annotate, add background, image→PDF, collage, QR read, video snapshot/mute/speed, audio normalise/bleep/channels. | D | site; App Store; MacG |
| 1.17 | Tools open a window | Tools are **not** zero-click: a drop on a tool opens a parameter window (Edit Photo with sliders, Add BG, Trim Audio, Convert Audio Channels, Bleep) with Reset / Apply / "Save Bleeped Copy". MacG: quality and other parameters are adjustable. | M/R | press `edit-photo.png`, `add-background.png`, `audio-tools.png`; MacG |
| 1.18 | File actions (third wheel, v2.0) | AirDrop, Float, Copy, Move, Copy Path, Terminal. "File actions do not use your conversion allowance." | D | site; 2.0 notes |
| 1.19 | "Float" | A 2.0 "floating-file feature to make drag and drop easier" — presumably a floating shelf for the dragged file, Yoink/Dropover-style (I). Not described further. | D / I | 2.0 notes |
| 1.20 | Progress feedback | "A small progress window" per job. Nothing documented about percentage, ETA, or per-file status within a batch. | D | App Store desc |
| 1.21 | Cancellation of a running job | **Not documented anywhere public.** | — | — |
| 1.22 | Output location | "The converted copy is saved right beside the original." Privacy page: "results are saved beside the original file." Originals are never modified ("converts files in place" in the App Store subtitle is marketing; the body text says copy beside original). | D | App Store; privacy page; press kit |
| 1.23 | Output naming / collisions | **Not documented.** (I: same base name, new extension; collision policy unknown.) | — / I | — |
| 1.24 | Notifications | Not documented. Result presentation is the progress window plus "reveals the finished files in Finder". | D | App Store desc |
| 1.25 | Reveal in Finder | Yes, automatic after each job. | D | App Store desc |
| 1.26 | Settings / customisation | Trigger keys are changeable ("Change the keys in Settings"; MacG: "Les raccourcis sont personnalisables"). No documented customisation of wheel contents, order, or presets. A reviewer explicitly **requested** presets ("web-optimized image"). | D/R | site; MacG; PH review |
| 1.27 | Where the app lives | Menu-bar app: "lives in your menu bar and stays out of the way until the next drag." No Dock presence implied. | D | App Store desc |
| 1.28 | Error handling | **Nothing public.** No documented behaviour for unsupported files, failed conversions, or partial batches. | — | — |
| 1.29 | Permissions / privacy | Local processing only; "no analytics, advertising, or tracking"; the only stored data is "macOS folder-access permission". (I: implies the app asks for folder access to write beside the original — sandboxed App Store app.) | D / I | privacy page |
| 1.30 | Requirements | macOS 13+, Apple silicon only (M1 or later). 97.6 MB download. | D | App Store |
| 1.31 | Pricing (context only) | Free for the first **20 files** ("conversion allowance"), then one-time IAP "Tangerine Full Unlock" **US$29.99** (€17 per MacG; $15 promo per chatgate). No subscription. | D/R | App Store; MacG; chatgate |
| 1.32 | Timeline | Launch video 7 Sep 2026 (X, ~120k views); PH launch mid-Sep/Oct 2026 (96 upvotes, 1 review); v2.0 ≈ 15 Oct 2026. | D/R | whatships; PH; App Store |
| 1.33 | Documented limitations | Apple silicon only; 20-file free cap; PDF→EPUB missing in 1.x (added 2.0); 2.0.1 and 1.4 both fix "some conversion options hidden" (wheel population bugs); 2.0 fixes "lag in wheel highlighting after idle" and "UI issues on older macOS versions". | D/R | App Store release notes & reviews |
| 1.34 | Criticisms | MacG: "quelques micro-lags … au moment d'ouvrir le menu" on a MacBook Air M1. PH review: tools wheel "dense on first use", needs onboarding; wants presets. X replies: "$30 too much" (search snippet; thread not retrievable). No Reddit or Hacker News discussion found (HN Algolia: 0 hits). | R | MacG; PH reviews; search snippet |
| 1.35 | Finder modifier interference | **Observed, not documented:** in the launch-video frame (Option+Shift tools wheel, v1.x) the drag image carries Finder's green **"+" copy badge**, i.e. the app's trigger coincided with Finder's native Option = copy modifier. In 2.x the tools key moved to Command (Finder: force-move) and Control was added. No public source discusses whether a drop on the wheel suppresses the Finder copy/move. | M / I | PH gallery image 1 |

## 2. What Tangerine appears to do (facts with sources)

**Interaction flow.** The user starts a normal Finder drag, then taps a modifier key. A single-ring radial wheel appears centred on the pointer, with the dragged file's thumbnail and size in the hub ([site](https://tangerineformac.com/), press screenshots). The key can be released; the wheel persists until the file is dropped or Escape is pressed ([site](https://tangerineformac.com/)). The wedge under the pointer is highlighted and its name is shown in the hub (video frame). Dropping on a wedge starts the job: a small progress window opens, the result is written **as a new file beside the original**, and the finished files are revealed in Finder ([App Store](https://apps.apple.com/us/app/tangerine-file-converter/id6813034065?mt=12)). In 1.x the wording was "hold Shift while dragging"; 2.x changed it to "tap".

**Three wheels, no nesting.** Which wheel appears is decided by which key is tapped: Shift = output formats, Command (formerly Option+Shift) = file-type-specific tools, Control (new in 2.0) = generic file actions (AirDrop, Float, Copy, Move, Copy Path, Terminal). You can switch wheels while one is open. There is no sub-wheel, no group, and no back navigation in any public material. Each observed wheel has 8–9 wedges with uppercase labels and (on the tools wheel) a glyph.

**Context dependence.** Wheel contents are computed per file type: PNG → JPG/WEBP/HEIC/TIFF/AVIF/BMP/PDF/DOCX; image tools → Compress/Metadata/Edit/Annotate/Add BG/Crop/Redact; video tools → Compress/Metadata/Mute/Trim/Crop/Speed/Snapshot/Split/Redact. The file-actions wheel is the only one that accepts folders and mixed selections ([press kit](https://tangerineformac.com/press)).

**Zero-click is only true for conversions.** Tools open a parameter window (sliders, waveform trimmer, background picker) with Reset/Apply; MacG confirms quality and other parameters are adjustable. Compress supports a target file size and, since 2.0, a percentage for single files and batches.

**Batch.** Several *compatible* files dragged together are processed as one job with one progress window ([App Store](https://apps.apple.com/us/app/tangerine-file-converter/id6813034065?mt=12)). Nothing public about mixed-type selections on the conversion wheel, per-file failure in a batch, or limits.

**Settings.** Trigger keys are configurable ("Change the keys in Settings"; [MacG](https://www.macg.co/logiciels/2026/10/tangerine-un-convertisseur-de-fichiers-toujours-portee-de-main-sur-mac-311381)). No public evidence of wheel customisation, action ordering, or presets; a reviewer asked for presets ([PH review](https://www.producthunt.com/products/tangerine-zero-click-file-converter/reviews)).

**Not publicly documented at all:** job cancellation, timeouts, output naming/collision policy, error messages, unsupported-file behaviour, notifications (beyond the progress window and Finder reveal), multi-monitor/edge handling, and what happens when the wheel overflows (275 "options" vs ≤9 wedges).

**Known weak spots.** Micro-lag on opening the wheel on an M1 Air (MacG); wheel-highlight lag after idle (fixed in 2.0 notes); conversion options sometimes hidden (fixed twice: 1.4 and 2.0.1); tools wheel "dense on first use" (PH); Apple-silicon-only; price pushback. Overall footprint of discussion is small: no HN or Reddit threads, 1 PH review, 3 App Store reviews.

**Product context.** Free for 20 files, then US$29.99 one-time IAP; menu-bar app; macOS 13+, Apple silicon; ~98 MB; vendor Leap Studio; launched Sep 2026, v2.0 Oct 2026.

## 3. How we should implement it on Windows (recommendations — my reasoning, not Tangerine facts)

Everything in this section is a **recommendation** for win-tools and should be validated by the interaction spike (ADR-0003 and the spike plan), not adopted on my say-so.

### 3.1 Keep (the parts that are platform-neutral and clearly work)

1. **Tap-to-open, wheel-persists-until-drop.** Tangerine's 2.x change from "hold" to "tap, then release and keep dragging" is the right call and transfers directly: holding a key through a whole drag is tiring and on Windows is *also* the mechanism Explorer uses for copy/move/link. A momentary tap that is consumed by our hook is the least interfering design. Escape (or tapping the trigger again) cancels; dropping outside the wheel lets the normal Explorer drop proceed.
2. **Hub shows the dragged item (thumbnail, name, size) and the highlighted action name.** Cheap, reassures the user they are operating on the right file, and the hub text label solves "what does this icon mean" without tooltips. Recommend: hub = thumbnail + size; label of the hovered wedge rendered in the hub; wedge labels uppercase short words, ≤ 8 wedges per ring for an easy hit area.
3. **Capability-driven, flat wheel per context.** Tangerine's wheels are *computed per file type*, which is exactly our capability-resolver model. Do not ship a tree of generic categories; compute the wedge list from the capability descriptors and render it.
4. **Output beside the original, originals untouched, reveal result.** Matches our file-safety rule. Tangerine's "reveal in Finder" ⇒ on Windows use `SHOpenFolderAndSelectItems` behind an explicit user action (toast button / progress window button), never auto-opening Explorer windows on every job (that would be noisy for batches and violates "never auto-launch produced files" in spirit).
5. **Tools that need parameters open a small window; conversions are zero-click.** Accept this honestly in the capability model: each command declares `RequiresParameters`; V1 Convert is zero-click, Resize/Compress may need a tiny parameter popover. Do not try to make everything zero-click; Tangerine didn't.

### 3.2 Change (where macOS choices do not transfer)

1. **Modifier keys are not free on Windows.** Tangerine uses Shift / Command / Control — on Windows **Ctrl = copy, Shift = move, Alt (or Ctrl+Shift) = link** during an OLE drag, and Explorer updates the drop effect live. The video frame already shows Finder's copy badge appearing under Tangerine's own trigger; on Windows the same conflict would also change the *semantics of the final drop*. Recommendation: the default trigger must be a non-modifier key (Space, a configurable letter, or a mouse button such as middle/X1), evaluated by the spike for: does the drag source still receive `QueryContinueDrag`, does the key reach a `WH_KEYBOARD_LL` hook during an OLE modal drag loop, and does Explorer ignore it. Modifiers can be *offered* as an option later, with a warning, but not the default. This is the single biggest divergence and is already ADR-0003's open question.
2. **Three separate wheels keyed by three modifiers → one wheel with a mode switch.** Three distinct trigger keys multiplies the modifier problem by three and was criticised as "dense on first use". Recommendation: one trigger, one wheel whose first ring is the capability set for the dragged file(s) (V1: Convert, Compress, Resize). If a second ring is ever needed, use nesting (hover/dwell on a wedge opens a sub-ring, e.g. Convert → JPG/PNG/WebP) with a centre "back" hit area — the brief already assumes nested wheels. Tangerine avoids nesting by having three flat wheels; we should instead prove one nested wheel in the spike and measure its latency and error rate.
3. **Overflow.** Tangerine shows ≤ 9 targets and silently hides the rest (its two "hidden options" bug fixes suggest pruning logic). Recommendation: capability descriptors carry a priority; the wheel shows the top N (≤ 8) and a "More…" wedge only if an ADR decides it. V1 never exceeds 8, so YAGNI.
4. **No Dock, no menu-bar app → tray icon.** The equivalent of "lives in the menu bar" is a notification-area icon with a context menu (Settings, Pause, Exit). Keep it idle: no polling, hook installed only once and doing no work.
5. **Result presentation → Windows 11 toast notifications.** Finder reveal + progress window maps to: a small non-activating progress window near the cursor (or anchored to the tray) for jobs over ~1 s, and a Windows toast on completion/failure with "Show in folder" and "Undo/Delete output" actions. Toasts require an AppUserModelID/shortcut or a packaged identity for unpackaged apps — the installer decision (MSIX vs classic) affects this; record it in the distribution ADR. Failures must be a distinct toast; never a silent no-op.
6. **Explorer vs Finder drag payloads.** Finder gives a single file-URL promise model; Explorer gives `CF_HDROP`, `CFSTR_SHELLIDLIST`, virtual files (Outlook, zips opened as folders, OneDrive placeholders). Recommendation: V1 accepts `CF_HDROP` with real paths only; anything else → "unsupported source" and let the drop fall through to Explorer. OneDrive "online-only" files must be detected (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`) and either hydrated with user consent or rejected — this has no macOS analogue in Tangerine's docs.
7. **DPI and multi-monitor.** Tangerine can rely on one DPI model per display and Cocoa coordinate conversion. On Windows the overlay must be PerMonitorV2, positioned from the physical cursor (`GetPhysicalCursorPos`) and clamped to the *work area* of the monitor under the cursor; the wheel radius is in DIPs scaled per monitor. Near an edge or corner, shift the wheel inward (keep it fully visible) rather than shrinking it; the spike should measure error rate at corners because the pointer is no longer at the hub.
8. **Batch and mixed selections.** Tangerine: "compatible files" only. Recommendation: the resolver computes the *intersection* of capabilities over the selection; empty intersection → wheel shows a single explanatory disabled state ("Mixed types — not supported in V1") rather than a partial wheel. Folders: out of scope for V1, fall through to Explorer.
9. **Cancellation and timeouts are mandatory for us even though Tangerine doesn't document them.** Progress window has Cancel; jobs carry `CancellationToken` + timeout; partial outputs are deleted; the original is hash-checked. This is a requirement of ours, not a Tangerine feature to copy.
10. **Naming/collisions.** Not documented by Tangerine; we already specify `photo.webp`, `photo (1).webp` with atomic move. Keep ours.
11. **Pricing/licensing.** Irrelevant to V1 (private build), noted only as context: a 20-file allowance plus one-time unlock is the market norm for this class of utility.

### 3.3 Things Tangerine does that we should explicitly not copy in V1

- Three wheels / three triggers (see 3.2.2).
- 275 formats and 26 tools; the "Actions" wheel (AirDrop, Terminal…); Float shelf. All out of scope; the capability system must make them *possible*, not present.
- Apple-silicon-only style hardware gating has no Windows analogue; we gate on OS baseline (ADR-0002) only.

## 4. Open questions a human should check by using the app

1. Is the key truly *tapped* (momentary) in 2.x, and does a second tap toggle/cancel? What happens if the key is tapped before the drag starts?
2. Does a drop on a wedge suppress Finder's own copy/move? What happens if you drop *outside* the wheel while it is open — normal Finder drop, or nothing?
3. How does the wheel behave at screen edges/corners and on a second display with different scaling? Does it reposition, clip, or shrink?
4. Measure open latency (trigger → wheel painted) with a high-speed recording; MacG reports micro-lags on M1 Air. Is the first open slower than subsequent ones (lazy loading)?
5. Overflow rule: drag a file type with many targets (e.g. PDF → 9+ formats). Are some hidden, is there a "more" wedge, is the order fixed?
6. Multi-file drag of **mixed** types onto the conversion wheel: refused, reduced to the intersection, or per-file?
7. Batch: one progress window or one per file? Can a batch be cancelled mid-way, and what remains on disk after cancel?
8. Output naming on collision (`photo.webp` exists): overwrite prompt, suffix pattern, or silent rename?
9. Failure path: drop a truncated/mislabelled file (e.g. `.png` that is really a JPEG, zero-byte file). What does the user see? Is any partial output left?
10. Is there a system notification on completion, or only the progress window + Finder reveal? Can reveal be turned off?
11. Settings contents: which keys can be chosen, can wheel contents or order be customised, is there a dwell/hover-to-open setting?
12. What exactly is "Float"? Does it act as a shelf that holds the file for a later drop?
13. Does the keyboard-only path (Enter on a file) exist outside a drag, i.e. is it a Finder extension or a global hotkey?
14. Does the wheel appear in non-Finder drag sources (Desktop, Mail attachments, browser downloads bar, Photos app)? This maps to our Explorer/Desktop/other-app matrix.
