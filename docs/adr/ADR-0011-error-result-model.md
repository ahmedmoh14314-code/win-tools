# ADR-0011: Error and result model across module and process boundaries

- **Status:** Proposed
- **Date:** 2026-10-19
- **Related requirements:** ARCH-004, SAFE-003

## Context
Jobs cross a process boundary (ADR-0005). Failures (unsupported input, corrupt input, engine error, timeout, cancellation, worker crash, disk full, permission denied, output collision) must map to deterministic, actionable states in the UI and must never be mistaken for success. Exceptions cannot cross the IPC boundary; a serialisable result shape is required.

## Decision
Not yet decided. Design questions: a discriminated result type (`Succeeded` / `Failed(reason, category, userMessage, diagnostics)` / `Cancelled` / `TimedOut`) versus exceptions inside a process plus translation at the boundary; a stable failure-category enum; what diagnostics are kept locally; how partial progress is reported.

## Alternatives considered
- Exceptions everywhere: rejected across the IPC boundary; acceptable inside a module if translated at the edge.
- Result objects everywhere: consistent, slightly more verbose.

## Consequences
To be written with the decision.

## Evidence / experiments
Unit tests for every failure category; lifecycle tests show each failure surfaces correctly and leaves no false-success output.

## Evaluator review
Pending.
