# ADR-0000: Record decisions as ADRs and build the engineering harness before product code

- **Status:** Accepted
- **Date:** 2026-10-19
- **Deciders:** product owner (instruction of 2026-10-19)
- **Related requirements:** all

## Context
The project will span many working sessions. Without durable state, decisions get re-litigated, verification gets skipped, and the repository drifts. The product owner required: explore before coding, plan before implementation, incremental progress, clean repository, Git checkpoints, structured cross-session state, independent review, and no completion claims without verification.

## Decision
Before any product code: Git on `main`, a concise `CONTRIBUTING.md` plus focused `docs/engineering/rules/*.md`, `docs/progress.md` for cross-session state, a JSON requirement ledger with explicit states, this ADR system, a mandatory 14-step workflow, and an adversarial evaluator pattern. Third-party development tooling is audited from source and scoped to the project.

## Alternatives considered
- Single large `CONTRIBUTING.md`: rejected, too costly in every session and hard to maintain.
- Diary-style progress log: rejected, becomes noise; `progress.md` is a snapshot, not a log.
- Markdown requirement list: rejected in favour of JSON so a script can enforce state rules.
- Installing whole tooling collections: rejected; smallest audited set only.

## Consequences
- Every substantial task costs a little ceremony (criteria, verification, review, progress update). This is intended.
- `progress.md` and the ledger must be kept accurate or the system decays; the workflow rule makes this a required step.
- Rules in `docs/engineering/rules/` are auto-loaded every session, so they must stay short.

## Evidence / experiments
Not applicable; process decision mandated by the product owner.

## Evaluator review
Not performed (process decision mandated by the product owner, not an architecture choice). Any later change to this harness goes through a normal evaluator pass.
