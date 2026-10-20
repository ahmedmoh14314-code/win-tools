# win-tools — project rules

Windows desktop file utility: drag file → trigger → radial menu → drop → safe operation.
C# / modern .NET. Local-first, offline V1. **No product code yet: see `docs/progress.md` for the current phase.**

## Session start (always)
1. Read `docs/progress.md` (phase, last known-good commit, next task).
2. Read the ADR index `docs/adr/README.md` before touching anything architectural.
3. Follow `docs/engineering/rules/workflow.md`. The rules in `docs/engineering/rules/` apply to every change; `docs/` holds the detail.

## Non-negotiable principles
- **Engineering correctness over agreement.** Challenge technically unsound requirements, dependencies, shortcuts or UX ideas, and say why. Do not comply just because it was proposed.
- **No substantial implementation without an approved plan.** Spikes and throwaway experiments go under `spikes/` and are labelled as such.
- **Incremental vertical slices.** One scoped task per pass; no unrelated features together; YAGNI.
- **Verify before claiming done.** "Complete" requires the acceptance criteria demonstrated (tests, benchmark, or recorded manual check). Never weaken a test to make it pass.
- **Independent review for important work.** Architecture decisions and non-trivial changes get an adversarial evaluator pass (`docs/engineering/evaluator-pattern.md`).
- **Requirements never vanish.** `docs/product/requirements.json` states only move with evidence; rejected/deferred items keep a rationale.
- **Decisions are recorded as ADRs** (`docs/adr/`) with alternatives, consequences and evidence. Material uncertainty is resolved by experiment, not opinion.
- **Git checkpoints.** Small, clean commits after each verified task; never push unless explicitly asked.

## Hard product constraints
- Local/offline V1: no backend, accounts, cloud, telemetry, payments, Store, or AI features.
- **Originals are never destroyed.** Write to temp → validate → atomic move with collision-safe naming. A failed job must never look like a success.
- All input files are untrusted. No `cmd.exe /c "<string>"`; use argument lists. Paths may contain spaces, quotes, Unicode, and exceed 260 chars.
- Never run as Administrator without a documented, approved justification. Never auto-launch produced files. Never execute macros or embedded content.
- UI never knows engine details (FFmpeg, image libs, PDF libs). Engines sit behind adapters and can be replaced.
- Expensive or unsafe work runs outside the UI process; the UI never freezes; long operations take a `CancellationToken` and a timeout.
- No god classes. No unreviewed dependencies: every library needs an entry in the dependency/license matrix first.

## Scope guard
V1 prototype = images (JPG/PNG/WebP) with Convert/Compress/Resize, and the drag→trigger→wheel→drop interaction proven excellent. Nothing else until that is verified.
