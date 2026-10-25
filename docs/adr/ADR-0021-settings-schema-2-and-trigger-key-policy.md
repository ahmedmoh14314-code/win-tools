# ADR-0021: Settings schema 2 in sections; trigger keys only where Explorer is known to ignore them

- **Status:** Accepted (V2 PR 1.5; trigger keys decided by the spike, 2026-10-25)
- **Date:** 2026-10-24
- **Deciders:** product owner (V2 plan §16–17, recommended defaults; D8)
- **Related requirements:** V2 plan §10 PR 1.5, D8; ADR-0016 (settings window)

## Context
V2 modules bring their own settings (PDF render DPI, archive limits, naming). Schema 1 is one flat object, so every module would add fields beside every other. The plan also extends the trigger keys (D8: Space and F2–F12, letters only after a typing-interference spike) and asks for conflict detection and Reset.

The low-level keyboard hook passes every key on (rules/windows-integration.md: hooks swallow nothing). A trigger key therefore also reaches Explorer during the drag. F2 renames, F3 opens search, F4 the address bar, F5 refreshes, F6 moves the focus, and letters select by name. Whether Explorer acts on them inside its drag loop is unproven, the same uncertainty D8 names for letters.

## Decision
1. **Schema 2 groups the file in sections:** `trigger`, `output`, `images`, `ui`. A module adds its own section when it has settings (YAGNI: no empty `pdf` or `archive` sections yet). In memory `AppSettings` stays flat, so no caller changes. A schema 1 file (flat) is read as it is and written back as schema 2. Missing sections take their defaults, and odd values are repaired by the existing normalisation.
2. **Trigger keys: Space, F2–F4 and F6–F12, decided by the typing-interference spike** (`docs/research/2026-10-25-typing-interference.md`).
   - Explorer ignored each of these keys during a drag (two cycles per key). A control without a drag showed that the probe sees Explorer's reaction to F2, F3, F4, F6, F10, F11 and type-ahead when there is one.
   - A conversion with F2, F4, F6 and F11 as the trigger passed end to end.
   - **F5** is refused: a refresh is invisible to the probe.
   - **F1** is refused: Help, not measured.
   - **Letters and digits** are refused although Explorer ignored them during a drag. The ring opens on any left-button drag, not only on a file drag, so a letter pressed while holding the mouse in a game or a drawing program would open it there. They wait for a file-drag-only condition.
   - `TriggerKeyPolicy` gives the reason for every refusal. A refused key in the settings file is reported in the load note, and the window names the keys it does not offer and why.
3. **Reset to defaults** in the settings window asks first, then restores every value in the file and clears any message about the old values. "Start with Windows" is not in the file and stays as it is.
4. **Sections are read only when there are some and the file does not say schema 1.** A file that says schema 1 is read flat whatever its fields; anything else without sections is read flat too, as V1 did, which keeps the known fields of a newer flat file. A newer number is never lowered when written.

## Alternatives considered
- **Allow F2–F12 before the spike, as D8 says.** Rejected at the time: with a pass-through hook, F5 during a drag could refresh the view and F2 could start a rename. The spike then measured them; F5 stays refused.
- **Offer letters, since Explorer ignored them during a drag.** Rejected for now: the risk is outside Explorer (any left-button drag opens the ring), and the spike measured Explorer only.
- **Remove F8–F12 before the spike, since they were unmeasured too.** Rejected: V1 users who chose them would silently have got Space. The spike kept them.
- **Also write the flat schema 1 fields beside the sections**, so a downgrade keeps the user's values. Rejected: two copies of every value invite them to disagree; a downgrade shows defaults but never overwrites the file.
- **A nested record in memory.** Rejected for now: every caller would change, for no behaviour.
- **Swallowing the trigger key in the hook.** Rejected: hooks swallow nothing (rules), and a swallowed key-down without its key-up confuses other programs.

## Consequences
- An older WinTools sees a schema 2 file as newer (the existing rule). It shows its own defaults and never overwrites the file, so a downgrade loses nothing on disk but shows defaults until the user upgrades again.
- Space has the same exposure as letters outside Explorer (a left-button drag in a game while pressing Space opens the ring). This is known since V1 and is recorded as a limitation; the file-drag-only condition would close it for every key.

## Evidence / experiments
- `SettingsSchemaTests` (15): migration keeps every value, the section layout, round trip, repair of a partial file, Reset, and every key's verdict.
- `SettingsHardeningTests`: a newer schema (3 and 4, flat and in sections) is never moved aside or overwritten; the load note reports an invalid output folder in both layouts and a refused trigger key with its reason.
- Spike (2026-10-25): `keyprobe` (during a drag) and `keycontrol` (no drag) for F2–F12, A, B, D, E, Q, Z, 0, 1 and 5; the results table is in the research note. Trigger end to end with F2 (3/3), F4, F6 and F11 (2/2 each).
- `SettingsSchemaTests.Every_key_offered_or_refused_has_a_reason`: every offered key and every refusal (F1, F5, letters, a digit, a modifier), shown failing on the earlier policy (5 of 16).

## Evaluator review
One round (2026-10-24), no blocker. Major, fixed: the policy refused F2–F7 for lack of evidence while allowing F8–F12 with none either (decision 2 now says so and the spike measures all of F2–F12). Minors fixed: the refusal reasons reached no one (load note and window hint); Reset left stale error messages and had no confirmation; the footer could overlap the buttons; a schema 1 file with an object field named like a section lost its values (decision 4); the output-folder load note missed schema 2 files; no newer-schema test used sections; `ToJson` lowered a newer schema number; the ADR index row and ADR-0013's schema note. Open: Reset and the window are not yet checked by hand (the desktop was in use).
