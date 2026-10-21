# Hierarchical radial menu: Explorer runs (2026-10-21)

Machine: the development PC (Windows 11 Pro 26200, 120 DPI, one monitor 1920×1080). Release builds. All input was injected by `tests/WinTools.EndToEnd` into a real Explorer window, and each run was judged against the host log.

The navigation is the product's own: point at the category, move out to the command, then wait at least the arm delay. Each run records whether the mouse button and Space were still down at the end; none was.

## Findings during the runs
1. **A command arc wider than its category made the path out cross a neighbour (first design).** The first run showed Convert closing and Tools opening on the way to "To JPEG". A switching rule fixed that path only, as finding 4 showed.
2. **The window region did not include the command arc (first design).** `CreatePolygonRgn` was called with a point count of 0, so the drag left the overlay at the command. The arc region no longer exists: the current design uses a plain circle.
3. **A harness crash once left the synthetic left button down.** The harness read log fields from the wrong level. It now releases Escape, Space and the button on any exception, and reports the input state in its summary.
4. **The first design could arm a neighbour's command** (evaluator M1, found by a sweep of every command). The menu was redesigned as one ring at a time, centred on the pointer (ADR-0014).
5. **The foreground guard stopped the suite at its second scenario (harness).**
   - Diagnosis: the Explorer window was visible at the right place, but `SetWindowPos(HWND_TOPMOST)` returned TRUE without making it topmost. The terminal stayed in front.
   - Cause: the first harness started from a fresh shell opens Explorer in the foreground. The next one, started from the same shell, has no foreground rights, and Windows ignores the raise.
   - Fix: every scenario runs from its own shell. The harness retries the check five times and records the window state on each refusal. It still injects nothing while the window is covered.
6. **fast 100 ms, first cycle: Tools opened instead of Convert (harness).**
   - The host log shows the overlay at 31.6 ms and OLE's first `DragEnter` at 71.8 ms, on the first drag after the host started. The harness's move to the category and its move to the command both came before that.
   - The drop target therefore saw one sample, 120 px straight up from the centre, which is a stroke into Tools. Nothing ran.
   - The harness now waits, within the same release time, until the host has opened the category before it turns to the command. A release before that is made on the category.
   - Any category that opens must be the wanted one. A drop is required whenever the host had opened the category.
   - Product limitation, recorded in ADR-0014: movement before the first `DragEnter` is not visible to the drop target.
7. **"outside" did not release outside (harness, criterion too weak).**
   - On the top ring every deep stroke opens a category. The straight path down opened Resize, and the re-centred ring was still under the release point.
   - The harness refused to release there and cancelled with Escape. The old criterion (nothing ran) still passed.
   - The scenario now opens the category and leaves its ring through a command, to a point checked to be the folder's empty background. It requires the release to have happened over Explorer.
   - "escape", "category" and "unsupported" now also require where the release happened.
8. **No scenario reached Remove metadata.** For `--op metadata.strip` the harness gives its JPEG copy a comment segment, and checks that the output no longer contains it.

## Results (current design, final harness)
71 of 71 cycles passed in 22 scenarios. Every producing cycle had a valid output (by signature and container check) and a clean folder, and its sources were unchanged by hash. No cycle left the button or Space down, and no host process remained.

| Scenario | Command | Cycles | Result |
|---|---|---|---|
| png | Convert → To JPEG | 5 | 5/5; drop accepted after the command ring had been open 574–578 ms |
| jpg | Convert → To PNG | 3 | 3/3 |
| bmp | Convert → To WebP | 3 | 3/3 |
| tif | Convert → To JPEG | 3 | 3/3 |
| webp | Convert → To PNG | 3 | 3/3 |
| png | Resize → 50% | 3 | 3/3 |
| jpg | Compress → Smallest | 3 | 3/3 |
| png | Compress → Lossless | 2 | 2/2 |
| png | Rotate → Right | 3 | 3/3 |
| jpg | Rotate → 180° | 2 | 2/2 |
| png | Tools → Image info | 3 | 3/3: info box shown, no file written |
| jpg (with a comment segment) | Tools → Remove metadata | 3 | 3/3: `photo (no metadata).jpg` valid, the comment gone |
| batch, 12 files with Unicode names | Convert → To WebP | 2 | 2/2: 12 valid outputs per cycle, one accepted drop |
| batch, 12 files | Rotate → Left | 1 | 1/1 |
| category | release halfway into a category | 5 | 5/5: no category opened, no drop, nothing ran |
| unsupported (`notes.txt`) | release on "No actions" | 3 | 3/3: nothing ran |
| escape | Escape during the drag | 3 | 3/3: nothing ran |
| outside | open Convert, leave through a command, release over the folder | 3 | 3/3: released over Explorer, nothing ran |
| fast 25 ms | release as fast as possible | 5 | 5/5: released 40–71 ms after the trigger (the harness cannot be faster), refused as not armed |
| fast 100 ms | | 5 | 5/5: released 100–116 ms, one drop each, refused as not armed (command ring open 47–92 ms) |
| fast 400 ms | | 5 | 5/5: released 400–401 ms, accepted (command ring open 305–385 ms), valid output |
| edge (bottom-right corner) | Convert → To JPEG | 3 | 3/3, see below |

The fast runs check the rule exactly, per drop:
- A drop runs a command if and only if that command was under the pointer and its ring had been open for 150 ms.
- A release over a command always reached the overlay as a Drop. None was lost to the window below, and none was silently cancelled.

**Screen corner (`edge`):** the Explorer window sat in the bottom-right corner of the work area. The trigger came 30/45 px from the corner, so the clamped ring's centre was 129 px away, deep inside the Compress sector.
- After the pointer had stayed there through repeated DragOver calls, no menu change had happened.
- The pointer then went to the centre and stroked into Convert. Only Convert opened. Its ring was pushed aside by the corner too, so the pointer went to that ring's centre before stroking to To JPEG.

## Measurements (host log, all 22 runs)
| Metric | Result | Budget |
|---|---|---|
| Trigger → overlay shown, warm (n = 49) | p50 12.1 ms, p95 16.4 ms, max 19.7 ms | ≤ 50 ms p95 |
| Trigger → overlay shown, first activation of a host (n = 22) | p50 28.2 ms, p95 34.1 ms, max 36.9 ms | ≤ 120 ms |
| Trigger → first OLE DragEnter on the overlay (n = 71) | p50 23.7 ms, p95 90.0 ms, max 96.2 ms | — (see finding 6) |
| Host private bytes at the end of a run | 9.4–16.1 MB | ≤ 100 MB |
| Host working set at the end of a run | 39.2–50.4 MB | ≤ 150 MB |

The memory figures are taken right after the last cycle, not after 2 minutes idle, so they are an upper bound for the budget's measurement.

Not covered: multi-monitor and mixed DPI (one monitor), the Desktop as the drag source, other DPI settings in Explorer. These remain on the manual protocol.
