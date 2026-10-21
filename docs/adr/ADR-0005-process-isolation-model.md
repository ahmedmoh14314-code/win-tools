# ADR-0005: Process isolation and worker model

- **Status:** Accepted (2026-10-21) for the first product slice; evaluator review recorded below
- **Date:** 2026-10-19
- **Related requirements:** ARCH-003, ARCH-004, SAFE-003, SEC-001

## Context
Converters parse untrusted input and may crash, hang or exhaust memory. The main UI process must survive and stay responsive. Preferred model: Main app → Job → Worker process → Engine. Open questions: one worker per job vs a pooled worker; IPC mechanism (stdin/stdout framing, named pipes, anonymous pipes); progress reporting; cancellation (cooperative via IPC, hard via `Process.Kill`); timeouts; job objects to cap memory/CPU and kill child trees; whether to lower the worker's integrity level or use a restricted token; how the worker gets engine assemblies loaded on demand.

## Decision
**A bounded pool of disposable worker processes per batch, started on demand; line-delimited JSON over redirected stdin/stdout.**
- **Parallelism:** half the logical processors, capped at 4, and never more lanes than files. Each lane takes files from one queue with its own worker. Measured on 12 logical processors, 100 photos of 1200×900 converted to WebP:

  | Lanes | Time |
  |---|---|
  | 1 | 20.9 s |
  | 2 | 9.9 s |
  | 4 | 6.1 s |
  | 6 | 4.7 s |
  | 8 | 4.1 s |

  4 lanes give a 3.4× speed-up; more add little and multiply the worst-case memory.
- **Failure containment:** a crash, hang or protocol violation fails only the current file. The lane kills its worker, deletes that file's temporary file, and starts a new worker for the next file. A lane stops after 4 failures in a row, its first worker and 3 restarts (a success resets the count) and reports what is left as not run, so scattered bad files never end a long batch. One invalid file never takes down the rest of the batch.
- **Cancellation:** lanes stop taking files; unfinished files are reported as `Cancelled`, never as crashes.
- The host starts `WinTools.Worker.exe` (next to the host) when a drop runs a command. It uses `ProcessStartInfo` with no arguments, no shell, no window, a working directory it owns, and below-normal priority. The worker exits when its stdin closes. Nothing runs while idle.
- Protocol: one `ConversionRequest {id, operationId, sourcePath}` per line in, one `ConversionResult {id, success, error, message, outputPath}` per line out. Non-ASCII is JSON-escaped, so the pipe is ASCII. Lines are limited to 64 KiB. Stderr is kept, bounded to 4 KiB.
- Failure handling in the host (`WorkerClient`):
  - **Per-job timeout (60 s):** kill the worker tree.
  - **Exit or broken pipe:** report `WorkerCrashed`.
  - **Malformed or mismatched answer:** report `WorkerProtocolError`.
  - **A success claim** is believed only if its name is one the naming policy allows for that source and the file's SHA-256 equals the hash the worker reports for the output it validated.
  - **A Windows job object** with kill-on-close holds every worker, so a worker cannot outlive a crashed host.
  - After any of these, the host deletes the request's deterministic temporary file and reports the rest of the batch as not run.
- Output safety lives in the worker (`ConversionJob`):
  - The source is opened read-only.
  - The result is written to `~wintools-<id>.tmp` (hidden) in the destination folder, created exclusively, and flushed to disk. A partial temp file is deleted; a file of that name that the worker did not create is never touched.
  - It is read back, checked structurally and fully decoded.
  - It is then moved to the first free name with a no-overwrite rename (`MoveFileEx` without `REPLACE_EXISTING`), so a name taken concurrently is skipped, never replaced.
  - Any failure deletes the temporary file.
- One batch at a time; a second drop waits. No queue or scheduler.

## Alternatives considered
- **In-process conversion:** rejected. A codec crash or hang would take the resident host with it.
- **Long-lived worker:** rejected for now. Idle cost, and state that survives across jobs. Revisit if first-job latency becomes a measured problem (now about 170 ms including process start).
- **Named pipes / gRPC:** unnecessary for one parent and one child process. Redirected stdio is private to the pair.
- **Worker per file:** simpler, but costs one process start per file in a multi-file drop.

## Consequences
- A worker crash, hang or protocol violation costs only the current batch. The host stays responsive, which is tested by killing a worker in the middle of a job.
- The worker runs as the same user, without a sandbox. A restricted token or AppContainer is a later hardening step (threat model).
- If the host exits or dies mid-job (including Ctrl+C), the job object kills the worker. If that happens between the temp write and the rename, or if something else kills the worker, a hidden `~wintools-<id>.tmp` can remain. It is never a final name, and it is not cleaned by scanning (no idle file-system work). If job-object assignment fails, `job.done` logs `jobObject: false`, and the worker then exits only when its stdin closes.

## Evidence / experiments
- `tests/WinTools.Worker.Tests/WorkerProcessTests.cs`: a two-request batch in one worker; a failure code crossing the boundary; a crash after the temp write (Debug-only fault injection), leaving no output and no temp, source unchanged, and the next request reported as not run; a hang killed at a 3 s timeout and cleaned up; a missing worker executable; success claims outside the naming policy rejected; the host assembly not referencing the worker assembly.
- `tests/WinTools.Worker.Tests/ConversionJobTests.cs`: output safety (existing and concurrently created destinations, validation failure, unwritable folder, source deleted during the job, long and Unicode paths).

## Evaluator review
Evaluator for the first product slice (2026-10-21), working from the code, the tests, its own probes against the built worker, and the raw end-to-end logs.
- Pass 1: CHANGES REQUIRED. B1: a rotated JPEG took about 5 minutes through the flip-rotator. M1: a folder with a trailing dot converted its twin. M2: a lone surrogate was altered on the pipe. M3: damaged image data inside an intact container was reported as success. M4: grace/DragEnter race. M5: the harness could not see a lost drop. Plus 12 minor items.
- Pass 2: MERGE once the minor items are fixed or recorded. The evaluator re-probed and confirmed B1 and M1–M3 fixed; no blocker or major remains. The minor items found in pass 2 are fixed (alternate-data-stream paths, out of memory reported as ImageTooLarge, job-object assignment logged, path policy re-checked in the worker client) or recorded as known limitations in the PR.
