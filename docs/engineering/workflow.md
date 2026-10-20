# Engineering workflow

This project is developed across many working sessions. Continuity, verification, rollback and independent review are first-class concerns. The short form is enforced by `docs/engineering/rules/workflow.md`; this file holds the rationale and the details.

## The loop for a substantial task

| # | Step | Output |
|---|---|---|
| 1 | Read project state | `docs/progress.md`, ADR index, requirement IDs in scope |
| 2 | Inspect relevant code/docs | Named files and the facts gathered from them |
| 3 | Research unknown APIs | Notes in `docs/research/` with sources |
| 4 | State assumptions | Explicit list; the user can correct them before work starts |
| 5 | Propose the smallest useful task | A task statement small enough to finish and verify in one session |
| 6 | Define acceptance criteria | Linked to requirement IDs; measurable |
| 7 | Verification strategy first | Failing tests, benchmark harness, or a written manual protocol |
| 8 | Implement only the scoped task | Vertical slice; no framework-building |
| 9 | Run verification | Actual command output quoted in the report |
| 10 | Independent evaluator pass | See `evaluator-pattern.md`; findings listed with verdicts |
| 11 | Fix valid findings | Invalid findings rebutted with evidence, not ignored |
| 12 | Update requirement status | Only with evidence; rejected/deferred items keep rationale |
| 13 | Update `docs/progress.md` | Phase, last known-good commit, decisions, open questions, blockers, next task |
| 14 | Commit a clean checkpoint | Working tree clean; conventional message; no push |

## What counts as substantial
Anything that: touches module boundaries, adds a dependency, changes the process/worker model, adds a Windows interop surface, changes file-writing behaviour, or spans more than one module. Small fixes still need steps 9, 13 and 14.

## Spikes
A spike answers one question with throwaway code under `spikes/<name>/`. It has a written question, a time box, and a result file in `docs/research/`. Spike code never migrates into `src/` by copy-paste; it informs an ADR and is re-implemented under tests.

## Session hand-off
A session ends only after `docs/progress.md` is accurate and the tree is committed or the uncommitted state is described in progress under "Current blockers". If a session is cut off, the next session starts by reconciling `git status` with `progress.md`.

## Rollback
Each checkpoint is a known-good state; `progress.md` names the last one. To roll back: `git log --oneline`, then `git revert` (preferred, history-preserving) or a new branch from the known-good commit. Never rewrite published history.

## Git conventions
See `git-conventions.md`.
