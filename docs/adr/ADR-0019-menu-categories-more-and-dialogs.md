# ADR-0019: Menu categories as module data, the "More…" policy, the dialogs process and folder inputs

- **Status:** Proposed (V2 PR 1.3)
- **Date:** 2026-10-24
- **Related:** ADR-0001 (WPF for ordinary UI only), ADR-0007 (capabilities), ADR-0014 (hierarchical radial menu), ADR-0016 (UI surfaces), ADR-0018 (jobs), UX-009 (folders)

## Context
V1's radial menu had a fixed set of categories (`MenuCategory`: Convert, Compress, Resize, Rotate, Tools) and relied on Resize never having more than 7 commands. V2 modules bring their own categories and more commands than fit on a ring. Some commands need a dialog: More…, and later passwords, page ranges and rename previews. Folders reach the menu in drops, but V1 neither recognised nor refused them (UX-009, V2 plan R8 and R9).

## Decision
1. **Categories are module data.**
   - `CapabilityCategory(Id, Label, Icon, Order)` is declared by each module. Modules that use the same id share the category, and the first declaration of an id wins.
   - The menu orders groups by `Order`. A capability whose category nobody declared goes under `Other`, at the end.
   - The overlay draws icons by key; an unknown key gets the generic glyph. The navigation sounds key on the ring position.
2. **A ring holds at most 8 sectors** (`RadialMenu.RingCapacity`). This follows ADR-0014: narrower sectors are too hard to aim at during a drag.
   - A category ring is Back plus at most 7 commands. Beyond that, the 7th sector is **More…**, holding the rest in menu order.
   - Beyond 8 categories, the 8th is a **More…** category holding the commands of the rest. Its own ring follows the same rule.
   - Nothing becomes unreachable (tested).
3. **More… opens a list in the dialogs process.**
   - `WinTools.Dialogs` is a short-lived WPF process like `WinTools.Settings`, so WPF stays out of the resident host.
   - The host writes a `PickRequest` (title, ids and labels, the drop point) as one JSON line on its stdin and reads a `PickResult` from its stdout.
   - It never receives file paths, which stay in the host.
   - The host runs the chosen command on the dropped files only if the chosen id is one it offered. Anything else, including closing the window and a 10-minute timeout, is a cancel.
   - The host grants the dialog foreground rights (`AllowSetForegroundWindow`), so the list takes the keyboard (Up, Down, Enter, Esc).
   - At the drop, re-resolution checks every command More… holds.
4. **Folders are a kind.**
   - The drop target marks a dropped folder with a trailing separator (the Windows convention for directory paths). It finds them on the same background task that expands 8.3 names, for names that have no extension, so the UI thread makes no file-system call.
   - From there, `FileKinds.IdentifyByName` sees the `Folder` kind by name alone, and capabilities accept it or not.
   - Image commands never accept folders. The ring says "Not for folders" instead of "No actions" when that is why nothing is offered.
   - A folder named like a file (`holiday.png`) reaches the worker as a file and fails with `IsAFolder` and a plain reason. It is never silent (UX-009).

## Alternatives considered
- **A scrollable or paged ring.** Rejected: paging during a drag adds a gesture that cannot be discovered, and ADR-0014's sector-size argument applies to every page.
- **Shrinking sectors to fit any count.** Rejected for the same reason.
- **A WPF popup in the host for More….** Rejected: ADR-0001 and ADR-0016 keep WPF out of the resident host. A separate process also isolates a dialog that hangs.
- **Checking every dropped path's attributes on the UI thread.** Rejected: a network path can block for seconds (see the ADR-0004 amendment on short names). Doing it for every drag on a task would make every drag wait.
- **Reading `CFSTR_SHELLIDLIST` attributes to find folders.** Rejected: a second parser and more shell calls, for information the trailing-separator convention carries.

## Consequences
- Adding a module's category or command needs no change in the menu, the overlay or the navigator.
- The dialogs process is now part of the payload: the host build copies it and the installer publishes it.
- More… can only be exercised end to end with a module that overflows a ring. Today's image menu never does, so its Explorer test comes with the first module that does.
- A folder whose name has an extension is checked only in the worker, as noted above.

## Evidence
- `MenuGoldenTests`: the V1 menu is unchanged. Its printer now prints the category label, which is what the enum name was.
- `MenuCategoryDataTests` (5): declared order, shared ids, undeclared categories, first declaration wins, icon keys.
- `RingCapacityTests` (6): no More… at exactly 7 commands; overflow at the command and the category level; nested overflow; every capability reachable.
- `PickContractTests` (5): only an offered id counts as a choice; the request crosses the pipe intact.
- `FolderInputTests` (Core) and the drop-target folder tests (Worker), with the worker refusing a folder named like a file.

## Evaluator review
Pending.
