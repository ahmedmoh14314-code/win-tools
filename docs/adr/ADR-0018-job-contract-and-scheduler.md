# ADR-0018: Job contract v2 and the job scheduler

- **Status:** Proposed (V2 PR 1.2)
- **Date:** 2026-10-24
- **Related:** ADR-0005 (worker process), ADR-0007 (capabilities, amended for PR 1.1), ADR-0011 (result model), ADR-0015 (worker limits)

## Context
The V1 job pipeline was the image module's own:
- `ConversionRequest` held one source path and the image qualities;
- `ConversionResult` held one output path or an `ImageInfo`;
- `JobRunner` ran one batch at a time behind a global gate.

V2 adds modules whose jobs take several inputs (merge PDFs, add to ZIP), produce several outputs (split, extract) or only facts (SHA-256, file info), and run in other workers. The V2 plan (§5) lists what the pipeline needs: several inputs and outputs, a staging-folder commit, parameters, progress, cancellation, a queue instead of the global gate, per-module limits, and Mark-of-the-Web propagation.

## Decision
1. **One contract for every module** (`WinTools.Core.Jobs.JobContract`):
   - `JobRequest(Id, CapabilityId, Inputs[], OutputDirectory?, Parameters)`. Parameters are a string map that the module validates; an unknown parameter is refused, not ignored. Secrets never travel in it; Stage 2 adds a separate stdin-only channel.
   - `JobResult(Id, Success, Error, Message, Outputs[], Facts[])`. Each output carries its path and the SHA-256 of the bytes the worker validated. Each fact is a label, a value and a notice flag.
   - A per-file capability (`CapabilityShape.PerFile`) gets one request per file; a selection capability gets one request with every input.
2. **The line protocol has an envelope.** Each line is a `WorkerMessage` with a `Type`: `request`, `result` or `cancel`.
   - Either side ignores a type it does not handle, so a module can add progress later without changing the protocol.
   - The host skips non-result lines while it waits for a result.
3. **The image worker speaks v2 at its boundary only** (`ImageJobs`):
   - Its per-file request and result stay module-internal: generalise only what two modules need.
   - It takes exactly one input. Its qualities arrive as parameters (`jpegQuality`, `webpQuality`, 1–100).
   - "Image info" returns the info panel's rows as facts, built in the worker.
4. **Believing results is the module's knowledge.** `IJobModule` (host side) says whether a success claim holds up and which temporary files a killed worker may leave. For images: the allowed name, the real format and the SHA-256, as before. `WorkerClient` is the same for every module.
5. **The scheduler** (`JobScheduler`) replaces the global gate:
   - Each module has a first-in, first-out queue (a channel with a single reader) and runs one job at a time. Jobs of different modules run side by side.
   - A queued job that is cancelled never reaches a worker. A running one stops taking requests: the host sends `cancel`, waits 1 s for an answer, then kills the worker.
   - The image worker handles one job at a time and cannot read `cancel` meanwhile, so it is killed. That is the V1 behaviour.
   - `CancelAll` (host exit) marks every job before signalling any token. Signalling runs the running job's continuations at once, and the queue could otherwise start the next job before its token was signalled (found by `Cancel_all_stops_queued_and_running_jobs`).
   - What runs a module's requests is an `IJobExecutor`: the process pool, or a fake in tests.
6. **Mark of the Web** (`MarkOfTheWeb`, in the worker):
   - When the source's `Zone.Identifier` has ZoneId 3 (internet) or 4 (restricted), the output gets a stream with that ZoneId only. URLs are not copied.
   - A mark that is present but unreadable counts as zone 3.
   - The stream is written on the temporary file before the rename, so no output is ever visible unmarked.
   - On volumes without alternate streams (FAT, exFAT) the mark is dropped, as Windows itself does.

## Deferred, with reasons (YAGNI)
- **Staging-folder commit for several outputs.** Its first consumer is PDF split and extract (Stage 2.1). It is built there, with its tests (collision, failure part-way, a reparse point at the destination), rather than as an unused library now.
- **Progress lines.** The envelope admits them and both sides skip them. A worker that sends them arrives with PDF; image jobs are short, and the host already reports progress per file.
- **Per-module limits beyond today's.** The image module keeps ADR-0015's limits through its `WorkerClient`. Each new module gets its own client with its own timeout, lanes and memory cap when it arrives.

## Alternatives considered
- **Keep the image contract and add one per module.** Rejected: the host would grow one runner per module, and logging, cancellation and feedback would be duplicated.
- **An object payload in the request.** Rejected: the payload crosses a process boundary, and a string map validated by the module is auditable.
- **gRPC or named pipes instead of stdin/stdout lines.** Rejected: the line protocol is proven, bounded and easy to fuzz, and it needs nothing new for V2.
- **`SemaphoreSlim` per module as the queue.** Rejected: waiters are not documented as first-in, first-out. A channel with one reader is.

## Consequences
- Every image test that went through the worker process now goes through the v2 path. `ImageJobsCompat` (tests only) sends their requests as `JobRequest` and reads the answers back, so their assertions did not change.
- Feedback still takes its words from the image operation behind the capability. Module-supplied words come with the second module (PR 1.4).
- The worker client no longer knows about images. A second module needs its own `IJobModule`, worker and parameters, and nothing in the scheduler.

## Evidence
- `JobContractTests` (Core, 8): framing, Unicode, parameters, stable error names, unknown types, malformed and oversized lines.
- `WorkerProtocolV2Tests` (6, the real worker process): ignored message types, malformed requests, refused inputs and parameters, and the host skipping non-result lines.
- `JobSchedulerTests` (7): order within a module, modules side by side, cancelling queued and running jobs, `CancelAll`, request shape and unknown modules.
- `MarkOfTheWebTests` (10): 5 failed before the change; the 5 others already passed and still do.
- **Unchanged behaviour:** `MenuGoldenTests` and every earlier test pass. Core 320, Worker 282 (Debug, which includes the fault-injection tests that Release compiles out); build with 0 warnings.

## Evaluator review
One round (2026-10-24). **Blocker, fixed:** CI failed. A Debug-only fault-injection test matched the old crash message; it now counts crashes by `ErrorCode`, in its own commit. Verification since runs Debug as well as Release.
**Major, fixed:** facts are worker text that reaches the user. A module now believes facts only within `JobFacts` (at most 32; label 1–40 and value up to 200 characters; no control or bidirectional-formatting characters; no null entries or lists), a failure that carries facts or outputs is not believed, and the presenter shows facts only from a success. `ImageModuleBelievabilityTests` builds hostile shapes by hand, which the compatibility shim could not.
**Minors, fixed:** a throwing job (or its logging and feedback) no longer ends its module's queue; the exit awaits `CancelAll` for up to 3 s, so workers get their grace and temps are cleaned; a source on a volume without named streams (FAT, exFAT) carries no zone instead of being read as internet; a mark that cannot be written on a volume with streams fails the job; a custom zone above 4 is carried as 4.
**Minors, deferred:** a cancel in the middle of a progress line loses that line (no worker sends progress yet; fixed with PDF's progress lines); the presenter's one set of progress counters, which a second module running alongside would mix (PR 1.4); a line with no type is skipped rather than failed fast.
