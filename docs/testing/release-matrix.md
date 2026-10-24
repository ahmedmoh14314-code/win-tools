# Release regression matrix

V2 plan §11, PR 0.4. Every release candidate runs this matrix on a real Explorer before it ships. Each owner-found bug
adds a row (or a case to a row) that fails on the code that had the bug.

## Automated rows

`tests/WinTools.EndToEnd/Run-ReleaseMatrix.ps1` runs them one after another and writes `release-matrix.md` (a table of
rows, cycles passed, exit code and whether any key was left down) next to per-row output and JSONL records.

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\WinTools.EndToEnd\Run-ReleaseMatrix.ps1 [-HostPath <installed WinTools.Host.exe>] [-Only 'row,row']
```

It drives the real mouse and keyboard, so it injects only on an idle, unlocked desktop:
- **Start it and leave the PC.** It waits until nobody has touched the mouse or keyboard for 20 s (`-IdleSeconds`) before the first row.
- **Using the PC pauses it.** Between rows there is a 3 s pause; input in its last 2.5 s pauses the run until the PC has been idle for 20 s again. After 30 min of use (`-MaxWaitMinutes`) it stops and lists the rows that did not run.
- **A row that hangs** for 15 min (`-RowMinutes`) is stopped, and every key and button it could have held is released.
- **The harness refuses to run** when the mouse buttons are swapped in Windows settings (an injected left click would be a right click). It also refuses to inject when its Explorer window is covered or loses the foreground.
- **After each row** the harness checks that no key or button is left down. If one is, the runner releases them and stops.
- **Before running,** quit an installed WinTools: the harness starts its own host, and a second instance does not start.

The report names the build under test: the host's version and the start of its SHA-256.

| Area | Rows |
|---|---|
| **First use** | `first-use-png`: cycle 1 runs on a fresh host and a cold worker |
| **Real compression** | `photo-compress` compresses `tests/samples/photo-960x720.jpg` (generated, see the samples README) at Balanced: the copy must be valid, the original untouched, and the saving at least 8 %. `photo-compress-high` expects the other honest outcome: this photo is already close to High's quality, so nothing is written, the toast says "Nothing to change" and the job reports NothingToDo. |
| **Multi-selection** | `ctrl-a-2`, `ctrl-a-10`, `ctrl-a-100`, `ctrl-click-*`, `shift-range-*`. A range longer than the view is made by clicking the first item, scrolling with the wheel and Shift-clicking the last. `--settle-ms 0` presses right after selecting, as a quick user does. Where to press is worked out before the last selection input, each cycle records the gap from that input to the press (`pressGapMs`), and a cycle with settle 0 fails when the gap exceeds 150 ms. |
| **Modifiers** | `ctrl-a-held-10`, `ctrl-click-held-*` and `shift-range-held-10` keep the key down through the drag, the trigger and the drop. `alt-refused` expects the "Release Alt" toast and nothing to run. |
| **Interaction** | `escape`, `outside`, `fast`, `category`, `edge` |
| **Paths** | `unicode-*` uses Arabic, emoji and spaces in folder and file names. `long-*` uses paths longer than 260 characters. Explorer hands those over as 8.3 short names (ADR-0004, 2026-10-24) only when 8.3 names are enabled on the temp volume (`fsutil 8dot3name query`). Where they are off, these rows test long paths without the short-name step. |
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
  - The 100-file Shift range was first made from the keyboard (Home, Shift+End). Explorer's list ignored the injected Home, and the status bar read "20 items selected" or "10 items selected". Adding scan codes to the injected keys did not help. The range is now made as a user makes it: click the first item, scroll with the wheel (which leaves the selection alone), then Shift-click the last item. The harness checks the last item's name, because UI Automation sees only the items Explorer has realised. Each cycle records the status bar's selection text, read after the drop.
  - With Shift released, the press now goes to a selected item in the middle of the view. The range's end can sit partly hidden at the bottom edge, where a press starts no drag. Every candidate is hit-tested to be the item itself.

## Evaluator review (PR #14)
No blocker. Three majors were fixed:
1. "At once" was not at once: the UI Automation walk sat between the selection and the press. The press point is now found before the last selection input, and the gap is recorded and bounded.
2. The runner refused a run started by a person. It now waits for idle.
3. There was no guard for swapped mouse buttons.

Minors fixed:
- the exception path releases each key on its own and always stops the host;
- every way Explorer can fail to open stops the host;
- the long-path padding no longer goes negative;
- a hung row is stopped and its input released;
- the report lists the rows that did not run and names the build;
- the selection text is read in any display language;
- the unused scan-code injection was removed.

Residuals:
- rows that need all 10 items on screen depend on the display scale; they assume 100–125 %;
- the photo sample's bytes depend on the GDI+ version and font smoothing of the machine that generated it, so the committed file and its hash are the reference.

## Runs

| Date | Build | Result |
|---|---|---|
| 2026-10-24 | main `14a4145`, the long-path fix (PR #13), the harness of `6111664` with the first photo sample (181,740 bytes) and the first High expectation | 22 of 23 rows passed, including 100 files by Ctrl+A and by Shift range, Ctrl and Shift held, Alt refused, Unicode and paths beyond 260 characters. `photo-compress-high` failed: High wrote a copy that saved 2.8 % (the defect PR #12 fixes). No key was left down after any row. |
| 2026-10-24 | PR #12 host (`fe2d2d9`, then `63cccc1`), single scenarios | `photo` at Balanced, 2 cycles, saved 37.3 %; `photo --op compress.high --expect already-optimized`, 2 cycles |
| 2026-10-24 | PR #13 host (`902af77`), single scenarios | `png --path-style long`, 3 cycles; `batch --batch 10 --path-style long --settle-ms 0`, 2 cycles |

| 2026-10-24 | main `d389835` (PR #12 merged), the committed harness and sample | 22 of 23 rows passed, including `photo-compress-high` (already optimized, nothing written) and `photo-compress` (37 %).
  - `ctrl-click-held-10` failed its one cycle: a Ctrl-click left 9 of the 10 files selected, so the drag carried 9 and the run went astray.
  - Re-run: 1/1, then 4/4 more cycles, so 6 of 7 cycles in all. The miss is in the harness's selection, not the product, and is recorded as an intermittent residual.
  - This run also found a runner defect: without `-Wait`, the exit code was empty and every row counted as failed. Fixed by taking the process handle while it runs.
  - No key was left down after any row. |
