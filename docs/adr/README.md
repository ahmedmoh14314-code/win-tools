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
| [ADR-0008](ADR-0008-packaging-strategy.md) | Packaging and private distribution for the experiment | Proposed | Research |
| [ADR-0009](ADR-0009-dependency-license-policy.md) | Dependency and license policy | Proposed | Product owner confirmation |
| [ADR-0010](ADR-0010-telemetry-policy.md) | Telemetry and diagnostics policy | Proposed | Product owner confirmation |
| [ADR-0011](ADR-0011-error-result-model.md) | Error / result model across process boundaries | Accepted (first slice) | — |
| [ADR-0012](ADR-0012-svg-and-heic-input.md) | SVG and HEIC/HEIF as input formats | Accepted (both deferred for V1) | — |
| [ADR-0013](ADR-0013-settings-and-output-policy.md) | Settings foundation and output policy | Accepted (data layer; UI deferred) | — |
| [ADR-0014](ADR-0014-hierarchical-radial-menu.md) | Hierarchical radial menu during the drag | Accepted | — |
| [ADR-0015](ADR-0015-worker-resource-limits.md) | Worker resource limits: pixels, memory cap, memory-bounded pool | Accepted | — |
| [ADR-0015](ADR-0015-worker-resource-limits.md) | Worker resource limits: pixels, memory cap, memory-bounded pool | Accepted | — |

Decision order recommended: 0002 → 0003 → 0001/0004 (same spike) → 0005 → 0011 → 0007 → 0006 → 0008. 0009 and 0010 can be confirmed any time.
