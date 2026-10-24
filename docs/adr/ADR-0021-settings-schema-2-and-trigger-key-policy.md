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
2. **Trigger keys stay Space and F8–F12, and the evidence is stated as it is.** Space is the only key measured during a drag (every Explorer end-to-end run). F8–F12 are kept from V1 so existing settings keep working, but they are **not measured** either, and Explorer uses F10 (menu bar) and F11 (full screen). The typing-interference spike therefore measures all of F2–F12 as well as letters; its recorded result decides which keys are offered, and may remove F10 or F11. Until then F2–F7, letters and digits are not offered: they have the same lack of evidence as F8–F12 and no existing users to keep working. `TriggerKeyPolicy` gives the reason for every refusal (F1 opens Help; F2–F6 are Explorer's own keys; letters reach its type-ahead; modifiers change the drop effect). A refused key in the settings file is reported in the load note with its reason, and the window says why other keys are not offered. That is the plan's conflict detection as far as it can go before the spike.
3. **Reset to defaults** in the settings window asks first, then restores every value in the file and clears any message about the old values. "Start with Windows" is not in the file and stays as it is.
4. **Sections are read only when there are some and the file does not say schema 1.** A file that says schema 1 is read flat whatever its fields; anything else without sections is read flat too, as V1 did, which keeps the known fields of a newer flat file. A newer number is never lowered when written.

## Alternatives considered
- **Allow F2–F12 now, as D8 says.** Rejected until the spike: with a pass-through hook, F5 during a drag could refresh the view and F2 could start a rename. (The plan itself says "F1–F12" in §5 and "F2–F12" in D8; both wait for the spike.)
- **Remove F8–F12 now, since they are unmeasured too.** Rejected: V1 users who chose them would silently get Space. The spike decides them first.
- **Also write the flat schema 1 fields beside the sections**, so a downgrade keeps the user's values. Rejected: two copies of every value invite them to disagree; a downgrade shows defaults but never overwrites the file.
- **A nested record in memory.** Rejected for now: every caller would change, for no behaviour.
- **Swallowing the trigger key in the hook.** Rejected: hooks swallow nothing (rules), and a swallowed key-down without its key-up confuses other programs.

## Consequences
- An older WinTools sees a schema 2 file as newer (the existing rule). It shows its own defaults and never overwrites the file, so a downgrade loses nothing on disk but shows defaults until the user upgrades again.
- The typing-interference spike now decides F2–F12 as well as A–Z. It needs the real desktop (the Explorer harness) and is the next interaction task.

## Evidence / experiments
- `SettingsSchemaTests` (15): migration keeps every value, the section layout, round trip, repair of a partial file, Reset, and every key's verdict.
- `SettingsHardeningTests`: a newer schema (3 and 4, flat and in sections) is never moved aside or overwritten; the load note reports an invalid output folder in both layouts and a refused trigger key with its reason.
- Spike: not yet run (the desktop was in use).

## Evaluator review
One round (2026-10-24), no blocker. Major, fixed: the policy refused F2–F7 for lack of evidence while allowing F8–F12 with none either (decision 2 now says so and the spike measures all of F2–F12). Minors fixed: the refusal reasons reached no one (load note and window hint); Reset left stale error messages and had no confirmation; the footer could overlap the buttons; a schema 1 file with an object field named like a section lost its values (decision 4); the output-folder load note missed schema 2 files; no newer-schema test used sections; `ToJson` lowered a newer schema number; the ADR index row and ADR-0013's schema note. Open: Reset and the window are not yet checked by hand (the desktop was in use).
