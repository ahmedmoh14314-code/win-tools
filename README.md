# win-tools (working name)

A lightweight, local-first Windows desktop utility: drag a file, press a trigger,
a radial menu appears around the cursor, drop the file on an action, and the
operation runs safely (e.g. `photo.png` → Convert → WebP → `photo.webp`).

**Status: research and feasibility spikes. No product code exists yet; see `docs/progress.md`.**

## Start here (every session)

1. `CONTRIBUTING.md` — the rules that apply to every session.
2. `docs/progress.md` — current phase, last known-good commit, next task.
3. `docs/product/brief.md` — the product brief and constraints.
4. `docs/product/requirements.json` — the requirement ledger (states, never deletions).
5. `docs/adr/README.md` — decisions made and decisions still open.
6. `docs/engineering/workflow.md` — the mandatory engineering workflow.

## Repository map

| Path | Purpose |
|---|---|
| `docs/engineering/rules/` | Focused rule files (architecture, security, testing, ...). |
| `docs/product/` | Brief, requirement ledger, scope decisions. |
| `docs/research/` | Evidence from spikes and API research. |
| `docs/architecture/` | Architecture documents once decided. |
| `docs/security/` | Threat model and security notes. |
| `docs/performance/` | Budgets and benchmark results. |
| `docs/adr/` | Architecture Decision Records. |
| `docs/engineering/` | Workflow, evaluator pattern, environment, git conventions, audits. |
| `scripts/` | Repository tooling (not product code). |

Source code will live under `src/` and `tests/` once the architecture spike is approved.
