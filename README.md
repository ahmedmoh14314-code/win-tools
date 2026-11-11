# win-tools (working name)

A lightweight, local-first Windows desktop utility: drag a file, press a trigger,
a radial menu appears around the cursor, drop the file on an action, and the
operation runs safely (e.g. `photo.png` → Convert → WebP → `photo.webp`).

**Status: Beta. Everything runs locally, with no account and no cloud, in English and Arabic.**

This release is a converter for three kinds of files. Other tools in the code are hidden from it
(`docs/release/public-scope.md` lists them and why).

| Family | What it does |
|---|---|
| Images | JPG, PNG, WebP, BMP, TIFF, and HEIC and AVIF input: convert, compress (high quality, balanced, smallest, lossless), resize, put in a PDF |
| PDF & Office | Word, PowerPoint and Excel to PDF; PDF to Word; PDF to PowerPoint (one picture per slide: not editable); PDF to Excel (tables read from the PDF's text; not scans); PDF to JPG or PNG; images to PDF; merge, split, rotate; make smaller (lossless) |
| Video | MP4, WebM, MKV, MOV (and AVI, WMV, GIF input): convert, compress (smaller file, balanced, higher quality) |

**What needs another program.** Word, PowerPoint and Excel to PDF, and PDF to Word, need Microsoft Office or
LibreOffice (free) on the PC. WinTools never installs or downloads them; without them these commands are not on the
wheel, and the Features page says what is missing. Everything else works on a clean Windows PC.

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
