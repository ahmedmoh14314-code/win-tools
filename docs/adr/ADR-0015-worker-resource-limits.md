# ADR-0015: Worker resource limits: pixels, memory cap, memory-bounded pool

- **Status:** Accepted (2026-10-21), TASK 5 hardening
- **Date:** 2026-10-21
- **Deciders:** product owner (roadmap TASK 5: "lower image limits, add a job-object memory cap")
- **Related requirements:** SEC-001, SAFE-003, ARCH-004, PERF-002

## Context
The worker allocated images up to 100 MP and could commit as much memory as the system granted. Up to 4 workers run in parallel (ADR-0005).

Measured peak commit per worker (`docs/performance/2026-10-21-worker-memory.md`):
- It grows linearly, about 21 MB per megapixel in the worst case (conversion to BMP).
- At 100 MP that is about 2.1 GB per worker, or 8.6 GB for four. The development machine had 4.3 GB of physical memory available while it was measured.

A hostile file could also make a decoder allocate natively, outside any .NET limit.

## Decision
- **Pixel limit: 50 MP** (`ImageLimits.MaxPixels`, 65,535 per side).
  - It is checked from the header before any pixel buffer is allocated, for decodes and for resize targets alike.
  - The worst measured peak at the limit is 1.06 GB. The limit covers 48 and 50 MP phone photos and cameras up to 50 MP. Larger images are refused with ImageTooLarge.
- **Per-worker memory cap: 1.5 GB** (`WorkerJobObject.WorkerMemoryLimitBytes`), set through `JOB_OBJECT_LIMIT_PROCESS_MEMORY` on the host's job object.
  - It covers native and managed commits. An allocation past it fails inside the worker: managed and WIC E_OUTOFMEMORY failures are reported as ImageTooLarge; a codec that cannot handle the failure crashes its worker, which the host reports.
  - The worker's runtimeconfig sets `System.GC.HeapHardLimit` to the same value. The worker joins the job only after it starts, and a GC that read the job's limit first would otherwise cap its heap at 75 % of it, depending on timing.
- **Commit-bounded pool:** at the start of a batch, the number of workers is
  `clamp(min(processors / 2, (available commit − 1 GB) / cap), 1, 4)`.
  - Even if every worker reaches its cap, the batch can never be what exhausts the system's commit, which would make allocations fail in every program. One worker always runs.
  - Commit (RAM plus page file, `ullAvailPageFile`) is used rather than free physical memory. Physical memory only decides paging speed, and on the development machine in normal use (about 2 GB free) it would have meant one worker for every batch.
  - On that machine, with 4.6 GB of commit left, the rule gives 2 workers.

## Alternatives considered
- **Keep 100 MP and rely on the pagefile:** four large conversions could need 8.6 GB. The machine would page heavily, and on an 8 GB machine allocations would fail at random.
- **64 MP:** the BMP peak is 1.35 GB, which leaves too little headroom under a 1.5 GB cap. A 2 GB cap would allow only two workers on a typical busy 16 GB machine.
- **A job-wide memory limit for all workers together:** a large file would then fail because other files of the same batch happened to run at the same time, not because of its own size. That is a non-deterministic failure.
- **The host reads image headers and schedules by pixel count:** this puts parsing of untrusted files in the host process, which ADR-0005 rules out.
- **Lower the peak by streaming the encode and validation:** possible later. It does not remove the need for a cap against hostile input.

## Consequences
- Images over 50 MP (for example 61 MP cameras and the 108/200 MP modes of phones) are refused with an actionable message. This is a V1 limitation.
- On a machine short of commit a batch runs with fewer than 4 workers, and small files then go slower than they could. The batch log records the number used (`job.done.workers`).
  - In the soak runs (`docs/performance/2026-10-21-soak.md`), 2 workers converted 50 small files in 291 ms (p50).
  - A first version bounded by free physical memory ran 1 worker on the same machine. The soak run exposed this, and it led to the commit rule.
- `ContainerCheck` derives its PNG inflate ceiling from the same limit.

## Evidence / experiments
- `docs/performance/2026-10-21-worker-memory.md`: the measurements above.
- `tests/WinTools.Worker.Tests/ResourceLimitTests.cs`:
  - the exact pixel boundary;
  - a 50 MP BMP rotated under the cap (the costliest case at the limit);
  - native commits past the cap refused. This is a Debug-only fault, and the test fails when the cap is removed (checked by mutation);
  - the lane rule, including the 4.6 GB case measured on the development machine;
  - the GC heap limit equal to the cap.

## Evaluator review
- **Security review (2026-10-21):** an independent reviewer found M1, an uncapped worker memory, which this ADR fixes. The other findings and their outcomes are in `docs/security/2026-10-21-hardening.md`.
- **Final evaluator (2026-10-21): MERGE**, with no blocker or major finding. It confirmed:
  - the x64 layout of `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`, the flags, and the native cap proven at run time;
  - the GC limit equal to the cap;
  - the lane rule, with no overflow and the reserve floored at zero;
  - the TIFF exact-count rule: bounded, and no conformant file refused;
  - the docs against the raw soak records.
- **Its minor findings, applied before merge:**
  - the stderr drain's exception is observed;
  - the line limit is stated in characters;
  - the memory-heavy tests say what they assume;
  - a failed job-object assignment is recorded as a residual risk.
