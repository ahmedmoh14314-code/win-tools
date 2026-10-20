# Spike 001 — core interaction feasibility (throwaway code)

Question: can Windows reliably support **drag a file → press a trigger → overlay appears at the cursor → drop on a target → our app receives the paths**, without breaking Explorer's own drag?

This is experiment code. It is not product code and will not be copied into `src/`. Findings go to `docs/research/`.

## Layout

| Project | Purpose |
|---|---|
| `src/Spike.Interaction.Core` | Shared, testable pieces: P/Invoke, low-level hooks on a dedicated thread, trigger detector, session state machine, overlay placement/hit-test math, `IDropTarget` implementation, JSONL telemetry. |
| `src/Spike.Interaction.Win32` | Overlay host in raw Win32 (popup window, `SetWindowRgn` circle, GDI, `RegisterDragDrop`). |
| `src/Spike.Interaction.Wpf` | Overlay host in WPF (transparent topmost window, WPF drag events) sharing the same Core. |
| `src/Spike.Interaction.DragSource` | Automated harness. `self` mode drags from its own window using the shell's data object and default drop source; `explorer` mode drives a real Explorer window via UI Automation. Both inject input with `SendInput`. |
| `tests/Spike.Interaction.Core.Tests` | xunit tests for the pure logic (placement, sectors, trigger parsing/detection, session, HDROP parsing). |
| `results/` | Raw JSONL logs (git-ignored). |

## Build and test

```powershell
dotnet build Spike.Interaction.slnx
dotnet test  Spike.Interaction.slnx
```

## Run the overlay host

```powershell
# Win32 host, Space as trigger (default), logs to results/
.\src\Spike.Interaction.Win32\bin\Debug\net10.0-windows\win-x64\Spike.Interaction.Win32.exe --trigger space
# Alternatives: --trigger ctrl+space | mbutton | xbutton1 | f8   --swallow (eat the trigger key)   --no-require-drag
# WPF host, same options:
.\src\Spike.Interaction.Wpf\bin\Debug\net10.0-windows\win-x64\Spike.Interaction.Wpf.exe --trigger space
```

Then drag a file from Explorer or the Desktop, press the trigger while dragging, move onto a sector (A top, B right, C bottom, D left), release. The console and the JSONL log show `decision`, `overlay.show`, `overlay.painted`, `drag.enter`, `drop`, `overlay.hide` and periodic `stats`.

## Run the automated harness (moves your mouse and presses keys)

The harness refuses to run if any other window is under its drag point or in the foreground, because `SendInput` would otherwise drive that window.

```powershell
$h = .\src\Spike.Interaction.DragSource\bin\Debug\net10.0-windows\win-x64\Spike.Interaction.DragSource.exe
& $h --mode self     --cycles 20 --trigger space            # shell data object + shell drop source, no Explorer
& $h --mode explorer --cycles 10 --trigger space            # real Explorer window on the sample folder
& $h --mode self     --cycles 5  --trigger space --cancel   # Esc instead of release
& $h --mode self     --cycles 5  --trigger mbutton
```

Sample files (spaces, Arabic/Japanese/emoji, quotes, long names) are created under `%TEMP%\spike-interaction-samples`.

Host option `--hide-on-release` restores the first (wrong) behaviour of hiding the overlay on the hook's button-up; it is the negative control that shows the drop being lost. The harness verifies the sample folder (exact bytes, no extra entries) after every run and exits with code 5 if anything changed.

More harness options: `--trigger-at "x,y;x,y"` presses the trigger at those physical screen points (one per cycle, for edges/corners; the drop aims at the clamped ring, so pass the same `--radius` as the host), `--hold-modifier shift|ctrl|alt` holds an Explorer drag modifier for the whole drag, `--files N` (N > 1 selects all items in Explorer mode).

Scripted runs (start host → run harness → stop host → summarise):

```powershell
pwsh ./scripts/Run-Scenario.ps1 -HostKind win32 -Trigger space -Mode explorer -Cycles 10 -Files 3
pwsh ./scripts/Run-Scenario.ps1 -HostKind wpf -Trigger space -Mode self -Cycles 100 -Config Release -IdleAfterSeconds 30
pwsh ./scripts/Summarize-SpikeLog.ps1 -Path results/host-<name>.jsonl -Expected 10
```

Results and their interpretation: `docs/research/2026-10-19-interaction-spike.md`.

## Manual protocol (what automation cannot cover)

Record each row's outcome in `docs/research/2026-10-19-interaction-spike.md`.

| # | Scenario | Pass criteria |
|---|---|---|
| M1 | Explorer, one file, Space, drop on B | `drop` logged with 1 path; Explorer shows no copy/move dialog; original untouched |
| M2 | Desktop, one file | same |
| M3 | Explorer, 3 files incl. spaces/Unicode | 3 correct paths |
| M4 | Press Esc while overlay shown | overlay hides; Explorer drag cancels; nothing dropped |
| M5 | Release outside the overlay | overlay hides on release; Explorer behaves as a normal drop (or nothing) |
| M6 | Drop without ever pressing the trigger | Explorer unaffected; overlay never appears |
| M7 | Cursor at each screen corner, then trigger | overlay fully on screen, usable |
| M8 | Second monitor / different DPI (if available) | correct monitor, correct size |
| M9 | 30 show/hide cycles | `stats`: working set, GDI/USER counts flat |
| M10 | Idle 60 s after the run | `stats` cpuPct ≈ 0, hook max ms < 1 |
| M11 | Trigger while Shift held (Explorer move modifier) | note what Explorer and the overlay do |
| M12 | Drag from an elevated (admin) Explorer/app | expected: hooks and drop are blocked by UIPI; record |
