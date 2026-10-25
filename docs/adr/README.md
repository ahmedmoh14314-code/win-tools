# Architecture Decision Records

Template: `template.md`. Numbering is sequential and never reused. Old ADRs are never deleted; a new ADR supersedes them. An ADR moves to **Accepted** only after an evaluator review is recorded in it.

| ADR | Title | Status | Needs |
|---|---|---|---|
| [ADR-0000](ADR-0000-engineering-harness.md) | Record decisions as ADRs; engineering harness before product code | Accepted | — |
| [ADR-0001](ADR-0001-ui-framework.md) | UI technology for the radial drag/drop overlay | Accepted (native Win32 overlay) | — |
| [ADR-0002](ADR-0002-supported-windows-versions.md) | Supported Windows versions and .NET target | Proposed | Product decision + API survey |
| [ADR-0003](ADR-0003-activation-trigger.md) | Activation / input strategy during drag | Accepted with amendments | UIPI, RegisterHotKey alternative |
| [ADR-0004](ADR-0004-radial-overlay-implementation.md) | Radial overlay window implementation | Proposed | Spike |
| [ADR-0005](ADR-0005-process-isolation-model.md) | Process isolation / worker model | Accepted (first slice) | — |
| [ADR-0006](ADR-0006-image-conversion-engine.md) | Image conversion engine for V1 | Accepted (WIC + SkiaSharp for WebP) | — |
| [ADR-0007](ADR-0007-capability-and-adapter-architecture.md) | Capability resolution and conversion adapter architecture | Accepted (first slice) | — |
| [ADR-0008](ADR-0008-packaging-strategy.md) | Packaging and private distribution for the experiment | Accepted for the 0.1.0 beta (per-user Inno Setup, portable ZIP) | Signing |
| [ADR-0009](ADR-0009-dependency-license-policy.md) | Dependency and license policy | Proposed | Product owner confirmation |
| [ADR-0010](ADR-0010-telemetry-policy.md) | Telemetry and diagnostics policy | Proposed | Product owner confirmation |
| [ADR-0011](ADR-0011-error-result-model.md) | Error / result model across process boundaries | Accepted (first slice) | — |
| [ADR-0012](ADR-0012-svg-and-heic-input.md) | SVG and HEIC/HEIF as input formats | Accepted (both deferred for V1) | — |
| [ADR-0013](ADR-0013-settings-and-output-policy.md) | Settings foundation and output policy | Accepted (data layer; UI deferred) | — |
| [ADR-0014](ADR-0014-hierarchical-radial-menu.md) | Hierarchical radial menu during the drag | Accepted | — |
| [ADR-0015](ADR-0015-worker-resource-limits.md) | Worker resource limits: pixels, memory cap, memory-bounded pool | Accepted | — |
| [ADR-0016](ADR-0016-v1-ui-surfaces.md) | V1 user interface surfaces: rendering, motion, sound, feedback, tray and settings | Accepted | — |
| [ADR-0017](ADR-0017-compression-semantics.md) | Compress presets keep a measured fidelity, not a fixed encoder quality | Accepted (gate values await owner validation) | — |
| [ADR-0018](ADR-0018-job-contract-and-scheduler.md) | Job contract v2 and the job scheduler | Proposed (V2 PR 1.2) | Evaluator review |
| [ADR-0019](ADR-0019-menu-categories-more-and-dialogs.md) | Menu categories as module data, "More…", the dialogs process, folder inputs | Proposed (V2 PR 1.3) | Evaluator review; More… keyboard focus needs a spike before a module relies on it |
| [ADR-0020](ADR-0020-general-files-module.md) | General Files: any file is a kind, Tools is shared, Copy path runs in the host | Proposed (V2 PR 1.4) | Evaluator review; Explorer run |
| [ADR-0021](ADR-0021-settings-schema-2-and-trigger-key-policy.md) | Settings schema 2 in sections; trigger keys Space, F7, F9, F12 (a stray press is harmless), from the typing-interference spike | Accepted (V2 PR 1.5) | Letters wait for a file-drag-only condition; F2–F6, F10, F11 for a held-key and modifier probe |
| [ADR-0022](ADR-0022-pdf-module-qpdf.md) | The PDF module's structural commands run the official qpdf build in the PDF worker | Proposed (V2 PR 2.1) | Evaluator review; Explorer run |
| [ADR-0023](ADR-0023-archives-zip.md) | Archives start with ZIP on .NET's own code, behind a name policy and decompression-time limits | Proposed (V2 PR 5.1) | Evaluator review; Explorer run |
| [ADR-0024](ADR-0024-subtitles.md) | Subtitles (SRT, WebVTT) parsed in house in the text worker; malformed timing reported, not guessed | Proposed (V2 PR 7.1) | Evaluator review; Explorer run |
| [ADR-0026](ADR-0026-secrets-pdf-passwords.md) | A password goes from the dialogs process to one job and reaches qpdf only on its stdin (`@-`); Remove protection never lifts an author's restrictions | Proposed (V2 PR 2.2) | Worker, host and Explorer tests; evaluator pending |
| [ADR-0025](ADR-0025-documents-ooxml-read.md) | Office documents read in house as archives of XML: no DTD, no resolver, no macro run; Remove properties copies every other part byte for byte | Proposed (V2 PR 6.1, 6.2) | Evaluator review; Explorer run |

Decision order recommended: 0002 → 0003 → 0001/0004 (same spike) → 0005 → 0011 → 0007 → 0006 → 0008. 0009 and 0010 can be confirmed any time.
