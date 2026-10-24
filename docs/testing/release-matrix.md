# Release regression matrix

V2 plan §11, PR 0.4. Every release candidate runs this matrix on a real Explorer before it ships. Each owner-found bug
adds a row (or a case to a row) that fails on the code that had the bug.

## Automated rows

`tests/WinTools.EndToEnd/Run-ReleaseMatrix.ps1` runs them one after another and writes `release-matrix.md` (a table of
rows, cycles passed, exit code and whether any key was left down) next to per-row output and JSONL records.

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\WinTools.EndToEnd\Run-ReleaseMatrix.ps1 [-HostPath <installed WinTools.Host.exe>] [-Only 'row,row']
```

It drives the real mouse and keyboard, so it runs only on an idle, unlocked desktop. Before the first row nobody may
have touched the mouse or keyboard for 20 s, and between rows there is a 3 s pause that must stay untouched. Otherwise
the run stops and reports the rows that did not run. The harness also refuses to inject input when its Explorer window
is covered or loses the foreground, and it fails a cycle that ends with a key or button still down.

| Area | Rows |
|---|---|
| **First use** | `first-use-png`: cycle 1 runs on a fresh host and a cold worker |
| **Real compression** | `photo-compress` compresses `tests/samples/photo-960x720.jpg` (generated, see the samples README) at Balanced: the copy must be valid, the original untouched, and the saving at least 8 %. `photo-compress-high` expects the other honest outcome: this photo is already close to High's quality, so nothing is written, the toast says "Nothing to change" and the job reports NothingToDo. |
| **Multi-selection** | `ctrl-a-2`, `ctrl-a-10`, `ctrl-a-100`, `ctrl-click-*`, `shift-range-*`. `--settle-ms 0` drags at once after selecting, as a quick user does. A range longer than the view is made by clicking the first item, scrolling with the wheel and Shift-clicking the last. |
| **Modifiers** | `ctrl-a-held-10`, `ctrl-click-held-*` and `shift-range-held-10` keep the key down through the drag, the trigger and the drop. `alt-refused` expects the "Release Alt" toast and nothing to run. |
| **Interaction** | `escape`, `outside`, `fast`, `category`, `edge` |
| **Paths** | `unicode-*` uses Arabic, emoji and spaces in folder and file names. `long-*` uses paths longer than 260 characters, which Explorer hands over as 8.3 short names (ADR-0004, 2026-10-24). |
| **Installed artifact** | With `-HostPath`: first use, compress, Ctrl held and Alt refused, run against the installed host |

The data-object cases (first read fails, recovered on DragOver, malformed DROPFILES, release on Drop and DragLeave) are
unit tests with fake `IDataObject`s (`DropDataTests`), because Explorer cannot be made to produce them on demand.

## Manual protocol rows

These are not automated, by design. Record each run in the release's acceptance note with the date and the build.

1. **After an Explorer restart.** Restart Explorer from Task Manager, wait for the taskbar, then drag a file from a new Explorer window, press Space and drop on a command. The harness never restarts Explorer: it is the user's shell.
2. **After sign-in with Start with Windows.** Turn on Start with Windows, sign out and sign in, then do the first drag without opening the app. The menu must open on the first Space.
3. **Rubber-band selection.** Select 10 files by dragging a rectangle in an empty part of the view, then drag one of them and press Space. UI Automation cannot drive a rubber band reliably.

## Findings

- **2026-10-24, first run:** every file whose path was longer than 260 characters got "No actions". Explorer drops such a file as its 8.3 short name, which `PathPolicy` refuses. Fixed by expanding short names on local drives (ADR-0004 amendment). The rows `long-png` and `long-batch-10` cover it.
- **2026-10-24, first run:** `photo-compress-high` failed on the main of that day: Compress High wrote a copy that saved 2.8 %, the insignificant copy the owner reported (ADR-0017, PR #12).
- **2026-10-24, PR #12 host:** Balanced reported "Already optimized" on the photo although q82 passed every gate; the quality search let one non-monotonic failure hide it. Fixed in PR #12 (`CompressionSearchTests`).
- **2026-10-24, the sample itself:** the first photo sample had white noise of ±6 on flat GDI+ gradients, which behaves like a high-ISO shot; even after the search fix, High and Balanced honestly refused it. The generator now adds phone-level noise (±2), and the High row expects "already optimized", which this photo is at High (q88 would save 6.9 %).
- **Harness defects found while building the matrix (fixed):**
  - The 100-file rows looked for each file in the view, waiting about 8 s for each one that Explorer had not realised (minutes per cycle). It now reads the realised items in one pass.
  - `-Only` given through `powershell.exe -File` arrived as one string, so no row ran and the empty run reported success. Commas are now split, unknown names are refused, and a run with no rows fails.
  - The 100-file Shift range was first made from the keyboard (Home, Shift+End). Explorer's list ignored the injected Home, and the status bar read "20 items selected" or "10 items selected". The range is now made as a user makes it: click the first item, scroll with the wheel (which leaves the selection alone), then Shift-click the last item. The harness checks the last item's name, because UI Automation sees only the items Explorer has realised. Each cycle records the status bar's selection text.
  - With Shift released, the press now goes to a selected item in the middle of the view. The range's end can sit partly hidden at the bottom edge, where a press starts no drag. Every candidate is hit-tested to be the item itself.

## Runs

| Date | Build | Result |
|---|---|---|
RUNS
