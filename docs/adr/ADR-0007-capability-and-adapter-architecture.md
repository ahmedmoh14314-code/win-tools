# ADR-0007: Capability resolution and conversion adapter architecture

- **Status:** Proposed
- **Date:** 2026-10-19
- **Related requirements:** UX-004, ARCH-001, ARCH-002

## Context
The radial menu must show only valid operations for the dragged input, derived from one central capability system, and adding a format or operation must not touch the radial architecture. Engines must sit behind adapters so they can be replaced. Open questions: how capabilities are declared (code registrations vs data), how file type is detected (content sniffing first), how an operation maps to an adapter and its parameters, how nested menus (Convert → WebP) are expressed, and how multi-file drops are handled later without redesign.

## Decision
Not yet decided. Design to be produced after the interaction spike proves the UI shape.

## Alternatives considered
- Declarative capability descriptors (input type predicates → operation tree) resolved by one service: favoured by the brief.
- Per-adapter "CanHandle" queries aggregated at runtime: simpler but scatters logic.
- Plugin discovery by reflection: rejected for V1 (YAGNI, startup cost, security surface).

## Consequences
To be written with the decision.

## Evidence / experiments
Unit-tested resolver with at least two formats and one fake adapter; demonstration of adding a third format without UI changes.

## Evaluator review
Pending.
