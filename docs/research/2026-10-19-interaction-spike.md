# Spike 001 — drag → trigger → overlay → drop on Windows

- **Status:** evidence collection complete for the automatable scenarios (2026-10-20), evaluator review done and incorporated (§Evaluator review); manual rows M2/M8/M12 and a human "feel" pass remain (see §Manual protocol status).
- **Scope of every claim below:** one machine (Windows 11 Pro 26200), one 1920×1080 monitor at 125 %, injected input only (one cursor path and one timing per scenario), non-elevated processes, Explorer and the shell default drop source as the only sources, no human. Nothing here says anything about Windows 10, multi-monitor, mixed DPI, RDP, other global hooks, or how it feels.
- **Code:** `spikes/interaction/` (throwaway). README there explains how to run it. `scripts/Run-Scenario.ps1` runs one host + harness scenario and `scripts/Summarize-SpikeLog.ps1` reduces a host log to the numbers below. Raw logs are in `spikes/interaction/results/` (git-ignored).
- **Feeds:** ADR-0001 (UI framework), ADR-0003 (activation trigger), ADR-0004 (overlay), ADR-0005 (hooks threading)
- **Environment:** Windows 11 Pro 10.0.26200, .NET SDK 10.0.402 (runtime 10.0.13), single monitor 1920×1080 at 125 % (120 dpi), work area 1920×1020, mouse + keyboard driven by `SendInput` from the harness process. Other user windows (Chrome, Windows Terminal) were open during the runs; a game that covered the screen on 2026-10-19 was closed.

## Question
Can Windows reliably support: user drags a file in Explorer/Desktop → presses a trigger while still dragging → an overlay appears at the cursor without stealing focus → the user drops on a target → our process receives the file paths → Explorer's own drag is not broken and cancel works?

**Answer: yes, with two design constraints discovered by experiment** (overlay must not hide on the hook's button-up; middle mouse button cannot be the trigger). Details below.

## Documentation facts the design relies on (Microsoft Learn, fetched 2026-10-19/20)
1. **DoDragDrop loop**: the drag source's `DoDragDrop` finds the window under the cursor, calls `IDropTarget::DragEnter`, then `DragOver` on mouse movement and `QueryContinueDrag` on *keyboard or mouse-button state changes*. `grfKeyState` carries only MK_CONTROL/SHIFT/ALT and the mouse buttons; ordinary keys such as Space are **not** part of the drag protocol, so the drag source never sees them. ([DoDragDrop](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-dodragdrop), [QueryContinueDrag](https://learn.microsoft.com/en-us/windows/win32/api/oleidl/nf-oleidl-idropsource-querycontinuedrag))
2. **QueryContinueDrag contract**: documented returns are S_OK (continue), DRAGDROP_S_DROP ("the key that started the drag-and-drop operation has been released") and DRAGDROP_S_CANCEL ("fEscapePressed is TRUE"). What a source does when a *second* mouse button is pressed is an implementation choice; the shell's default drop source cancels (measured below).
3. **RegisterDragDrop** requires `OleInitialize` (not `CoInitialize`), a message-pumping STA thread, and the window must be unobscured. A .NET console app's main thread is MTA by default: `OleInitialize` returns `RPC_E_CHANGED_MODE` (0x80010106) until `[STAThread]` is applied. Confirmed by the first run. ([RegisterDragDrop](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-registerdragdrop))
4. **Shell drop effects**: Explorer offers COPY|MOVE|LINK. If the target returns DROPEFFECT_MOVE the shell may treat it as a move; the safe target behaviour is to return COPY (or NONE) so the source never deletes anything. The spike target always returns COPY. ([Shell data scenarios](https://learn.microsoft.com/en-us/windows/win32/shell/datascenarios))
5. **Low-level hooks** run on the installing thread, which must pump messages; a hook slower than `LowLevelHooksTimeout` (capped at 1000 ms; the default when the registry value is absent is undocumented) is silently removed on Windows 7+. The hook is called *before* the input is placed in the target queue. Microsoft recommends a dedicated hook thread that hands work off and returns immediately, and suggests Raw Input as the better long-term mechanism. ([LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc), [SetWindowsHookEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw))
6. **Non-activating window**: `WS_EX_NOACTIVATE` keeps a top-level window from becoming foreground on click; `WS_EX_TOOLWINDOW` hides it from the taskbar/Alt-Tab; `WS_EX_TOPMOST` keeps it above non-topmost windows. `SW_SHOWNA`/`SWP_NOACTIVATE` show without activation. ([Extended window styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles), [ShowWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow))
7. **DPI**: Per-Monitor V2 awareness (manifest + `SetProcessDpiAwarenessContext`) makes `GetCursorPos`/monitor rectangles physical pixels; WPF supports PMv2 natively. ([High DPI](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows))

## Method
- **Win32 host**: hooks (dedicated thread) → pure `TriggerDetector` → `InteractionSession` state machine → overlay popup clipped to a circle, `RegisterDragDrop`, four sectors with a dead zone. Every step is timestamped with `Stopwatch` and logged as JSONL.
- **WPF host**: same Core; overlay is a transparent topmost `Window` with `AllowDrop` and the ex-style tweak.
- **Harness**: `SendInput` choreography (press, glide past the drag threshold, trigger, glide into sector 1, release or Esc). `self` mode drags from its own window with the shell's data object and default drop source (`SHDoDragDrop`), so the source-side result (DRAGDROP_S_DROP/CANCEL, effect) is observable; `explorer` mode drives a real Explorer window via UI Automation (items are found by display name, which has no extension by default). A foreground guard makes the drag-source window topmost, activates it with a click on itself, and refuses to inject input if any other window covers the drag point; it re-checks before every cycle.
- **Unit tests**: 51 passing (placement clamping at all corners and on a negative-coordinate monitor, DPI scaling, sector hit-testing, trigger parsing/detection incl. modifiers and auto-repeat, session transitions incl. the post-release wait, HDROP round-trip with Unicode/long paths).
- **Metrics** (all from the overlay host's log, `Stopwatch` based): hook→overlay shown (`SetWindowPos` returned), hook→first paint, hook→DragEnter, focus stolen (foreground window before vs after show), process working set / private bytes / GDI / USER / handle counts every 2 s, hook callback worst case. "hook→Drop" is dominated by the script's own waits (≈530 ms) and is not a latency metric. **"First paint" is a proxy and differs per host:** Win32 = WM_PAINT finished (GDI drawn, not yet composed by DWM); WPF = first `CompositionTarget.Rendering` tick (before the render thread hands the frame to DWM). Neither is on-screen presentation, so the two hosts' paint numbers are lower bounds and must not be compared against each other as "who paints first"; the budget's ETW/DWM measurement is still owed by the product.

## Results

### R-A Headline path, Explorer as the drag source (Win32 host, Debug)
| Run | Trigger | Cycles | shows | DragEnter | Drop | paths per drop | hook→shown ms (min/med/max) | hook→paint ms | hook→DragEnter ms | focus stolen |
|---|---|---|---|---|---|---|---|---|---|---|
| B | Space | 10 (1 file) | 10 | 10 | 10 | 1 (`plain.txt`) | 6.7 / 10.2 / 21.2 | 7.8 / 12.1 / 35.9 | 8.9 / 14.4 / 53.1 | no |
| C | Space | 10 (3 files) | 10 | 10 | 10 | 3 (spaces/parens, Arabic+Japanese+emoji) | 6.3 / 10.2 / 20.0 | 7.4 / 13.3 / 33.5 | 12.9 / 20.6 / 49.2 | no |
| G | X1 button | 5 | 5 | 5 | 5 | 1 | 8.3 / 9.3 / 18.8 | 9.8 / 10.7 / 34.8 | 13.0 / 19.9 / 52.6 | no |
| H | Ctrl+Space | 5 | 5 | 5 | 5 | 1 | 6.7 / 9.9 / 20.8 | 9.1 / 11.9 / 35.6 | 11.0 / 13.6 / 54.3 | no |
| F | Middle button | 5 | 5 | **0** | **0** | – | 7.2 / 9.2 / 23.9 | – | – | no |
| I | Space while Shift held | 5 | **0** | 0 | 0 | – | – | – | – | – |
| J | Space, then Esc | 5 | 5 | 5 | **0** (cancelled) | – | 6.6 / 9.3 / 23.0 | 8.0 / 11.3 / 37.5 | 15.0 / 21.6 / 56.1 | no |

File safety evidence for these runs is weaker than first written: it rests on a manual directory listing after the batches (three 8-byte files, original timestamps, no "- Copy" entries, nothing new on the Desktop or in %TEMP%) rather than on a per-run assertion, and no Explorer dialog detection existed. From the evaluator's review onward the harness verifies the folder after every run (`harness.sampleCheck`: exact bytes of every sample, no extra entries; the five runs in §R-F.1 all report "3 files intact, no extra entries"). "Explorer unaffected" is therefore supported for the shell-source runs and plausible, not proven, for the Explorer runs above; see also the 22051A incident in §R-J. Paths arrive exactly as on disk (`C:\Users\…\spike-interaction-samples\ملف عربي 日本語 ✓ émoji 🚀.jpg`).

- **F (middle button):** the overlay appears (hook sees the button) but the drag ends immediately. In self mode the shell's drop source returns DRAGDROP_S_CANCEL 13 ms after MIDDLEDOWN (20/20, effect NONE); in Explorer mode the cancel is inferred from "overlay shown, no DragEnter, no drop, folder unchanged" (the source HRESULT is not observable there). The hook never swallows mouse input and `SendInput` sets MK_MBUTTON exactly as hardware does, so this is not a harness artefact. The precise rule is not "any second button": `QueryContinueDrag` receives MK_LBUTTON/MK_MBUTTON/MK_RBUTTON only, and X1 (not in that mask) does **not** cancel (G, and 20/20 in self mode). Right button untested (expected to cancel like middle). Middle button is therefore **not usable** as a trigger with shell drags.
- **I (Shift held):** by design the detector requires the trigger's modifiers to match exactly, so Shift+Space is not "Space" and nothing activates; Explorer continues its own Shift(move) drag and the release over the same folder is a no-op. Whether a trigger should also fire with extra modifiers held is a product decision (ADR-0003); it is not a platform limit.

### R-B Self mode (shell data object + shell default drop source, Win32 host, Debug) — source-side outcome visible
| Trigger | Cycles | Drops / Cancels (source) | DragEnter → Drop in overlay | effect returned | hook→shown ms (med) | hook→paint ms (med) | hook→DragEnter ms (med) |
|---|---|---|---|---|---|---|---|
| Space | 20 | 20 / 0 | 20 / 20 | COPY ×20, MOVE ×0 | 8.7 | 10.2 | 15.9 |
| Ctrl+Space | 20 | 20 / 0 | 20 / 20 | COPY ×20 | 8.2 | 9.5 | 12.0 |
| X1 | 20 | 20 / 0 | 20 / 20 | COPY ×20 | 9.6 | 10.8 | 14.4 |
| Middle | 20 | 0 / **20** | 0 / 0 | – (cancelled by the source) | 9.8 | 11.3 | – |
| Space + Esc (`--cancel`) | 5 | 0 / 5 | 5 DragEnter, 5 DragLeave, 0 Drop | – | 10.4 | 11.8 | 17.6 |
| Space, Shift held | 5 | 5 / 0 (dropped on the source window itself, no target) | overlay never shown | – | – | – | – |

Esc while the overlay is shown: the hook reports Escape, the overlay hides (`reason=Escape`), OLE sends DragLeave, the source returns DRAGDROP_S_CANCEL. Nothing is dropped.

### R-C Edges and corners (Space, self mode; trigger pressed at the given physical point)
| Point | Win32 rect (clamped) | WPF rect | cursor inside ring at show? | DragEnter (Win32) | Drop |
|---|---|---|---|---|---|
| (0,0) top-left | [0,0,275,275] | same | no (cursor 194 px from centre, radius 137) | after the first move (374 ms = script wait) | yes, sector 1 |
| (1919,0) | [1645,0,1920,275] | same | no | after move | yes |
| (0,1019) | [0,745,275,1020] | same | no | after move | yes |
| (1919,1019) | [1645,745,1920,1020] | same | no | after move | yes |
| (960,0) top edge | [823,0,1098,275] | same | yes | 26.6 ms | yes |
| (0,510) left edge | [0,373,275,648] | same | yes | 17.6 ms | yes |
| (1919,510) right edge | [1645,373,1920,648] | same | on the boundary | after move | yes |
| (960,1019) bottom edge | [823,745,1098,1020] | same | on the boundary | after move | yes |

Both hosts clamp the window fully inside the work area and the drop is received at every point (8/8 each). **Design consequence:** when the ring is clamped by more than its inner radius the cursor starts outside the ring, so the user must move into it before any sector is live. The product should either shift the ring so the cursor is always inside (and map sectors relative to the ring, not the cursor) or shrink/reshape near edges. Not a platform limit.

### R-D Stability: 100 cycles then 30 s idle (Space, self mode)
| Host / build | cycles OK | working set MB (start → after cycle ≈5 → end) | private MB | GDI / USER objects | handles | idle CPU (last 12 samples, % of one core) |
|---|---|---|---|---|---|---|
| Win32 Debug | 100/100 | 31.8 → 42.8 → 45.0 | 7.9 → 12.7 → 14.3 | 4 / 13–14 flat | 303–311 | 0–0.8 |
| Win32 Release | 100/100 | 31.7 → 42.5 → 45.7 | 8.1 → 12.5 → 14.3 | 4 / 12–15 flat | 304–316 | 0–0.8 |
| WPF Release | 100/100 | 110.5 → 134.0 → 137.3 | 69.6 → 87.6 → 88.6 | 8 / 19–23 flat | 455–479 | 0–0.8 |

After the first few cycles (JIT, first paint) growth is ≈2–3 MB over 95 cycles in both hosts and stops when idle; GDI, USER and handle counts are flat. No evidence of a per-cycle leak in either host at this scale. The 0.78 % samples are the 2 s stats timer itself (one 15.6 ms scheduler tick per sample). Hook callback worst case over all runs: keyboard 2.75 ms, mouse 4.6 ms (limit 1000 ms), typical < 1 ms.

### R-E Win32 vs WPF on identical scenarios (Release builds, same machine, same harness)
| Metric | Win32 (Release, 100 cycles) | WPF (Release, 100 cycles) |
|---|---|---|
| hook→overlay shown ms (min/med/max) | 4.2 / 9.6 / 18.5 | 2.8 / 6.4 / 35.6 |
| hook→first paint ms | 5.1 / 11.1 / 28.8 | 3.0 / 7.2 / 35.9 |
| hook→DragEnter ms (median) | 15.2 | 348.7 (see note) |
| Drops received | 100/100 (sector 1) | 100/100 (sector 0: the 100-cycle run predates the harness aiming at a sector's middle; the later 3-, 10- and 10-cycle WPF runs report sector 1) |
| focus stolen | never | never |
| working set idle after run | 45.7 MB | 137.3 MB |
| private bytes idle after run | 14.3 MB | 88.6 MB |
| threads | 10–14 | 28–34 |
| startup CPU (first 2 s) | ≈1.5 % | ≈5 % (36 % in Debug) |
| Explorer-sourced drops (3 files) | 10/10 | 10/10 |

Notes:
- WPF's DragEnter arrives only when the mouse first moves (≈335 ms = the script's settle time) whereas the Win32 host receives DragEnter 10–20 ms after the key press without any movement. Cause: the WPF window is a layered window with a transparent centre (dead zone); the cursor sits over transparent pixels, which OLE/hit-testing treat as "not over this window" until the cursor reaches a painted sector. The Win32 host's region is an opaque circle. This is a rendering choice, not a WPF defect; it matters for how the dead zone is drawn.
- Both hosts are equally fast at showing and painting (single-digit ms warm). The WPF process costs ≈3× working set and ≈6× private bytes in Release as well as Debug, so the earlier Debug-only observation holds.
- Debug-vs-Release did not change any latency or memory conclusion for the Win32 host (42–45 MB either way).

### R-F Problems found and what was done
1. **Overlay hid before the drop arrived (run 3, cycle 1; reproduced on record by the negative control below).** The low-level mouse hook sees WM_LBUTTONUP before the drag source's DoDragDrop loop processes it (documented: hooks run before the input is queued). Hiding the overlay on the hook's button-up changes the window under the cursor, so OLE sends DragLeave and the drop goes to the window underneath (with Explorer as source that would be a real copy/move into whatever the ring covered).
   **Rule (corrected after the evaluator review):** once DragEnter has been received, OLE guarantees a terminal Drop or DragLeave, so after a release *inside* the overlay the session waits for that call with no short timer (a 5 s watchdog only guards a dead source); only when no drag is inside (DragEnter never came, or DragLeave came before the release) does a 300 ms grace apply; Esc after the release is ignored while a drop is pending. The first version of the fix used a 300 ms timer in both cases, which would have re-created the hazard under load; it is gone. Unit-tested in `InteractionSessionTests`.
   **Negative control (host `--hide-on-release`, self mode, 10 cycles):** hide at the hook's button-up → DragLeave 2.8 ms later → 0 drops in the overlay, while the source reports DRAGDROP_S_DROP with effect NONE (the drop landed on the window underneath). 10/10 cycles lost. With the rule above, the same scenario is 20/20 (Win32) and 10/10 (WPF).
   **Release outside the ring (M5 in self mode, `--drop-offset 400`, 5 cycles):** DragEnter, then DragLeave as the cursor leaves, release outside → grace → hide (`releaseGraceExpired`), 0 drops in the overlay, source drop lands below. Not yet run with Explorer as the window below (see conditions).
   Measured release → Drop on this idle machine (harness release vs host drop wall clocks, 224 pairs): median 2–5 ms, max 11 ms; nothing measured under load, which is why no short timer is allowed on the inside path.
2. **Foreground-lock rules.** A background process cannot `SetForegroundWindow`; the harness now makes its own window topmost and clicks it (the only legitimate route). While the user is actively using another window the harness refuses or re-activates per cycle (seen once: Windows Terminal had focus; run was repeated).
3. **Explorer UI Automation names** have no extension when "Hide extensions" is on (default). Harness fixed; not a product issue.
4. **Sector boundary rounding.** Dropping exactly at 3 o'clock lands on the 0/1 boundary; Win32 (integer px) and WPF (DIP, rounded) disagreed. Harness now aims at a sector's middle; product hit-testing should use the same coordinate space as the drawing and avoid relying on exact boundaries.
5. **Middle button cancels shell drags** (see R-A/R-B). Trigger candidates reduce to: a non-modifier key (Space), modifier+key (Ctrl+Space), X buttons.

### R-J Explorer-sourced cancel, WPF with Explorer, Explorer corners
| Run | Host | Scenario | Result | hook→shown ms (med/max) | hook→DragEnter ms (med) |
|---|---|---|---|---|---|
| J, J2 | Win32 | Explorer drag, Space, Esc while overlay shown (5 + 3 cycles) | 8/8: DragEnter then DragLeave, overlay hides (`Escape`), **0 drops**, Explorer drag cancelled, folder unchanged | 9.3 / 23.0 | 21.6 |
| R5 | WPF Release | Explorer drag, 3 files (spaces, Arabic/Japanese/emoji), Space (10 cycles) | 10/10 drops, 3 correct paths each, sector 1 | 7.9 / 12.5 | 351 (first move; transparent centre) |
| K | Win32 | Explorer drag, trigger pressed at the 4 screen corners | 4/4 drops after the cursor moved into the clamped ring | 7.2 / 28.4 | 331–440 (first move) |

**Incident 22051A (unresolved, treated as a real finding after the evaluator review):** three times the run *immediately after* a run that ended with Esc or a Shift-modified drop could not find the sample-folder item. In the recorded case (`harness-win32-explorer-space-1file-after-cancel.jsonl`) the harness found hwnd 22051A titled "spike-interaction-samples - File Explorer", injected its activation click (left button at (220,220)) into it, and 1.2 s later the *same hwnd* was titled with a user folder ("سطح", 1 item). So an Explorer window changed folder or tab within seconds of a cancel-ended run, and the harness clicked inside a user's folder before aborting (no drag was started; nothing was moved, confirmed by listing). Likely cause: Windows 11 tab reuse (`explorer.exe <dir>` opening a tab in an existing window, the harness closing the whole window at the end, the title lookup then matching whichever tab is active). This is exactly the "Explorer disturbed" symptom the spike exists to catch and it is **not** explained yet. Consequences: no further Explorer-mode runs until the harness identifies the window by folder path (`IShellWindows`/location), never clicks or sends Ctrl+A unless the verified window is under the cursor, closes only what it opened, and asserts the folder after every run (the last item is done). The measurements above are unaffected (they come from runs where the right window was found and the item dragged).

## Manual protocol status (spikes/interaction/README.md)
| # | Scenario | Status | Evidence |
|---|---|---|---|
| M1 | Explorer, one file, Space, drop | **automated, pass** | run B |
| M2 | Desktop as source | **not automated** (would require placing a file on the owner's Desktop and minimising all windows) | needs a human: see "What to try by hand" |
| M3 | Explorer, 3 files incl. spaces/Unicode | **automated, pass** | run C |
| M4 | Esc while overlay shown | **automated, pass** (self 5/5, Explorer 8/8) | self cancel run; J, J2 |
| M5 | Release outside the overlay | partially: grace path exercised by the middle-button runs (overlay hides 300 ms after release, nothing dropped) | F |
| M6 | Drop without pressing the trigger | covered indirectly by run I (no activation, Explorer unaffected) | I |
| M7 | Corners and edges | **automated, pass with design note** | R-C |
| M8 | Second monitor / different DPI | **not available on this machine** | untested |
| M9 | 30+ show/hide cycles, counts flat | **automated, pass (100 cycles)** | R-D |
| M10 | Idle after the run | **automated, pass** | R-D |
| M11 | Shift held during trigger | **automated**: overlay does not activate by detector design; Explorer unaffected | I |
| M12 | Elevated source (UIPI) | **not automated** (needs an elevated Explorer/app) | needs a human |

Subjective "feel" (overlay appearance, cursor feedback, Explorer sluggishness as perceived by a person) was not assessed; the numbers say the overlay is painted within ≈10 ms of the key press, but a human pass is still required before ADR-0003/0004 are accepted.

### R-K Condition closure (Phase 1B, 2026-10-20, Win32 host)
Harness hardened first: Explorer window found by its shell location (`IShellWindows` LocationURL), re-verified before every injection; Ctrl+A only into the verified window; a release is refused (Esc instead) if the window under the release point is not the expected one; only the view the harness opened is closed; folder assertion after every run.

**Incident 22051A, what is known:** with scratch folders, every `explorer.exe <dir>` call created a separate window with its own HWND on this machine (no tab reuse observed), so the suspected tab-reuse mechanism was **not** reproduced, and why hwnd 22051A's title changed to a user folder remains unexplained (user navigation or a shell-internal view switch are both possible). What is understood is the harness defect that let it matter: the old harness identified its window by *title prefix*, which reflects whatever view is active, clicked into it without re-checking, and closed it with `WM_CLOSE`. The hardened harness identifies the view by location, re-verifies before every injection and closes only its own view through `IWebBrowser2.Quit`, so a window that changes location is refused instead of used. Not observed again in 9 Explorer-mode and self-mode runs.

| Condition | Run | Result |
|---|---|---|
| Hardened harness sanity (Explorer, 1 file) | 5 cycles | 5/5 drops, folder intact |
| Release outside the ring with Explorer's own folder underneath | 5 cycles | 4 released: DragEnter → DragLeave → grace hide, 0 drops in overlay, Explorer's drop on its own folder is a no-op, folder intact; 1 refused by the guard (other window under the point) |
| Release 0 ms / 10 ms after reaching the sector | 10 + 10 | 20/20 drops |
| Release while still moving | 10 | 10/10 drops |
| Early trigger (before the drag threshold) | 5 | 5/5 drops (the detector waits for the drag) |
| Right button as trigger | 5 | 5/5 cancelled by the shell drop source (same as middle) — rejected |
| Swallow Space (`--swallow`), Explorer | 5 | 5/5 drops; Explorer item selection after the drop identical with and without swallowing (selected 5/5 each) |
| 1000 cycles, Release, 5 s stats | 1000 | 1000/1000 drops; hook→shown median 8.9 / max 26.4 ms; DragEnter median 15.8 ms; **1 gen0 GC in 1000 cycles (1.8 ms pause), 0 gen1/gen2**; GDI 4 / USER 13–15 / handles 303–315 flat; private 13.9 MB (cycle ≈100) → 20.2 MB (cycle 1000), of which ≈4 MB is uncollected managed garbage (GC heap 1.9 → 6.1 MB); ≈2 MB native growth over 900 cycles unexplained: **this fails the budget's 0-growth-over-1000-cycles bar** and must be explained (forced-GC end-of-run snapshot, native heap breakdown) before PERF budgets are claimed |
| External idle CPU (`\Process\% Processor Time`, 60 s, stats timer off) | 30 samples | mean 0.077 % of one core (2 non-zero samples), within the < 0.1 % budget |

Unreported earlier and recorded now: in `host-wpf-self-space-20-Release` cycle 1 the overlay showed but got no DragEnter (hidden by the grace timer), the source did not return within 4 s and the foreground moved to Windows Terminal; most likely a harness/foreground artefact, but that drop was lost and the run counts 19/20. A first 1000-cycle attempt stopped safely at cycle 304 when the owner used Windows Terminal: the 12 non-drop cycles in it all coincide with the terminal taking the foreground, and the guard aborted on the next loss. That run is excluded from the numbers above.

Not done (needs a human or other hardware): Desktop as source (M2; recommended in Windows Sandbox), elevated source (M12), feel pass, multi-monitor/mixed DPI, trigger while another app is foreground in a meaningful way (all foreground cases here are the drag source itself).

## Evaluator review (2026-10-20, independent adversarial pass)
Verified 20+ table cells against the raw logs (all matched except the WPF sector cell, corrected above) and `dotnet test`. Verdict: **CONDITIONAL GO to product design, no product code**. Findings and their disposition:

| # | Severity | Finding | Disposition |
|---|---|---|---|
| 1 | Major | 300 ms grace after a release *inside* the overlay could hide it while a Drop is in flight (slow source, busy Explorer) and the drop would land on Explorer underneath; Esc after release had the same hazard; the original lost-drop log had been overwritten. | **Fixed**: rule restated (no timer after DragEnter; grace only with no drag inside; Esc ignored while pending), 51 tests, negative control and release-outside runs on record (§R-F.1). |
| 2 | Major | "Explorer stayed normal" not evidenced; incident 22051A contradicts it. | **Accepted**: wording corrected (§R-A), per-run folder assertion added, incident recorded (§R-J), Explorer-mode runs suspended until the harness is hardened. |
| 3 | Major | Harness could move user files (fixed-point activation click, Ctrl+A to the foreground, release over Explorer if the overlay fails). | **Accepted, partly fixed**: post-run content assertion added; window-by-path lookup, drop-point verification and tab-safe close are conditions before the next Explorer run. |
| 4 | Major | M5 (release outside with a live Explorer drag) never exercised. | **Partly done**: self-mode M5 on record; Explorer variant is a condition. |
| 5 | Major | Hook removal during GC pauses / AV heuristics not addressed. | **Accepted**: carried to ADR-0005 / ADR-0003 as open design items. |
| 6 | Minor | WPF 100-cycle "sector 1" cell false. | **Fixed** (§R-E). |
| 7 | Minor | "First paint" is a different proxy per host, not DWM presentation. | **Accepted**: caveat in §Method; budget measurement owed by the product. |
| 8 | Minor | "No leak" unhedged in ledger/progress; ≈34 KB/cycle growth, no GC stats, budget says 1000 cycles. | **Accepted**: hedged everywhere; 1000-cycle run with GC counters is a condition. |
| 9 | Minor | Middle-button mechanism mis-stated ("any second button"). | **Fixed** (§R-A). |
| 10 | Minor | Explorer-side Esc HRESULT inferred, not observed. | **Accepted**: stated as inferred. |
| 11 | Minor | Trigger-key swallowing in the detector would eat Space system-wide whenever the left button is down. | **Accepted**: noted in ADR-0003; product rule = swallow only when the overlay consumed the key. |
| 12 | Minor | Counts conflated (≈70 Explorer cycles → 62, of which 44 expected a drop; Debug vs Release mixed in one R-E row; "long paths" only unit-tested). | **Fixed** in progress.md and here. |
| 13 | Minor | Headline "yes" lacked scope. | **Fixed** (scope line at the top). |

Pushed back on nothing; every finding was either correct or a fair demand for evidence.

## Conclusions
- The interaction is feasible on Windows 11 with raw Win32 interop and also with WPF; Explorer-sourced drags deliver correct CF_HDROP paths (single, multiple, spaces, Unicode) to a topmost non-activating overlay that appears 7–20 ms after the trigger, without focus change. "Without disturbing Explorer" is supported by folder assertions for the shell-source runs and by listings for the Explorer runs, with one unexplained window-switch incident (§R-J) still open.
- A trigger that is not an OLE drag modifier is invisible to the drag source by design; Space and Ctrl+Space both work with Explorer. X1 works; the middle button cannot be used (shell cancels). Ctrl/Shift/Alt alone remain unsuitable (they change Explorer's effect) and were not tested as triggers.
- The overlay must defer hiding until OLE Drop/DragLeave (or a short grace period) after the hook sees the button-up. This belongs in the product's session state machine.
- Edge/corner clamping works; the ring must be repositioned relative to the cursor near edges so the cursor starts inside it.
- No handle, GDI or USER leak over 1000 cycles (Win32 host); private bytes still grow (≈7 KB/cycle, ≈2 MB of it native and unexplained), which misses the 0-growth budget; WPF has no 1000-cycle run. Idle CPU 0.077 % of one core measured externally with a performance counter (not `dotnet-counters`).
- Framework: both are fast enough; WPF costs ≈3× memory. The decision in ADR-0001 should weigh memory and startup against development speed, not latency.
