# Typing-interference spike: which keys may be the trigger (ADR-0021, plan D8)

- **Date:** 2026-10-25, on the development PC (Windows 11 Pro 26200, one 1920×1080 monitor at 100 %, Arabic keyboard layout active)
- **Build:** the Stage 1 integration copy (PRs #15–#19 merged), Release
- **Tool:** the Explorer harness, scenarios `keyprobe` and `keycontrol` (`tests/WinTools.EndToEnd`, marked SPIKE)

## Question
The low-level hook passes every key on. A trigger key therefore also reaches Explorer during the drag. Does Explorer act on F2–F12, letters or digits while a file is being dragged? If it does, that key cannot be the trigger.

## Method
- **`keyprobe --key K`**
  1. Selects the file in the harness's own Explorer window, presses the button on it and moves past the drag threshold, so an OLE drag is running.
  2. Presses K instead of the trigger and waits 700 ms.
  3. Cancels the drag with Escape and releases the button.
  4. Before, during and after, it records what a key could change, through UI Automation and the window rectangle:
     - the selected items (type-ahead);
     - the focused element (a rename box, the search box, the address bar, another pane, the menu bar);
     - the window title;
     - the window rectangle (full screen).
  5. **Inert** means all three records are equal.
- **`keycontrol --key K`**, the positive control: the same window and files, the item selected by a click, **no drag**, then K. It shows whether the probe can see Explorer's reaction to K at all.
- **Letters:** the type-ahead target file is named after the character the key types in the Explorer thread's keyboard layout (`ToUnicodeEx`).
  - On this PC A types "ش", so the first control run (a file named "AA probe") saw no reaction for any letter, while the digit 1 did.
  - The layout itself was not changed.
- Every run started a fresh host with default settings (trigger Space), so the host never reacted to K.

## Results
"During a drag" is two cycles per key. "Control" is one run without a drag.

| Key | During a drag | Control (no drag) | What the control saw |
|---|---|---|---|
| F2 | inert 2/2 | reacted | rename box (`UIRenameTextElement`) |
| F3 | inert 2/2 | reacted | focus to the search box |
| F4 | inert 2/2 | reacted | focus to the address bar |
| F5 | inert 2/2 | no visible change | a refresh changes nothing the probe records |
| F6 | inert 2/2 | reacted | focus moved to another pane |
| F7, F8, F9, F12 | inert 2/2 each | no visible change | Explorer has no function on these keys |
| F10 | inert 2/2 | reacted | menu-bar focus |
| F11 | inert 2/2 | reacted | full screen (rectangle 0,0,1920,1080) |
| A, D, E, Q, Z | inert 2/2 each (with the layout target) | reacted | type-ahead selected "شش probe" (A), and likewise |
| B | inert 2/2 | no target | B types "لا" (two characters) on this layout |
| 0, 1, 5 | inert 2/2 each | reacted | type-ahead selected "00 probe", "11 probe", "55 probe" |

**Trigger end to end:** a PNG conversion with the trigger set to F2 (3/3), F4, F6 and F11 (2/2 each) passed the normal checks:
- valid output;
- source unchanged;
- toast shown;
- Explorer kept the focus.

The host log confirmed `"TriggerKey":"F2"` (and F11).

## Reading
- During an OLE drag, Explorer's own drag loop holds the input. The control shows that F2, F3, F4, F6, F10, F11 and type-ahead are visible to the probe when Explorer acts, and none of them acted during the drag. **Explorer ignores these keys during a drag.**
- **F5** is "inert" only in the sense that nothing the probe records changed; it cannot see a refresh. **Not proven.**
- **F1** was not measured: it opens Help, possibly in a browser.
- **A separate finding from the host's code, not from Explorer:**
  - The ring opens on the trigger whenever the left button is down and the pointer has moved past the drag threshold (`InteractionSession.OnTrigger`, fed by the mouse hook). That holds for any left-button drag, not only for a file drag.
  - So a letter or digit pressed while holding the mouse in another program would open the ring over that program: a game (firing while pressing Q or E), a drawing program, a text selection.
  - The F keys are rarely pressed that way. Space, the default since V1, has the same exposure; it is recorded as a known limitation.

## Decision (applied in `TriggerKeyPolicy`)
- **Offered:** Space, F2, F3, F4, F6, F7, F8, F9, F10, F11, F12.
- **Refused:**
  - F1 (Help, not measured);
  - F5 (a refresh is invisible to the test);
  - letters and digits (they would open the ring over games and drawing programs);
  - modifiers (they change the drop effect).
- **Follow-up before letters:** a file-drag-only condition. Candidates:
  - the drag-image window that Explorer's drag source shows (`SysDragImage`);
  - or showing the ring only after an OLE `DragEnter` on an invisible target under the cursor.

  The keyprobe results above then already cover Explorer's side.

## Limits
- One PC, one layout (Arabic), Explorer as the drag source; other drag sources were not probed.
- F7–F9 and F12 have no Explorer function to observe, so the control cannot react for them; they are offered because Explorer has nothing that could act on them, and the drag probe saw nothing.
