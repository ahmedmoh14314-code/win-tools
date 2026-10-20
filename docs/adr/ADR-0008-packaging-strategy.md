# ADR-0008: Packaging and private distribution for the experimental stage

- **Status:** Proposed
- **Date:** 2026-10-19
- **Related requirements:** DIST-001, DIST-002, PLAT-003

## Context
A handful of users need an easy private install without Administrator rights. Options: portable zip (framework-dependent or self-contained, single-file), per-user installer (Inno Setup, WiX, Velopack-style), MSIX (requires signing with a trusted cert for sideloading, developer mode or cert install on the target machine). SmartScreen will warn on unsigned downloads; code signing certificates have cost and identity requirements. Packaging also interacts with ADR-0001 (WinUI 3 historically prefers packaged apps) and with how the worker process and engines are laid out.

## Decision
Not yet decided. Deliberately low priority until the interaction is proven.

## Alternatives considered
To be researched: portable self-contained zip; per-user installer; MSIX sideload; signing options (none / self-signed for friends / purchased certificate / Azure Trusted Signing).

## Consequences
To be written with the decision.

## Evidence / experiments
Research note on SmartScreen behaviour and signing options; a trial build of the chosen format installed on a clean non-admin account.

## Evaluator review
Pending.
