# Git conventions

- Branch: `main` is always a known-good state. Feature or spike work on `feat/<topic>` or `spike/<topic>` branches; merge with a fast-forward or a merge commit after verification.
- Commit message: `type(scope): summary` where type ∈ `docs|chore|spike|feat|fix|test|perf|refactor|adr`. Body explains why. No tool or assistant attribution trailers.
- Checkpoint = commit on a clean tree after verification. Tag milestones `m0-harness`, `m1-spike-interaction`, … when reached.
- Never push unless explicitly asked. Never rewrite history on `main`.
- `*.pfx`, `.env*` are ignored on purpose; do not force-add them.
- Large binaries (sample media) only under `tests/samples/` with a provenance note; keep each under 2 MB unless an ADR says otherwise.
