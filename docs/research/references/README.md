# Reference implementations — index and licensing ledger

Clean-room rule for everything in this folder: we study behaviour, architecture, tests and failure reports. **No source was copied into this repository.** Clones were depth-1, read-only, in a local scratch directory, never built or executed. Licenses below were read from the license file in each clone on 2026-10-20 (not from memory or metadata alone).

| Reference | URL | Commit read | License (file) | What we studied | Source reuse | Attribution if reused | Status |
|---|---|---|---|---|---|---|---|
| Action Wheel | github.com/cmm-cmm/Action-Wheel | `da021af` | MIT (`LICENSE.txt`, © 2026 Minh Pham); bundled font OFL-1.1 | WinUI 3 radial launcher: hooks, overlay, transparency, config, packaging, pitfalls — [action-wheel.md](action-wheel.md) | Allowed; **not recommended** (concepts only, see its §4) | MIT notice if any file is ever copied | Reference |
| FileConverter | github.com/Tichau/FileConverter | `5dce6c0` | **GPL-3.0** (`LICENSE.md`) | Conversion architecture, engine invocation, Explorer integration, 579 issues — [fileconverter.md](fileconverter.md) | **Not allowed** for our shipped product (GPL) | n/a | **Research only** |
| DragShift | github.com/llamaboi55/DragShift | `88abe0c` | MIT (`LICENSE`) | Closest product analogue (Explorer drag → key → radial drop): trigger, hide timing, e2e tests — [other-radial-implementations.md](other-radial-implementations.md) | Allowed; not recommended | MIT notice | Reference |
| Gradial | github.com/longitudezero/Gradial | `6617aa1` | MIT (`LICENSE`) | Native Rust overlay: per-monitor prewarm, edge assist, hook hygiene, evidence labels | Allowed; not recommended | MIT notice | Reference |
| AutoHotPie | github.com/dumbeau/AutoHotPie | `6263a40` | MIT (`LICENSE.md`; a second `license.txt`, also MIT) | Pie-menu issues: screen-share artefacts, stale overlay blocking clicks, working-set trimming | Allowed; not recommended | MIT notice | Reference (narrow) |
| RadialDock | github.com/Wwp2/RadialDock | `7b3015a` | Apache-2.0 (`LICENSE`) | Excluded after review except one idea (hotkey registered only during a drag) | Allowed | Apache NOTICE + license | Excluded (low evidence) |
| rovyl | github.com/HenryCauan/rovyl | `5c4a579` | **GPL-3.0** (`LICENSE`) | Hook-death issue report only | **Not allowed** | n/a | **Research only** |
| PowerToys | github.com/microsoft/PowerToys | `ef43bf8` | MIT (`LICENSE`) | Keyboard/mouse hook hosting, "stops working" issues — [input-hook-robustness.md](input-hook-robustness.md) | Allowed; not recommended | MIT notice | Reference |
| AutoHotkey | github.com/AutoHotkey/AutoHotkey | `0b8e6fa` | **GPL-2.0** (`license.txt`) | Hook thread priority and lifetime | **Not allowed** | n/a | **Research only** |
| libuiohook | github.com/kwhat/libuiohook | `2a2d40f` | **LGPL-3.0** (`COPYING.LESSER.md`, "Version 3") | Hook re-install heuristics | Would need dynamic linking + ADR (`dependencies.md`) | LGPL obligations | Research only |
| SharpHook | github.com/TolikPylypchuk/SharpHook | `3bb6ecb` | MIT (`LICENSE`) | .NET hook wrapper issues | MIT for its own code, but it **ships native libuiohook (LGPL-3.0)**: LGPL obligations apply to any use | MIT notice + LGPL obligations | Reference |
| globalmousekeyhook | github.com/gmamaladze/globalmousekeyhook | `662b251` | MIT (`LICENSE.txt`) | .NET hook issues | Allowed; not recommended | MIT notice | Reference |
| microsoft-ui-xaml (2 source files) | github.com/microsoft/microsoft-ui-xaml | `1af995b` | MIT (repository license) | How WinUI 3 decides window transparency — [winui3-overlay-feasibility.md](winui3-overlay-feasibility.md) | n/a (read to confirm behaviour) | n/a | Reference |
| Tangerine (macOS app) | tangerineformac.com | n/a | Proprietary | **Public behaviour only** (site, store page, videos, reviews); no binary inspection — [tangerine-behaviour.md](tangerine-behaviour.md) | Not applicable | n/a | Behavioural inspiration only |

## Conclusions
- Nothing studied is copied or planned to be copied. Where a MIT component looked reusable, the reviews name it under "possible reuse (not performed)" and recommend re-deriving it, because each is small and our versions need tests anyway.
- GPL/AGPL references (FileConverter, rovyl, AutoHotkey) are research only for as long as the shipped product is not GPL (`docs/engineering/rules/dependencies.md`). FileConverter also ships a GPL FFmpeg build and AGPL Ghostscript; neither is acceptable for us.
- If any code is ever reused, it gets its own dependency-matrix entry first, with the license text and notice kept alongside it.
