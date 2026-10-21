# Hierarchical radial menu: Explorer runs (2026-10-21)

Machine: the development PC (Windows 11 Pro 26200, 120 DPI, one monitor). Release builds. All input was injected by `tests/WinTools.EndToEnd` into a real Explorer window, and each run was judged against the host log.

The navigation is the product's own: point at the category, move out to the command, then wait at least the arm delay. Each run records whether the mouse button and Space were still down at the end; none was.

## Findings during the runs
1. **A command arc wider than its category made the path out cross a neighbour (first design).** The first run showed Convert closing and Tools opening on the way to "To JPEG". A switching rule fixed that path only, as finding 4 showed.
2. **The window region did not include the command arc (first design).** `CreatePolygonRgn` was called with a point count of 0, so the drag left the overlay at the command. The arc region no longer exists: the current design uses a plain circle.
3. **A harness crash once left the synthetic left button down.** The harness read log fields from the wrong level. It now releases Escape, Space and the button on any exception, and reports the input state in its summary.
4. **The first design could arm a neighbour's command** (evaluator M1, found by a sweep of every command). The menu was redesigned as one ring at a time, centred on the pointer (ADR-0014). The png and jpg rows below are from the first design and must be re-run.

## Results
| Scenario | Command | Cycles | Result |
|---|---|---|---|
| png (first design) | Convert → To JPEG | 1 + 5 | 6/6 passed: valid output, source unchanged by hash, clean folder; the drop was accepted after the category had been open 618 ms (first run) |
| jpg (first design) | Convert → To PNG | 3 | 3/3 passed |
| all scenarios (current design): png, jpg, bmp, tif, resize, compress, rotate, image info, category drop, unsupported, escape, outside, fast 25/100/400 ms, batch | — | — | **not yet run**: they need the desktop free (the foreground guard stopped the earlier suite twice because another window was in front of Explorer) |
