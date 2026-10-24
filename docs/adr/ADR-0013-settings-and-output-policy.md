# ADR-0013: Settings foundation and output policy

- **Status:** Accepted (2026-10-21), for the data and config layer. A settings UI is deferred to the UI phase.
- **Date:** 2026-10-21
- **Related requirements:** SAFE-001, SAFE-002, SEC-001, UX-003

## Context
V1 needs persisted preferences: the trigger key, where outputs go, encoder qualities and resize defaults, with a theme placeholder for later. There is no settings UI yet. The data must be local, contain no secrets, and survive crashes, corruption and version changes.

## Decision
- **Model (`WinTools.Core.Settings.AppSettings`):** an immutable record with safe defaults.
  - `Schema` (1 in V1; 2 since ADR-0021, which also groups the file in sections).
  - `TriggerKey`: Space by default; allowed values are Space and F8–F12. Modifiers are never allowed, because they change Explorer's drop effect.
  - `OutputPolicy` and `OutputFolder`: SameFolder by default, or Folder with a path.
  - `JpegQuality` and `WebPQuality`: 90 by default, range 1–100.
  - `ResizeWidth` and `ResizeHeight`: 1600 each by default; 0 leaves that side free.
  - `KeepAspectRatio` and `NoUpscale`: both true by default.
  - `Theme`: "System", a placeholder.
  - `Normalized()` repairs invalid values instead of failing:
    - An unknown trigger falls back to Space.
    - Out-of-range qualities are clamped.
    - An invalid or non-canonical output folder falls back to the same folder.
  - `CustomResizeId()` maps the resize defaults to a parameterised operation id.
- **Store (`WinTools.Host.SettingsStore`):** the file is `%APPDATA%\WinTools\settings.json`; `--settings <path>` overrides it for tests.
  - **Atomic save:** the new file is written and flushed to `settings.<guid>.tmp` in the same folder, then swapped in with `File.Replace`, or moved into place the first time.
  - **Corrupt file:** it is moved aside to `settings.corrupt-<timestamp>.json` and defaults are used.
  - **Newer schema:** the store becomes read-only, and the file is never moved aside or overwritten, even when this version cannot parse it. The `schema` field is read case-insensitively, as a number or a numeric string. The fields this version understands are used if they parse; otherwise defaults are used.
  - **Unwritable path:** `Save` never throws. A folder at the settings path, a read-only or locked file, or a parent that is a file makes it return false; the host logs that and runs on with the settings in memory.
  - **Invalid output folder:** it is repaired to the same folder, and the load note in the log says so. There is no UI yet to tell the user directly.
  - **First run:** the defaults are written out, so the file can be edited by hand.
- **Output policy:**
  - Requests carry `OutputDirectory` (null means the same folder).
  - The worker validates it: canonical (`PathPolicy`), existing, and neither a link nor inside a link.
  - The temporary file and the final name are created in that folder.
  - The host's success check uses the same folder.
- **Qualities** reach the worker per request and apply to conversions to JPEG and WebP. Compression levels keep their own fixed qualities.

## Alternatives considered
- **Registry storage:** less transparent, and harder to back up or reset.
- **`Microsoft.Extensions.Configuration`:** a dependency without a need, since one JSON file is enough.
- **Rejecting invalid settings:** that would make the app unusable after a hand edit. Repairing to safe defaults is friendlier and safe.

## Consequences
- The UI phase only needs to edit an `AppSettings` and call `SettingsStore.Save`.
- An output folder on another volume works: the temporary file is created in the destination folder, so the final step is still a same-volume rename.

## Evidence / experiments
- `tests/WinTools.Core.Tests/SettingsAndMetadataTests.cs` (`AppSettingsTests`): defaults, repair, the folder rule, a JSON round trip and rejection of garbage, and custom resize ids.
- `tests/WinTools.Worker.Tests/MetadataBatchSettingsTests.cs` (`SettingsStoreTests`): missing file, a corrupt file moved aside, a newer schema left untouched, and 20 atomic saves with no temp file left. `BatchTests` covers the output folder, a missing folder, and JPEG quality.

## Evaluator review
Independent evaluator of PR #4 (2026-10-21): MERGE AFTER FIXES. The settings findings are fixed and pinned by `SettingsHardeningTests`:
- The newer-schema protection could be bypassed (a value this version cannot parse, other casing).
- A failed first-run save crashed the host.
- The invalid-folder fallback was silent.

Also fixed: NT device paths (`\??\`) were accepted as an output folder.
