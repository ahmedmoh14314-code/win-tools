# Engineering workflow (mandatory for substantial work)

Full text with rationale: `docs/engineering/workflow.md`. Short form:

1. **Read state**: `docs/progress.md`, relevant ADRs, requirement entries.
2. **Inspect** the relevant code/docs before proposing anything.
3. **Research** unknown Windows/.NET APIs (official documentation). Do not guess API behaviour.
4. **State assumptions** explicitly.
5. **Propose the smallest useful task** and get it approved if it is substantial.
6. **Define acceptance criteria** up front (link to requirement IDs).
7. **Tests / verification strategy first** where practical (TDD for logic; recorded manual protocol for UI/interaction).
8. **Implement only that task.**
9. **Run verification** (build, tests, benchmarks as relevant) and read the output.
10. **Independent evaluator pass** (see `docs/engineering/evaluator-pattern.md`) for architecture decisions and non-trivial changes.
11. **Fix valid findings**; push back on invalid ones with evidence.
12. **Update requirement status** only with evidence attached.
13. **Update `docs/progress.md`** (phase, last known-good commit, decisions, open questions, next task).
14. **Commit a clean checkpoint.** Working tree clean; message explains the why.

Never bundle unrelated major features. Prefer a working vertical slice over a framework.
If a step is skipped, say so explicitly in the report.
