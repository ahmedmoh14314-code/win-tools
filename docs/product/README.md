# product

- `brief.md` — the original product brief, verbatim. Never edited to reflect later decisions.
- `requirements.json` — the requirement ledger. `requirements.schema.json` defines its shape.

## Ledger rules
- Requirements are never deleted. Dropped items become `rejected` or `deferred` with a `rationale`.
- `verified` requires an `evidence` field naming the tests, benchmark file or recorded manual protocol and the commit.
- `blocked` requires `blockedBy`.
- Acceptance criteria may be refined, but refinement that makes a criterion weaker must be called out in the commit message.
- IDs are stable: `CATEGORY-NNN`. New items append; numbers are never reused.
- Run `scripts/Validate-Requirements.ps1` before committing a ledger change; it enforces the rules above.
