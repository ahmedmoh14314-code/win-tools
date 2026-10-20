# Product architecture proposal (V1)

- **Status:** Proposal, 2026-10-20. Not implemented. Feeds ADR-0001/0003/0004/0005/0006/0007/0008.
- **Inputs:** Spike 001 (`docs/research/2026-10-19-interaction-spike.md`), reference review (`docs/research/reference-implementations.md`), product brief and requirement ledger.
- **Scope guard:** V1 = drag→trigger→radial→drop for JPG/PNG/WebP with Convert/Compress/Resize. Anything for other categories is listed under "Not now" and has no code.

## 1. Process model
```
Explorer / Desktop (drag source, untouched)
        │ OLE drag (CF_HDROP)
        ▼
Resident host process  (per-user, unelevated, single instance, tray)
  ├─ Input thread ........ LL keyboard + mouse hooks → posts events
  ├─ UI thread ........... Interaction session, overlay (IDropTarget), tray, results
  └─ Job manager ......... queue, timeouts, cancellation, worker supervision
        │ named pipe, length-prefixed JSON (one channel)
        ▼
Worker process  (on demand, one per batch, Windows job object: kill-on-close, memory cap)
  └─ Conversion adapters → engine (V1: Windows Imaging Component)
```
Why two processes and not more: engines parse untrusted files and can crash or hang (FileConverter #790; `security.md`), so they leave the resident process. Where the trigger detection lives (in-process LL hooks, Raw Input, or a helper process) is **open** (ADR-0005): the spike's 1000-cycle run was a near-idle host and measured callback time from inside the managed callback, so it cannot show GC-induced entry delay; experiments E1/E2 decide it (§4). A separate settings process is deferred until ADR-0001 is decided (only needed in the hybrid option).

## 2. Modules (assemblies) and what each owns
| Assembly | Contains | Depends on | Why it is separate |
|---|---|---|---|
| `WinTools.Core` (plain `net10.0`, no Windows APIs) | Interaction session state machine, capability descriptors + resolver, command model, job state machine, output naming, result types, settings model + validation | nothing | The compiler enforces "no Win32/UI in logic"; everything here is unit-tested (TDD) |
| `WinTools.Windows` | P/Invoke (CsWin32 where it fits), hook host, monitor/DPI queries, OLE drop target, CF_HDROP reading, single instance, tray, startup registration, job object | Core | Interop isolated behind thin, tested wrappers (`windows-integration.md`) |
| `WinTools.App` (host exe) | Composition root, overlay presenter (stack per ADR-0001), results/notification surface, settings UI, diagnostics surface | Core, Windows | The only assembly that knows the UI framework |
| `WinTools.Worker` (exe) | Pipe server, adapter registry, V1 WIC adapter, output writer (temp → validate → atomic move) | Core | Crash/hang isolation; engines load only here |
| Tests | `Core.Tests` (unit), `Windows.Tests` (interop wrappers, placement math), `Worker.Tests` (golden samples, hostile files, lifecycle), e2e harness (from the spike, run in Windows Sandbox/VM) | — | `testing.md` levels |

Not created: plugin system, DI container, MVVM framework, event bus, generic "engine" abstraction beyond one adapter interface. Composition is plain constructors in `App`.

## 3. Components
**App host.** Single instance (`Local\` mutex + registered window message to the first instance), tray icon on a hidden top-level `WS_EX_TOOLWINDOW` window (a message-only window does not receive the `TaskbarCreated` broadcast, so the icon would not come back after an Explorer restart), HKCU Run startup with quoted path and stale-entry check, clean shutdown (revoke drop target, unhook, cancel jobs, kill worker). If the tray icon fails, the app must still be quittable.

**Windows interaction layer.** Dedicated input thread pumping messages for both LL hooks; callbacks are allocation-free, do no I/O, never throw, and only post compact events. All-or-nothing install. Liveness: **event-driven only** (no polling timer, `PERF-001`): re-check on session unlock (`WTS_SESSION_UNLOCK`) and power resume, and, if Raw Input is registered, treat "`WM_INPUT` motion seen but no LL callback" as hook loss. Re-install only on such evidence, never blindly (re-installing changes the hook-chain position). If keys are ever swallowed, key-down, auto-repeat and key-up are swallowed as a pair (PowerToys #38089 class). A failed or lost hook puts the app in a visible degraded state ("trigger unavailable"), never silent. Monitor and DPI are read for the target point immediately before each show.

**Interaction session (Core).** The spike's state machine, carried over as specification: Idle → LeftDown → Dragging → OverlayShown → AwaitingDrop → Idle. Rules proven by experiment: no activation without an OLE-sized drag; trigger modifiers must match exactly; after a release with the drag inside, wait for OLE Drop/DragLeave (watchdog only, 5 s); 300 ms grace only without DragEnter; Esc ignored while a drop is pending; a shown overlay with no DragEnter within N ms hides itself (stale-overlay rule). Every transition is a unit test.

**Radial UI.** One pre-created overlay window, never activated, shown at the cursor, ring shifted so the cursor is always inside it near edges, every pixel of the ring hit-testable and nothing outside it. Sectors are rendered from the resolver's result after DragEnter (neutral ring before that). **Untested:** the spike only calls `QueryGetData` at DragEnter and reads paths at Drop. Reading HDROP and sniffing at DragEnter must be spiked: bounded asynchronous read off the UI thread with a deadline, cloud placeholders (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`/`OFFLINE`) skipped with a reason (SAFE-004), slow UNC/removable paths time out to a "cannot inspect" ring; non-HDROP sources (UX-008) and folders (UX-009) get an explicit ring state. The drop target returns `DROPEFFECT_COPY` or `NONE`, never `MOVE`. Hit-testing uses the same geometry and coordinate space as drawing. Rendering stack: ADR-0001 (Spike 002 decides).

**Capability resolver (Core).** Input: the dropped paths and a content-sniff result per file (first bytes read by a small bounded reader in the host, because an on-demand worker cannot meet the DragEnter latency; never the extension alone). Data: declarative capability descriptors (`format → operations → output formats/presets`). Output: the list of sector commands valid for *all* dropped files, or an explanation why none are. Adding a format or operation means adding a descriptor, not touching the UI.

**Command model (Core).** Immutable records: `ConvertTo(format, quality)`, `Compress(preset)`, `Resize(preset)`. A command plus a set of input paths is a job request.

**Job manager (App).** Queue per batch; per-item results (one bad file never aborts the batch); cancellation token and timeout per job; starts the worker on demand and supervises it (crash, hang, exit code); maps worker messages to user-visible results. States: Queued → Running → Succeeded | Failed(reason) | Cancelled | TimedOut. A failed job never looks like success.

**Worker boundary.** One anonymous pipe pair inherited by the worker at spawn (no name to squat), length-prefixed JSON messages with a protocol version: `Start(job)`, `Progress`, `ItemResult`, `Cancel`, `Done`. The host owns the job object; cancel = message, then kill after a grace period. Both stdout and stderr are not used for protocol (logged with a cap only). Spawn cost is measured before choosing per-batch vs pooled (ADR-0005).

**Conversion adapter interface (Worker).** `IConversionAdapter { bool CanHandle(SniffResult, Command); Task<ItemResult> RunAsync(input, command, tempOutput, CancellationToken) }`. One implementation in V1 (WIC), creating decoders/encoders **by CLSID or with `GUID_VendorMicrosoft`** after our own sniff, so third-party codecs installed on the machine are never fed untrusted input (SEC-003), and opening files as streams so long paths work (WIC filename APIs are MAX_PATH-bound). Success is decided by the adapter's return value plus a decode of the temp output by the output writer, never by parsing engine text.

**Output writer (Worker).** Temp file in the output directory → validate (decode, dimensions, non-zero) → reserve the final name by exclusive create (`name (2).png` style) → atomic move → delete temp on any failure. Refuses reparse points on the output path; handles long paths and Unicode. Originals are only ever opened for reading.

**Settings.** One JSON file in `%LOCALAPPDATA%`, source-generated serializer, schema version, atomic save shared with the output writer's primitive, invalid file left untouched and reported (defaults in memory), no file watcher in V1. V1 settings: output naming, JPEG quality, resize presets. The trigger is fixed in V1 (UX-003 is deferred).

**Diagnostics.** Local rolling log (size-capped), no file contents, paths only where needed for a failure; `EventSource` events for activation, overlay show, job start/finish (performance budgets); degraded-state surface; "copy diagnostics" button. Never modal dialogs from background code.

## 4. Open decisions and what settles them
| Decision | Options | Settled by |
|---|---|---|
| ADR-0001 UI stack | WPF everywhere vs resident Win32 overlay + WPF UI process | Spike 002 (reference review §5): DragEnter without movement, fall-through, idle private bytes of production-quality rendering, cold start |
| ADR-0004 edge rule | Shift ring vs shrink | One unit-tested placement rule + a corner run |
| ADR-0005 worker | Per batch vs pooled; spawn cost | Worker spawn/pipe spike (framework-dependent vs ReadyToRun) |
| ADR-0005 trigger detection | In-process LL hooks vs Raw Input always-on (+ LL hook only per session) vs helper process | E1: hook *entry* latency (`KBDLLHOOKSTRUCT.time`/`MSLLHOOKSTRUCT.time` vs callback entry) under induced GC churn in the chosen UI stack; E2: Raw Input `WM_INPUT` delivery during a real Explorer drag; E3 only if a helper is still plausible: pipe hop p95 |
| DragEnter capability | Sniff at DragEnter vs generic ring until Drop | Spike row: HDROP read + bounded sniff at DragEnter, latency and placeholder handling |
| Unwritable output folder | Fail vs fallback location | ADR with SAFE-005 |
| ADR-0006 engine for WebP **encode** | WIC has no built-in WebP encoder (to verify on the baseline) vs libwebp (BSD) behind the adapter | Documentation check + dependency-matrix entry |
| Image policy | Alpha flattening for JPEG, ICC, EXIF orientation, timestamps | Golden-sample tests before the first adapter is written (FileConverter lessons 9) |
| Result surface | Toast (needs app identity for unpackaged apps) vs tray balloon vs small results panel | ADR when the job manager is built |
| ADR-0008 install | Framework-dependent per-user installer vs trimmed self-contained; signing | Packaging spike after ADR-0001 |

## 5. Not now (YAGNI)
Nested/group radials, user-defined actions, configurable trigger (UX-003), video/PDF/Office engines, FFmpeg/ImageMagick/Ghostscript, context-menu integration, auto-update, portable mode, settings hot reload, thumbnails in the hub, multi-monitor overlay pool, helper process for hooks (unless E1/E2 say otherwise). Each has a recorded reason to revisit (reference review §1–§6).

## 6. First vertical slice once ADR-0001 is decided
Core session + resolver + one command (Convert PNG→JPG) + worker with WIC + output writer + overlay + tray, with tests at each level. Nothing else.
