# ADR-0014: Hierarchical radial menu during the drag

- **Status:** Accepted (2026-10-21)
- **Date:** 2026-10-21
- **Related requirements:** UX-001, UX-004, UX-005, OPS-001, OPS-002

## Context
TASK 2 and TASK 3 added resize, compress, rotate, remove metadata and image info. That is up to 19 commands for one file, and a flat ring with 19 sectors cannot be hit reliably. The menu has to be navigated while the left button is held for the drag, so nothing can be clicked. Pointing and releasing are the only input.

The roadmap also asks for protection against accidental activation (a minimum visible time and a movement threshold), evaluated by testing.

## Decision
**Two concentric levels around one centre** (`WinTools.Core.Interaction`):
- **Categories:** the inner ring (28–110 px at 96 DPI) holds the categories, clockwise from 12 o'clock in a fixed order: Convert, Compress, Resize, Rotate, Tools.
- **Where the menu comes from:** `RadialMenu.Build` groups whatever the capability resolver allows by operation kind, and leaves out empty categories. The view has no format logic.
- **Commands:** pointing at a category opens its commands in an arc of the outer band (110–190 px). The arc is centred on the category and gives each command at most 45°.
- **Custom size:** the exact resize from the settings is the last Resize command, unless that size is already a preset.

**Navigation (`MenuNavigator`, pure and tested):**
- **Staying open:** a category stays open while the pointer is in the centre or the outer band.
- **Switching:**
  - **Problem:** an arc can be wider than its category. Convert's 4 × 45° is wider than 72°, so the straight path out to a far command crosses a neighbouring category. The first Explorer run hit exactly this.
  - **Rule:** while a category is open, a neighbour opens only near the hub (the inner 40 % of the ring), or in a direction outside the open arc.
- **Nothing ever appears under the pointer:** commands appear outside it, so reaching one always takes an outward movement.

**Accidental-activation safety:**
- **Movement threshold:** the pointer starts in the 28 px dead zone at the centre, so nothing is hot until it moves out of it.
- **Minimum visible time:** a drop runs a command only if its category has been open for at least `ArmDelay` = 150 ms. Until then the cursor shows no-drop.
- **A drop that runs nothing:** a drop on a category, the dead zone or a gap. Its effect is DROPEFFECT_NONE, so OLE ends the drag with DragLeave and the file stays where it is.
- **Re-check at drop time:** the drop's own data object is resolved again, and the command runs only if it is still offered.

**Overlay (`RadialOverlay`):**
- The window is the full 380 px circle (at 96 DPI). It is clamped so the whole circle fits the monitor work area.
- Its region is the category ring plus the open arc, so only what is painted takes the drop. Elsewhere the drop falls to the window below, exactly as outside the ring before.
- It paints a `MenuView` and knows nothing about operations.

## Alternatives considered
- **A flat ring with every command:** 19 sectors of 19°. Not usable.
- **Dwell to open, with the submenu replacing the ring in place:** the commands would appear under the pointer, so a release right after opening would hit whatever lands there. Every level would also add a dwell wait.
- **Switching category anywhere in the ring:** the straight path to a far command closes the category the user is heading into. Found in the Explorer run.
- **An arming delay counted per command:** this would slow down moving between commands of a category that is already open, with no safety gain over the per-category delay.

## Consequences
- A new operation of an existing kind appears without any change to the menu (`RadialMenuTests.A_new_operation_appears_without_any_menu_change`). A new kind needs one line in `RadialMenu.CategoryOf`.
- **Timing depends on how often OLE calls DragOver:** a pointer held still on a not-yet-armed command shows no-drop until the next DragOver. The Explorer `fast` runs measure whether a release after the delay is still accepted.
- This is the functional V1. The visual design (shapes, colours, typography, animation) belongs to the UI phase.

## Evidence / experiments
- `tests/WinTools.Core.Tests/RadialMenuTests.cs`:
  - `RadialMenuTests`: grouping, order, empty categories, intersection, the custom size.
  - `CommandArcTests`: arcs, wrap-around, the full circle.
  - `MenuNavigatorTests`: arming delay, dead zone, the band without a category, switching rules, the diagonal path at 96/120/192 DPI, DPI scaling.
- Explorer runs (`tests/WinTools.EndToEnd`, 2026-10-21): see `docs/performance/2026-10-21-hierarchical-menu.md`.

## Evaluator review
Pending (TASK 4 pull request).
