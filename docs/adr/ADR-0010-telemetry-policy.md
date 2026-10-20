# ADR-0010: Telemetry and diagnostics policy

- **Status:** Proposed (constraint stated in the brief; needs explicit confirmation)
- **Date:** 2026-10-19
- **Related requirements:** PLAT-002, SCOPE-004, PERF-002

## Context
The brief excludes analytics and remote processing for V1 and requires that network connectivity is unnecessary. Performance budgets still require instrumentation, which must be local only.

## Decision (proposed)
No telemetry, crash upload or analytics in V1. Instrumentation is local: `EventSource`/ETW events, an opt-in local log file with a size cap, and benchmark harnesses in the repo. Crash dumps, if collected, stay on the user's machine. Any future change requires a new ADR and explicit user consent design.

## Alternatives considered
- Opt-in crash reporting: deferred; useful later for a wider audience, not for a few friends.
- Third-party telemetry SDKs: rejected for V1 (network, privacy, dependency weight).

## Consequences
Diagnosing problems on friends' machines relies on them sharing local logs or dumps manually.

## Evidence / experiments
Not applicable (policy).

## Evaluator review
Pending (product owner confirmation required).
