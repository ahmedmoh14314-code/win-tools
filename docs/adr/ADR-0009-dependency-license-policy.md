# ADR-0009: Dependency and license policy

- **Status:** Proposed (policy drafted in `docs/engineering/rules/dependencies.md`; needs product-owner confirmation)
- **Date:** 2026-10-19
- **Related requirements:** DEP-001

## Context
The brief forbids accidental GPL contamination or unknown redistribution obligations and requires a matrix per dependency. The product may become commercial; licenses with revenue thresholds or non-commercial clauses matter.

## Decision (proposed)
- Permitted without ADR: MIT, BSD-2/3, Apache-2.0, MS-PL, .NET Foundation, Unlicense/CC0.
- Requires an ADR: LGPL (dynamic linking only), split or dual licenses with commercial terms (e.g. Six Labors), any "non-commercial" clause, any component that bundles optional delegates with their own licenses.
- Forbidden in the shipped product: GPL, AGPL, SSPL.
- Every dependency: matrix row before the first reference; version pinned; vulnerability check before checkpoints that touch dependencies.
- Platform APIs (WIC, Win32, COM) are preferred over libraries when fidelity and effort are comparable.

## Alternatives considered
- Allow GPL with process separation: rejected; distribution obligations and user confusion outweigh benefits for a small utility.
- No formal policy: rejected by the brief.

## Consequences
Some convenient libraries will be excluded or need an ADR; engine choice (ADR-0006) is constrained accordingly.

## Evidence / experiments
Not applicable (policy).

## Evaluator review
Pending (product owner confirmation required).
