# ADR-0005: Process isolation and worker model

- **Status:** Proposed
- **Date:** 2026-10-19
- **Related requirements:** ARCH-003, ARCH-004, SAFE-003, SEC-001

## Context
Converters parse untrusted input and may crash, hang or exhaust memory. The main UI process must survive and stay responsive. Preferred model: Main app → Job → Worker process → Engine. Open questions: one worker per job vs a pooled worker; IPC mechanism (stdin/stdout framing, named pipes, anonymous pipes); progress reporting; cancellation (cooperative via IPC, hard via `Process.Kill`); timeouts; job objects to cap memory/CPU and kill child trees; whether to lower the worker's integrity level or use a restricted token; how the worker gets engine assemblies loaded on demand.

## Decision
Not yet decided.

## Alternatives considered
- In-process with try/catch: rejected; native engine crashes and OOM kill the process.
- Separate process per job: strong isolation; measure spawn cost against the job-start budget.
- Long-lived worker pool: lower latency; must handle poisoned workers and memory growth.
- Windows Job Objects for limits and kill-on-close: likely required in any variant.

## Consequences
To be written with the decision.

## Evidence / experiments
Spike: spawn cost of a .NET worker (framework-dependent vs self-contained, ReadyToRun), pipe round-trip latency, kill behaviour with a job object, and memory cap enforcement.

Related robustness item raised by the Spike 001 evaluator (2026-10-20): the global low-level hooks live in the main process on a managed thread that is suspended during every GC; a gen2 pause longer than `LowLevelHooksTimeout` gets the hook silently removed and the trigger dies without any symptom. The process model must include hook-health detection and re-installation (or a Raw Input alternative) and keep GC pressure away from the hook thread; this belongs to the same "main app must stay responsive" concern as worker isolation.

## Evaluator review
Pending.
