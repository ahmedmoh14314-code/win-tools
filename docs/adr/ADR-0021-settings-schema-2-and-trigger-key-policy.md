# ADR-0021: Settings schema 2 in sections; trigger keys only where Explorer is known to ignore them

- **Status:** Proposed (V2 PR 1.5)
- **Date:** 2026-10-24
- **Deciders:** product owner (V2 plan §16–17, recommended defaults; D8)
- **Related requirements:** V2 plan §10 PR 1.5, D8; ADR-0016 (settings window)

## Context
V2 modules bring their own settings (PDF render DPI, archive limits, naming). Schema 1 is one flat object, so every module would add fields beside every other. The plan also extends the trigger keys (D8: Space and F2–F12, letters only after a typing-interference spike) and asks for conflict detection and Reset.

The low-level keyboard hook passes every key on (rules/windows-integration.md: hooks swallow nothing). A trigger key therefore also reaches Explorer during the drag. F2 renames, F3 opens search, F4 the address bar, F5 refreshes, F6 moves the focus, and letters select by name. Whether Explorer acts on them inside its drag loop is unproven, the same uncertainty D8 names for letters.

## Decision
1. **Schema 2 groups the file in sections:** `trigger`, `output`, `images`, `ui`. A module adds its own section when it has settings (YAGNI: no empty `pdf` or `archive` sections yet). In memory `AppSettings` stays flat, so no caller changes. A schema 1 file (flat) is read as it is and written back as schema 2. Missing sections take their defaults, and odd values are repaired by the existing normalisation.
2. **Trigger keys stay Space and F8–F12.** `TriggerKeyPolicy` judges every key and gives the reason for a refusal: F1 opens Help; F2–F7 are Explorer's own keys; letters and digits reach its type-ahead; modifiers change the drop effect. F2–F7 join the letters behind the typing-interference spike. A key moves to the allowed set only with that spike's recorded result. This is the plan's conflict detection: a key is offered only when nothing is known to conflict with it.
3. **Reset to defaults** in the settings window restores every value in the file. "Start with Windows" is not in the file and stays as it is.

## Alternatives considered
- **Allow F2–F12 now, as D8 says.** Rejected until the spike: with a pass-through hook, F5 during a drag would refresh the view and F2 could start a rename. Correctness over agreement (CONTRIBUTING).
- **A nested record in memory.** Rejected for now: every caller would change, for no behaviour.
- **Swallowing the trigger key in the hook.** Rejected: hooks swallow nothing (rules), and a swallowed key-down without its key-up confuses other programs.

## Consequences
- An older WinTools sees a schema 2 file as newer (the existing rule). It shows its own defaults and never overwrites the file, so a downgrade loses nothing on disk but shows defaults until the user upgrades again.
- The typing-interference spike now decides F2–F7 as well as A–Z. It needs the real desktop (the Explorer harness) and is the next interaction task.

## Evidence / experiments
- `SettingsSchemaTests` (13): migration keeps every value, the section layout, round trip, repair of a partial file, Reset, and every key's verdict.
- `SettingsHardeningTests`: a newer schema (now 3 and 4) is never moved aside or overwritten.
- Spike: not yet run (the desktop was in use).

## Evaluator review
Pending.
