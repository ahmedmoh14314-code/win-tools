# win-tools (working name)

A lightweight, local-first Windows desktop utility: drag a file, press a trigger,
a radial menu appears around the cursor, drop the file on an action, and the
operation runs safely (e.g. `photo.png` → Convert → WebP → `photo.webp`).

**Status: 0.2.0 Beta. Everything runs locally, with no account and no cloud, in English and Arabic.**

| Family | What it does |
|---|---|
| Images | JPG, PNG, WebP, BMP, TIFF, plus HEIC and AVIF input: convert, compress, resize, rotate, flip, crop presets, one-click adjustments, collage, remove metadata, to AVIF, to PDF |
| PDF | Merge, split, rotate, optimize, repair, protect, unlock, info, to PNG, JPG, TXT and MD, and Edit pages (reorder, turn, repeat, delete, extract) |
| Video and audio | Convert, compress, trim, split, crop, join, speed, extract or remove audio, frames, normalize, mono and stereo, info |
| Documents | Text, info and clean for Office files; CSV and TSV to Excel; Excel to CSV; Markdown to HTML; Office to PDF when LibreOffice is installed |
| Archives | ZIP (encrypted too), TAR, TAR.GZ, GZIP, and 7z and RAR extraction; Add to ZIP |
| Subtitles | SRT and VTT conversion, timing shifts, clean encoding, info |
| Files | Copy path, name or folder, move to, copy to, show in folder, open in Terminal, batch rename, SHA-256 |

The trigger is Space by default; F7, F9, F12 or a letter A to Z can be chosen in Settings. A letter works only while dragging files.

## Start here (every session)

1. `CONTRIBUTING.md` — the rules that apply to every session.
2. `docs/progress.md` — current phase, last known-good commit, next task.
3. `docs/product/brief.md` — the product brief and constraints.
4. `docs/product/requirements.json` — the requirement ledger (states, never deletions).
5. `docs/adr/README.md` — decisions made and decisions still open.
6. `docs/engineering/workflow.md` — the mandatory engineering workflow.

## Repository map

| Path | Purpose |
|---|---|
| `docs/engineering/rules/` | Focused rule files (architecture, security, testing, ...). |
| `docs/product/` | Brief, requirement ledger, scope decisions. |
| `docs/research/` | Evidence from spikes and API research. |
| `docs/architecture/` | Architecture documents once decided. |
| `docs/security/` | Threat model and security notes. |
| `docs/performance/` | Budgets and benchmark results. |
| `docs/adr/` | Architecture Decision Records. |
| `docs/engineering/` | Workflow, evaluator pattern, environment, git conventions, audits. |
| `scripts/` | Repository tooling (not product code). |

| `src/` | Product code: `WinTools.Core`, `WinTools.Windows`, `WinTools.Host`, `WinTools.Worker`. |
| `tests/` | Unit and integration tests (run in CI), `WinTools.EndToEnd`, a local Explorer harness, and `WinTools.Soak`, local leak and UI-stall runs. |
| `spikes/` | Throwaway experiments kept as evidence; never referenced by product code. |

## Build and run

Requires the .NET 10 SDK (`global.json`) on Windows 10/11 x64.

```
dotnet build WinTools.slnx
dotnet test tests/WinTools.Core.Tests
dotnet test tests/WinTools.Worker.Tests
src/WinTools.Host/bin/Debug/net10.0-windows/win-x64/WinTools.Host.exe
```

While the host runs, drag a file in Explorer, press **Space** without releasing the mouse, move to a command on the wheel and release.
