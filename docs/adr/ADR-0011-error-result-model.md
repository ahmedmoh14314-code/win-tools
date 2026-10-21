# ADR-0011: Error and result model across module and process boundaries

- **Status:** Accepted (2026-10-21) for the first product slice; evaluator review recorded below
- **Date:** 2026-10-19
- **Related requirements:** ARCH-004, SAFE-003

## Context
Jobs cross a process boundary (ADR-0005). Failures (unsupported input, corrupt input, engine error, timeout, cancellation, worker crash, disk full, permission denied, output collision) must map to deterministic, actionable states in the UI and must never be mistaken for success. Exceptions cannot cross the IPC boundary; a serialisable result shape is required.

## Decision
Results cross the process boundary as data, never as exceptions. `ConversionResult` carries `success`, an `ErrorCode` (a stable name in JSON) and a short message without file contents, plus `outputPath` on success. The worker catches everything per request and maps it to a code. The host maps the ways the process itself can fail (`WorkerCrashed`, `WorkerTimeout`, `WorkerProtocolError`) to the same shape. Codes in this slice: `InvalidRequest`, `UnsupportedOperation`, `SourceNotFound`, `SourceNotReadable`, `InputTooLarge`, `ContentMismatch`, `DecodeFailed`, `ImageTooLarge`, `EncodeFailed`, `OutputDirectoryNotWritable`, `OutputDirectoryIsLink`, `OutputNameInvalid`, `DiskFull`, `NoFreeOutputName`, `OutputValidationFailed`, `WorkerCrashed`, `WorkerTimeout`, `WorkerProtocolError`, `Internal`.

## Alternatives considered
- Exceptions everywhere: rejected across the IPC boundary; acceptable inside a module if translated at the edge.
- Result objects everywhere: consistent, slightly more verbose.

## Consequences
- The host never needs to parse exception text, and the codes are stable for logs and future UI messages.
- Not every code has a test yet: `OutputDirectoryIsLink`, `EncodeFailed`, `DiskFull` and a process-level `WorkerProtocolError` are untested (listed in the PR).

## Evidence / experiments
Unit tests for every failure category; lifecycle tests show each failure surfaces correctly and leaves no false-success output.

## Evaluator review
Evaluator for the first product slice (2026-10-21), working from the code, the tests, its own probes against the built worker, and the raw end-to-end logs.
- Pass 1: CHANGES REQUIRED. B1: a rotated JPEG took about 5 minutes through the flip-rotator. M1: a folder with a trailing dot converted its twin. M2: a lone surrogate was altered on the pipe. M3: damaged image data inside an intact container was reported as success. M4: grace/DragEnter race. M5: the harness could not see a lost drop. Plus 12 minor items.
- Pass 2: MERGE once the minor items are fixed or recorded. The evaluator re-probed and confirmed B1 and M1–M3 fixed; no blocker or major remains. The minor items found in pass 2 are fixed (alternate-data-stream paths, out of memory reported as ImageTooLarge, job-object assignment logged, path policy re-checked in the worker client) or recorded as known limitations in the PR.
