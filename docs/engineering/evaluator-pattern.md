# Independent evaluator pattern

The implementer is not the sole judge of its own work. For important work a separate, fresh-context reviewer is run and its findings are addressed before the task is called done.

## When
- Every ADR before it moves from Proposed to Accepted.
- Every change that is "substantial" per `workflow.md`.
- Any claim that a requirement is now `verified`.

## How
Use an independent reviewer (a person or a separate automated review run) with **no access to the implementer's reasoning**: give it the diff or the ADR text, the relevant rule files, the acceptance criteria, and the instruction to be adversarial and evidence-based. Do not pass the implementer's conclusions or justification. For code, `/code-review` or `/security-review` can be the reviewer; for architecture, a `general-purpose` agent with the brief below. The implementer waits for the result and does not self-review in parallel.

## Architecture-decision brief (what the evaluator must challenge)
- Assumptions: which are untested? Which contradict Windows documentation?
- Hidden complexity: what will this cost in six months?
- Performance: idle cost, latency on the activation path, memory.
- Failure modes: crash, hang, malformed input, multi-monitor, DPI, locale, long paths.
- Maintainability: can an engine/framework be swapped? Is anything a god object?
- Windows-specific edge cases: Explorer drag semantics, hooks, elevation boundaries, focus stealing, UIPI.
- Evidence: is the decision backed by a spike result or by opinion?

## Implementation-change brief
Correctness · architecture boundaries · security (untrusted input, process launching, paths) · concurrency and cancellation · resource lifetime (handles, hooks, processes, temp files) · error handling and result model · tests (exist, meaningful, not weakened) · performance regressions · unnecessary complexity.

## Rules for the evaluator
- Verdict per finding: **Confirmed** (reproduced or shown in code/docs), **Plausible** (needs a check), **Rejected** (with reason).
- No ceremonial approval. "Looks good" without listing what was checked is a failed review.
- Severity-ranked output; the top finding first.

## Rules for the implementer
- Address every Confirmed finding or argue, with evidence, why not.
- Record disagreements in the ADR or the commit message.
- The report to the user lists the findings and their outcomes.
