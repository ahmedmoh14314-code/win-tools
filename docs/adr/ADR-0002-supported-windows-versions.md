# ADR-0002: Supported Windows versions and .NET target

- **Status:** Proposed
- **Date:** 2026-10-19
- **Related requirements:** PLAT-001, DIST-001

## Context
The API surface available for overlays, DPI, drag-and-drop and packaging differs between Windows 10 builds and Windows 11. Windows 10 reached end of mainstream support in October 2025 (verify current servicing status before deciding). The experimental audience is the owner and a few friends; their machines define the real baseline. The installed SDK is .NET 8.0.200; newer .NET releases exist and the target framework decision interacts with the UI framework (ADR-0001).

## Decision
Not yet decided. Interim (2026-10-19): the spike targets `net10.0-windows` with .NET SDK 10.0.402 pinned in `global.json`; this is the working assumption for the product target framework unless the Windows-baseline question changes it. Questions:
1. What OS versions do the experimental users actually run?
2. Which required APIs (if any) are Windows 11-only?
3. Which .NET version: current LTS vs latest STS, and the implication for self-contained size and single-file publish.

## Alternatives considered
- Windows 11 only: simplest, newest APIs, excludes Windows 10 users.
- Windows 10 1809+ and Windows 11: wider, constrains APIs, more test matrix.
- Windows 10 22H2 + Windows 11: a middle ground often used by .NET desktop apps.

## Consequences
To be written with the decision.

## Evidence / experiments
Survey of required APIs with minimum OS versions (docs/research). Audience OS poll.

## Evaluator review
Pending.
