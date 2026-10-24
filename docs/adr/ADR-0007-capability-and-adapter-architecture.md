# ADR-0007: Capability resolution and conversion adapter architecture

- **Status:** Accepted (2026-10-21) for the first product slice; evaluator review recorded below
- **Date:** 2026-10-19
- **Related requirements:** UX-004, ARCH-001, ARCH-002

## Context
The radial menu must show only valid operations for the dragged input, derived from one central capability system, and adding a format or operation must not touch the radial architecture. Engines must sit behind adapters so they can be replaced. Open questions: how capabilities are declared (code registrations vs data), how file type is detected (content sniffing first), how an operation maps to an adapter and its parameters, how nested menus (Convert → WebP) are expressed, and how multi-file drops are handled later without redesign.

## Decision
**A declarative operation catalog and one resolver in Core; the radial overlay renders whatever the resolver returns.**
- **The format table:** `ImageFormats` holds the decodable and encodable formats, extensions, display names and alpha policy.
- **Operations are rows** generated from that table and from fixed presets: `Operation(Id, Label, Kind, Target, Resize, Compress, RotateDegrees)`.
  - Ids: `convert.to-<format>`, `resize.percent-N`, `resize.box-WxH[-stretch]`, `resize.width-N`, `resize.height-N`, `compress.high|balanced|small|lossless`, `rotate.left|right|180`.
  - `Operation.AppliesTo(contentFormat)` is the single validity rule. A format never converts to itself; lossy compression applies to JPEG/WebP and lossless to PNG.
  - `OutputFormat` gives the written format (the target for convert, the source's own format otherwise), and `NameSuffix` names the output.
- **The flat ring shows conversions only** until the hierarchical menu lands; resize, compress and rotate are complete in the worker.
- **`CapabilityResolver.Resolve(paths)`** uses the extension only as a hint (`FormatHint`), because it runs during the drag and must not read files. That means no I/O on the UI thread and no hydration of cloud placeholders (SAFE-004).
  - The answer is the **intersection** of operations that apply to every file.
  - Unsupported files, refused paths (`PathPolicy`), and empty or oversized selections (more than 50 files) get no commands. The overlay then shows "No actions" and refuses the drop (effect NONE).
- **The worker never trusts the hint.** It detects the content format (`FileSignature`), requires `AppliesTo(content)` (otherwise `ContentMismatch`), checks the container, and decodes. A mislabelled file is converted according to its real content when the operation applies to it.
- **Adapter boundary:** the worker's codecs (`WicCodec`, `SkiaCodec`) behind `ImagePipeline` are the engine adapters. The host and Core know only operation ids and results.

## Alternatives considered
- Format logic in the overlay: rejected by the architecture rules.
- Content sniffing during the drag: rejected. It means I/O on the UI thread, hydrates cloud files, and gains nothing over checking in the worker.
- A general MIME-sniffing framework: not needed for two formats.

## Consequences
- Adding a format or operation adds rows in Core and an engine branch in the worker. The overlay does not change; it sizes the ring to the number of commands.
- ARCH-002's "fake adapter in tests" criterion is not met by design. Substitutability is shown at the process boundary instead, through the worker contract.

## Evidence / experiments
- `tests/WinTools.Core.Tests/FilesAndCapabilitiesTests.cs` (resolver, signature, structure check, naming); `ConversionJobTests.Mislabelled_file_is_refused_by_content`.
- Explorer end-to-end runs with a `.txt` file: the overlay shows "No actions" and nothing is written.

## Amendment: file kinds and capability descriptors (2026-10-24, V2 PR 1.1)
V2 adds modules beyond images (V2 plan §5), so the resolver and the menu stop speaking in image terms.

- **`FileKind`** (`WinTools.Core.Files`): a stable id, a family (`Image`, `Other`; more when a module needs one), a display name and its extensions. `FileKinds.IdentifyByName` is the menu-time hint; the worker still checks the content. `FormatHint` maps an image kind to the image module's `ImageFormat`.
- **`CapabilityDescriptor`** (`WinTools.Core.Capabilities`): `Id`, `Label`, `Category` (an id string), `Module`, an `InputRule` (which kinds, minimum and maximum count), `Shape` (per file or the whole selection), `Produces` (file, files, folder, facts, clipboard), `LongRunning`, and a module-owned `Payload`.
- **The image module** (`ImageCapabilities`) describes each `Operation` as a capability and keeps the operation as its payload. Nothing outside the module looks inside it. Ids, labels, categories and order are V1's.
- **`CapabilityResolver`** takes the descriptors of every module and intersects as before. It now also honours each capability's selection size.
- **`RadialMenu` and `MenuNavigator`** work on descriptors. The category order and icons are still a fixed table; making them module data is V2 PR 1.3.
- **Jobs still run image operations.** The coordinator unwraps the payload for the job runner, and generic jobs are V2 PR 1.2.
- **Not added (YAGNI):** content identification beyond today's signatures, families for modules that do not exist yet, parameters and dialogs. Each comes with the PR that needs it.

**Evidence (no behaviour change):**
- `MenuGoldenTests` pins the V1 menu: 21 selections, including mixed, unsupported, refused and empty ones, under 4 settings for the exact resize. It records every category, command id, label, output format and name suffix (1,270 lines). The golden file was written from main before the change and the refactor reproduces it byte for byte.
- All earlier tests pass unchanged in what they assert; only their calls moved to the new types.
- `CapabilityDescriptorTests` covers the kind registry, the image catalog's mapping, the selection-size rule and resolution across two modules.

## Evaluator review
Evaluator for the first product slice (2026-10-21), working from the code, the tests, its own probes against the built worker, and the raw end-to-end logs.
- Pass 1: CHANGES REQUIRED. B1: a rotated JPEG took about 5 minutes through the flip-rotator. M1: a folder with a trailing dot converted its twin. M2: a lone surrogate was altered on the pipe. M3: damaged image data inside an intact container was reported as success. M4: grace/DragEnter race. M5: the harness could not see a lost drop. Plus 12 minor items.
- Pass 2: MERGE once the minor items are fixed or recorded. The evaluator re-probed and confirmed B1 and M1–M3 fixed; no blocker or major remains. The minor items found in pass 2 are fixed (alternate-data-stream paths, out of memory reported as ImageTooLarge, job-object assignment logged, path policy re-checked in the worker client) or recorded as known limitations in the PR.
