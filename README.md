# win-tools (working name)

A lightweight, local-first Windows desktop utility: drag a file, press a trigger,
a radial menu appears around the cursor, drop the file on an action, and the
operation runs safely (e.g. `photo.png` → Convert → WebP → `photo.webp`).

**Status: functional V1 in progress. Conversion between PNG, JPEG, WebP, BMP and TIFF works end to end from Windows Explorer. Resize, compress, rotate, remove metadata, image info and batch processing (up to 1000 files per drop) are implemented and reached through a two-level radial menu (categories, then commands; ADR-0014). Settings live in `%APPDATA%\WinTools\settings.json` (ADR-0013). SVG and HEIC input are deferred (ADR-0012). No installer, tray or settings UI yet; see `docs/progress.md`.**

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
| `tests/` | Unit and integration tests (run in CI) and `WinTools.EndToEnd`, a local Explorer harness. |
| `spikes/` | Throwaway experiments kept as evidence; never referenced by product code. |

## Build and run

Requires the .NET 10 SDK (`global.json`) on Windows 10/11 x64.

```
dotnet build WinTools.slnx
dotnet test tests/WinTools.Core.Tests
dotnet test tests/WinTools.Worker.Tests
src/WinTools.Host/bin/Debug/net10.0-windows/win-x64/WinTools.Host.exe
```

While the host runs, drag an image (PNG, JPEG, WebP, BMP or TIFF) in Explorer, press **Space** without releasing the mouse, and drop the file on the ring. The output appears next to the source, as `photo.jpg` or `photo (1).jpg` if that name is taken. **Escape** cancels. **Ctrl+C** in the host's console exits.
