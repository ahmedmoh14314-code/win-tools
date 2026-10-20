# Testing rules

- **Tests are specification.** Logic (capability resolution, command model, job state machine, filename collision, result model) is developed test-first.
- **Never weaken a test to make implementation pass.** If a test is wrong, say so and fix the test in its own commit with rationale.
- **Levels** (add as the code exists, not before):
  - Unit: capability resolution, command logic, path and collision handling, job state transitions.
  - Integration: each conversion adapter against golden sample files, with checksums or perceptual comparison as appropriate.
  - Malformed/hostile input: truncated, mislabeled, zero-byte, huge-dimension and fuzz-style files must fail cleanly.
  - Lifecycle: cancellation, timeout, worker crash/kill, partial-output cleanup, original untouched (hash before and after).
  - Edge: Unicode, long and quoted paths; name collisions; read-only targets; network and removable drives.
  - Interaction (recorded manual protocol until automated): multi-monitor, DPI 100/125/150/200%, screen edges and corners, Explorer and Desktop as drag sources.
  - Performance: benchmarks against `docs/performance/budgets.md`; leak checks for overlay show/hide cycles.
- **Sample files** live under `tests/samples/` with provenance and license noted; never commit personal files.
- **Verification output must be read**, not assumed: quote the relevant lines in the report.
- A `verified` requirement cites the test name(s) or the recorded manual protocol run.
