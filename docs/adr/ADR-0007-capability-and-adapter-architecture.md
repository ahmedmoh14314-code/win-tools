# ADR-0007: Capability resolution and conversion adapter architecture

- **Status:** Accepted (2026-10-21) for the first product slice; evaluator review recorded below
- **Date:** 2026-10-19
- **Related requirements:** UX-004, ARCH-001, ARCH-002

## Context
The radial menu must show only valid operations for the dragged input, derived from one central capability system, and adding a format or operation must not touch the radial architecture. Engines must sit behind adapters so they can be replaced. Open questions: how capabilities are declared (code registrations vs data), how file type is detected (content sniffing first), how an operation maps to an adapter and its parameters, how nested menus (Convert → WebP) are expressed, and how multi-file drops are handled later without redesign.

## Decision
**A declarative operation catalog and one resolver in Core; the radial overlay renders whatever the resolver returns.**
- An `Operation(Id, Label, Source, Target)` row describes each command. `OperationCatalog` lists the rows of this build: `convert.png-to-jpeg` and `convert.jpeg-to-png`.
- `CapabilityResolver.Resolve(paths)` uses the extension only as a hint (`FormatHint`), because it runs during the drag and must not read files. That means no I/O on the UI thread and no hydration of cloud placeholders (SAFE-004).
  - An operation is offered when every dropped file has its source format.
  - Mixed, unsupported, empty or oversized selections (more than 50 files) get no commands. The overlay then shows "No actions" and refuses the drop (effect NONE).
- The worker never trusts the hint. It checks the content signature (`FileSignature`) and the container structure, and decodes. A mislabelled file fails with `ContentMismatch`.
- Adapter boundary: the worker's `ImageTranscoder` is the engine adapter. The host and Core know only operation ids and results. A fake engine adapter interface is **not** introduced: there is one engine, and the process boundary already isolates it (YAGNI).

## Alternatives considered
- Format logic in the overlay: rejected by the architecture rules.
- Content sniffing during the drag: rejected. It means I/O on the UI thread, hydrates cloud files, and gains nothing over checking in the worker.
- A general MIME-sniffing framework: not needed for two formats.

## Consequences
- Adding a format or operation adds catalog rows and engine support. The overlay does not change; it sizes the ring to the number of commands.
- ARCH-002's "fake adapter in tests" criterion is not met by design in this slice. Substitutability is shown at the process boundary instead (the worker contract), and it stays open in the requirements ledger.

## Evidence / experiments
- `tests/WinTools.Core.Tests/FilesAndCapabilitiesTests.cs` (resolver, signature, structure check, naming); `ConversionJobTests.Mislabelled_file_is_refused_by_content`.
- Explorer end-to-end runs with a `.txt` file: the overlay shows "No actions" and nothing is written.

## Evaluator review
Evaluator for the first product slice (2026-10-21), working from the code, the tests, its own probes against the built worker, and the raw end-to-end logs.
- Pass 1: CHANGES REQUIRED. B1: a rotated JPEG took about 5 minutes through the flip-rotator. M1: a folder with a trailing dot converted its twin. M2: a lone surrogate was altered on the pipe. M3: damaged image data inside an intact container was reported as success. M4: grace/DragEnter race. M5: the harness could not see a lost drop. Plus 12 minor items.
- Pass 2: MERGE once the minor items are fixed or recorded. The evaluator re-probed and confirmed B1 and M1–M3 fixed; no blocker or major remains. The minor items found in pass 2 are fixed (alternate-data-stream paths, out of memory reported as ImageTooLarge, job-object assignment logged, path policy re-checked in the worker client) or recorded as known limitations in the PR.
