# ADR-0014: Hierarchical radial menu during the drag

- **Status:** Accepted (2026-10-21), after the evaluator re-check and the Explorer runs.
- **Date:** 2026-10-21
- **Related requirements:** UX-001, UX-004, UX-005, OPS-001, OPS-002

## Context
TASK 2 and TASK 3 brought the number of commands for one file up to 19, and a flat ring of 19 sectors cannot be hit reliably. The menu is used while the left button is held for the drag, so nothing can be clicked. Pointing and releasing are the only input.

The roadmap asks for protection against accidental activation (a minimum visible time and a movement threshold), evaluated by testing.

## Decision
**A compound marking menu: one ring on screen at a time, always centred on the pointer** (`WinTools.Core.Interaction`).

**Top level:**
- The categories fill the ring: Convert, Compress, Resize, Rotate, Tools, clockwise from 12 o'clock.
- `RadialMenu.Build` groups whatever the capability resolver allows by operation kind and leaves out empty categories. The view has no format logic.

**Opening a category:**
- Moving deep into a category (80 of 110 px at 96 DPI) opens it.
- The owner moves the ring so that it is centred on the pointer.
- The new ring shows that category's commands, plus a **Back** sector centred on the direction the stroke came from. Back is never narrower than a category sector, so retracing any stroke that opened the category lands on it and returns to the categories, centred the same way.
- The exact resize size from the settings is the last Resize command. If it has the same size as a preset but a different behaviour, it is labelled "(enlarge)" or "(stretch)".

**Why this replaces the first design:** the first design kept both levels on screen, with the commands in an arc around the category ring. Commands that are wider than their category lie in the directions of other categories. So in the first Explorer run, and in the evaluator's sweep of every command, a straight path to a command crossed a neighbour, which opened that neighbour and armed one of its commands. With one ring centred on the pointer, a straight stroke out of the centre stays inside one sector, so it reaches exactly the command it aims at. `MenuNavigatorTests.Every_straight_stroke_arms_exactly_the_command_aimed_at` checks this for every command, every format and mixed batches, three stroke directions, three target positions and four DPIs.

**Accidental-activation safety:**
- **Movement threshold:** a ring reacts only after the pointer has been inside its 28 px centre. Every ring appears centred on the pointer, so this holds at once, except where the ring was pushed aside at a screen edge; there the user has to come to the centre first. Starting off-centre therefore never selects anything. For the first ring, the start is the pointer position at the trigger.
- **Opening needs depth:** a category or Back opens only from deep in its sector, never from a graze.
- **Minimum visible time:** a drop runs a command only if its ring has been on screen for at least `ArmDelay` = 150 ms. A drop on a category, on Back, in the centre or outside the ring runs nothing. A release on a command before the delay plays the information sound, so "nothing ran" is never silent.
- **Re-check at drop time:** the drop's own data object is resolved again, and the command runs only if it is still offered.
- **What the menu sees:** the overlay follows the pointer from OLE's first DragEnter on. In Explorer that came 24 ms after the trigger at the median and up to 96 ms (the first drag of a fresh host). A movement made before that is not seen; the first position counts as a straight stroke from the centre. A hand that has already turned towards a command in that time opens the category its pointer is in, and the ring re-centres there, so nothing runs (Explorer finding 6).

**Drop effect:**
- The effect is COPY whenever a command is under the pointer, even before the arm delay has passed. The drop is judged when it happens.
- This makes the outcome independent of when OLE calls DragOver: a release after the delay always reaches the overlay and runs, and a release before it reaches the overlay and runs nothing.
- A UI timer repaints the command as armed when the delay passes.
- The effect is never MOVE.

**Overlay:** the window is a 220 px circle at 96 DPI, the size the latency budget was measured with. It is clamped to the monitor work area and moved by its owner when the menu changes level. It paints a `MenuView` and knows nothing about operations.

## Alternatives considered
- **A flat ring with every command:** 19 sectors of 19°. Not usable.
- **Both levels on screen, commands in an outer arc (the first implementation):** wrong commands could be armed on straight paths, as described above. Capping the arc at 180° still crosses neighbours. Requiring a return to the centre before switching only moves the failure to strokes that pass near the centre.
- **Dwell to open, with the submenu replacing the ring in place without re-centring:** the commands would appear under the pointer, and every level would cost a wait.
- **COPY only once armed:** the result would depend on whether OLE sends a DragOver after the delay while the pointer is still. A release on an armed command could become a DragLeave.

## Consequences
- **Adding operations:** a new operation of an existing kind appears without any change to the menu (`RadialMenuTests.A_new_operation_appears_without_any_menu_change`). A new kind needs one line in `RadialMenu.CategoryOf`.
- **Each level costs one deliberate stroke,** and the ring moves when it opens. A fast hand can stroke category then command without looking, but the release must come at least 150 ms after the command ring appeared.
- **At screen edges** a pushed-aside ring asks the user to come to its centre first. This is the price of the movement threshold.
- **This is the functional V1.** The visual design (shapes, colours, typography, animation) belongs to the UI phase.

## Evidence / experiments
- `tests/WinTools.Core.Tests/RadialMenuTests.cs`:
  - `RadialMenuTests`: grouping, order, empty categories, intersection, the custom size, distinct labels, the category of each kind, extensibility.
  - `MenuNavigatorTests`: graze vs open, re-centring, Back, the arm delay, the dead zone and outside, the pushed-aside ring at a screen edge, the exhaustive stroke sweep, view equality, DPI of the new monitor.
- Explorer runs: `docs/performance/2026-10-21-hierarchical-menu.md`. 71 of 71 cycles passed in 22 scenarios on the current design:
  - every command kind, conversion from each of the five formats, Remove metadata and Image info, and batches of 12;
  - the no-run cases: a release on a category, an unsupported file, Escape, and leaving the ring;
  - fast releases at 25, 100 and 400 ms, which held the arm rule per drop;
  - a trigger in the bottom-right screen corner, where nothing opened until the pointer had reached the pushed-aside ring's centre.

  The trigger → overlay latency was p95 16.4 ms warm and at most 36.9 ms on first activation.

## Evaluator review
First pass (2026-10-21): MERGE AFTER FIXES, with no blocker and no safety failure. The findings:
- **M1:** a straight path could arm a neighbour's command. This led to the redesign above.
- **M2:** near an edge the pointer started outside the dead zone. Fixed by the movement threshold rule.
- **M3:** the `fast` E2E check could not catch a lost or silently cancelled drop. The harness now requires the exact rule per drop and a Drop whenever the release was over the overlay.
- **Minor:** duplicate labels, region failure paths, a menu fixed by an empty first DragEnter, and a tautological test. All are addressed. The latency check is to be re-run in Explorer.

Re-check (2026-10-21): MERGE AFTER FIXES, with no blocker or major finding.
- **M1–M3 fixed:** the evaluator's sweep of 172,800 strokes, with clamping near edges and corners, found none that armed a wrong command or armed early.
- **Explorer runs:** required before merge.
- **Minor findings, all fixed:**
  - **N1:** the first ring's start comes from the trigger position.
  - **N2:** no re-centring once the overlay was hidden.
  - **N3:** a level change paints once.
  - **N4:** Back is at least as wide as a category (`Retracing_any_opening_stroke_lands_on_back`).
  - **N5:** a release before the delay has a sound, and the ADR claim is reworded.
  - **N6:** clamped re-centring is now a Core test (`Clamped_recentring_near_edges_never_arms_a_wrong_command`).

Explorer runs (2026-10-21): all green on the final harness. Three harness defects were found and fixed on the way, with the host logs as evidence: the foreground rights of a second harness, the fast path's moves before the first DragEnter, and an "outside" scenario that never left the ring. None of them was a product defect.
