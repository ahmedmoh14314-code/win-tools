# Reference review — Tichau/FileConverter

| | |
|---|---|
| **Name** | File Converter (Tichau/FileConverter) |
| **URL** | https://github.com/Tichau/FileConverter |
| **License (verified)** | **GPL-3.0**. `LICENSE.md` in the clone opens with "GNU GENERAL PUBLIC LICENSE Version 3, 29 June 2007"; `README.md` § License says "GPL version 3"; every `.cs` file header carries a GPL v3 notice. The bundled `Middleware/ffmpeg/LICENSE` is also the GPL v3 text (the shipped ffmpeg is a `--enable-gpl` build, confirmed by the ffmpeg banner quoted in issue #780). Ghostscript (`Middleware/gs/gsdll64.dll`) is AGPL upstream. |
| **Snapshot studied** | commit `5dce6c0980635e3a6ae8d8acff235bcc9b1441a4` (2026-10-19), shallow clone, version 2.2.0 per `version.xml` / `Application/FileConverter/Application.xaml.cs` |
| **What was studied** | Conversion job model and scheduler, format/capability mapping, batch handling, output naming and collisions, job lifecycle/threading/cancellation, error surfacing, presets, FFmpeg / ImageMagick / Ghostscript / Office integration, process invocation details, temp files, configuration storage, shell extension and registration, installer/updater, logging; plus the full issue tracker (579 issues, 345 open / 234 closed, fetched with `gh` on 2026-10-20). |
| **Date** | 2026-10-20 |
| **Status** | **Research only — no code reuse.** GPL-3.0 code must not be copied, adapted line-by-line, or linked into win-tools. Everything below is a description of mechanisms in our own words with file-path citations, for architectural lessons only. The clone lives in a local scratch directory and was never built or executed. |

Reader orientation: FileConverter is the closest shipped analogue to our V1 (a Windows utility that converts files from Explorer, engines bundled, no backend). It is 10+ years old, single-maintainer, WPF on .NET Framework 4.8, and has a large, honest issue history. That history is the main value here: it shows which parts of this product class break in the field.

---

## 1. Architecture summary (our words, with evidence)

### 1.1 Shape
Two binaries plus bundled engines:

- **`FileConverterExtension.dll`** — a *managed* in-process COM shell extension (SharpShell 2.7.2, association "all files") that adds a "File Converter" submenu to the classic Explorer context menu. It is loaded **into Explorer and into any process that hosts shell context menus**. Source: `Application/FileConverterExtension/FileConverterExtension.cs`.
- **`FileConverter.exe`** — a WPF app that is launched *per conversion batch* with command-line arguments (`--conversion-preset "<name>" <file>...`), shows a job-list window, runs the jobs, and (optionally) exits itself a few seconds after success. Source: `Application/FileConverter/Application.xaml.cs` (argument parsing in `Initialize`, job creation in `RunConversions`).
- **Engines**: `ffmpeg.exe` (99 MB, GPL build) as a child process; ImageMagick via **Magick.NET-Q16-AnyCPU 14.10.2 in-process**; Ghostscript 10.02.1 (`gsdll64.dll`, 24.6 MB) loaded by ImageMagick for PDF input; Microsoft Office via **NetOffice 1.7.4.11 COM automation** of an installed Word/Excel/PowerPoint; a CD-ripper pair (`Ripper.dll`, `yeti.mmedia.dll`). Package list: `Application/FileConverter/FileConverter.csproj` lines 392–418; engine copy step is a post-build `copy` in the same file; install manifest `Installer/Product.wxs`.

There is **no worker process**. Everything that is not ffmpeg runs inside the UI process on plain `Thread`s.

### 1.2 Conversion job model
- `ConversionJob` (`Application/FileConverter/ConversionJobs/ConversionJob.cs`) is a mutable `INotifyPropertyChanged` class holding preset, input path, output path(s), `State`, `Progress`, `UserState` (localized status text), `ErrorMessage`, and a cancel flag. State enum (`ConversionState.cs`): Unknown → Ready → InProgress → Done | Failed. **There is no Cancelled, TimedOut or Crashed state**; cancel is modelled as Failed with a localized "Canceled" message.
- Two-phase lifecycle: `PrepareConversion` (computes output paths, creates folders, checks category compatibility, calls virtual `Initialize`) then `StartConversion` (calls virtual `Convert` synchronously, then `OnConversionSucceed`/`OnConversionFailed`).
- Subclasses per engine/format: `ConversionJob_FFMPEG`, `ConversionJob_ImageMagick`, `ConversionJob_Gif`, `ConversionJob_Ico`, `ConversionJob_Word/Excel/PowerPoint` (via `ConversionJob_Office`), `ConversionJob_ExtractCDA`. Composite jobs (Gif, Ico, Word→image) chain *child jobs* through an intermediate file in `%TEMP%` and poll the child's progress with a 40 ms `Task.Delay` loop.
- Engine selection (`ConversionJobFactory.cs`) is **by input file extension and output type only** — no content sniffing anywhere. The same holds for the shell extension's "which presets apply" and for `Helpers.GetExtensionCategory`.

### 1.3 Scheduler and threading
`Services/ConversionService.cs`: one "ConversionQueueThread" prepares all jobs, then loops: compute flags, start any Ready job on a free slot (array of `Thread`, size = `MaximumNumberOfSimultaneousConversions`, default 2 in `Settings.default.xml`, else `ProcessorCount/2`), `Thread.Sleep(50)` between iterations, and a 20 ms spin-wait until a started job leaves Ready. Jobs run `Convert()` fully synchronously on their thread. Progress and state reach the UI through property-change notifications raised from worker threads; the `State` setter does a *synchronous* `Dispatcher.Invoke` to refresh the cancel command. There is no `CancellationToken`, no timeout, no job queue abstraction, and no way to add jobs to a running batch (issue #701).

### 1.4 Format/capability mapping
- **Input taxonomy**: `Helpers.GetExtensionCategory` (`Application/FileConverter/Helpers.cs`) is a hard-coded switch from extension to one of Audio / Video / Image / AnimatedImage / Document / Misc. **Misc is treated as "compatible with everything"** ("to be tolerant") in `Helpers.IsOutputTypeCompatibleWithCategory`.
- **Output taxonomy**: `OutputType` enum (`OutputType.cs`) × category compatibility matrix, also a hard-coded switch in `Helpers.cs`.
- **Presets** (`ConversionPreset/ConversionPreset.cs`, `Settings.default.xml` — 76 presets, 2117 lines): name (with `/` folders), `OutputType`, explicit `InputTypes` list, a string-keyed settings dictionary whose *relevant keys* are seeded per output type (`InitializeDefaultSettings`), an `InputPostConversionAction` (None / MoveInArchiveFolder / Delete), and an output-name template. Changing the output type re-seeds the settings and prunes incompatible input types (`CoerceInputTypes`). Presets are therefore the capability descriptors; the context menu is literally "presets whose InputTypes contain the selected extension(s)".
- Default presets are re-merged on every install/upgrade (`SettingsService.PostInstallationInitialization` → `Settings.Merge`), which re-adds presets the user deleted (issue #697).

### 1.5 Batch handling
- Shell extension builds one argument string per selection; when it exceeds ~8000 chars it instead writes all paths to `%TEMP%\file-converter-input-list*.txt` and passes `--input-files` (`FileConverterExtension.cs`, `MaximumProcessArgumentsLength`). Two code paths for the same thing produced a long-lived divergence bug: "wrong preset used for more than 16 files" (#567, #614, fixed in 2.2 per `CHANGELOG.md`).
- A preset is enabled only if it accepts *every* selected extension; otherwise it is shown disabled (`RefreshPresetList`).
- One `FileConverter.exe` process per batch; batches do not merge. If one file in a batch is not convertible the whole run can abort (#542).

### 1.6 Output naming and collisions
- Template engine `PathHelpers.GenerateFilePathFromTemplate` does plain string replacement of tokens such as `(p)`, `(f)`, `(o)`, `(i)`, `(d0)`, `(n:i)`, `(d:yyyy)`. Because replacement runs on the *whole path* including the original filename, filenames that contain `(O)` or `(f)` get mangled (#797).
- Collision policy `PathHelpers.GenerateUniquePath`: loop appending ` (2)`, ` (3)`… while `File.Exists` or the name is in the batch's own blacklist. It is check-then-use with no reservation; two concurrent jobs can race, and the batch blacklist is only for paths generated in the same process.
- `PathHelpers.IsPathValid` is a regex that requires `X:\` or `\\server\` prefixes; a `\\?\` long-path prefix would be rejected. No MAX_PATH strategy exists; `MaximumProcessArgumentsLength` is the only length-aware code.
- Output is written **directly to the final path by the engine** (ffmpeg gets the final path; `MagickImage.Write` gets the final path). There is no temp → validate → atomic move. ffmpeg is passed `-n` so it refuses to overwrite. On failure `OnConversionFailed` deletes whatever exists at the output paths. On success the only "validation" is `File.Exists` *after* the state was already set to Done (`StartConversion` logs an error if outputs are missing but does not flip the state).
- **Original handling**: by default untouched; output timestamps are set to the original's. The `Delete` post-action is a hard `File.Delete` (no recycle bin — users asked, #146). If output path equals input path and the post-action is move/delete, the **original is renamed before conversion** (`PrepareConversion`), i.e. the original is mutated on disk before any output exists.

### 1.7 FFmpeg integration (process)
`ConversionJobs/ConversionJob_FFMPEG.cs` (+ `.Converters.cs` for quality mappings):
- `ProcessStartInfo` with `UseShellExecute=false`, `CreateNoWindow=true`, **both** stdout and stderr redirected. Executable path = app directory + `ffmpeg.exe` (checked with `File.Exists`, failing the job with a "reinstall" message if missing). **No cmd.exe.** Working directory is not set (inherits Explorer's).
- **Arguments are a single interpolated string** with paths wrapped in `"`; there is no escaping. It works only because NTFS forbids `"` in names. The "custom command" preset feature splices user-entered text verbatim into that string.
- Output parsing: reads **only `StandardError`** line-by-line synchronously; regexes extract duration and `time=` for progress; failure is detected by substring matching on "Error", "Exiting.", "Unsupported dimensions", "No such file or directory" after stripping the file names from the line (a fix for #247 where a file named "Error …" failed every conversion). A known transport-stream false positive is special-cased.
- 2.2 added `-progress pipe:1` (progress to **stdout**) to fix the progress bar (#525, #589, #603, #680) — but stdout is redirected and **never read**. Once the pipe buffer fills, ffmpeg blocks on write and the job hangs forever with ffmpeg at 0 % CPU: that is exactly the symptom in the 2.2 freeze cluster (#700, #703, #716, #722, #724, #739, #740, #749, #780, #800, #803 — "freezes when the time estimate appears", "ffmpeg process stuck, must taskkill"). This is a textbook .NET redirected-pipe deadlock. (Hypothesis from code reading; consistent with every report; not executed.)
- **Cancellation** sets a flag; the process is killed only when the *next stderr line* arrives. No timeout. Child processes are not job-objected and nothing kills them on app exit (#361, #463: closing the window leaves ffmpeg running with a 0-byte output).
- Two-pass GIF writes a palette PNG to `%TEMP%`; intermediates are deleted only on the all-passes-succeeded path.
- Hardware acceleration (NVENC/AMF) is a global setting that rewrites codec args; produced its own issue family (#558, #572, #691, #713, #616, #752).

### 1.8 ImageMagick / Ghostscript (in-process)
`ConversionJobs/ConversionJob_ImageMagick.cs`: Magick.NET in the UI process. Ghostscript directory is set to the app folder (`MagickNET.SetGhostscriptDirectory`). Format hints are extension-based (`.avif`, `.cr2`, `.dng` forced; `.gif` first frame). Page count for PDFs is obtained by reading the whole PDF at density 1×1 — Ghostscript aborts with a "rangecheck in setscreen" and the **native delegate error takes the whole app down with no log line** (#790; also #133, #561, #643 "no output / crash on pdf→png"). Progress via Magick's progress event, which is also the cancel hook. Default color handling produces the recurring "transparent → black background" family (17 issues, see §3). ImageMagick's temp/cache can fail with "unable to extend cache – no space on device" on a 16 KB SVG (#674).

### 1.9 Office (COM automation)
`ConversionJob_Office.cs`, `ConversionJob_Word.cs` etc.: detects Office via App Paths registry keys, instantiates the real Word/PowerPoint/Excel invisibly, calls export-to-PDF, then optionally pipes the PDF through the ImageMagick job. Not cancellable, cannot be sandboxed, depends on the user's Office licence state and build. The result is the second-largest issue family (23 issues): licence/installation errors (#155, #173, #368, #466), "every second file fails" (#455), silent failures with "See inner exception(s)" (#756), M365 type-library drift breaking the export call (#756, #757, #778, #799).

### 1.10 Error handling and how failures reach the user
- Job failures: `ConversionFailed(message)` sets Failed + `ErrorMessage` (first failure wins). Messages are either localized resource strings, a raw ffmpeg stderr line, or `exception.Message`. The job list shows the message per job (`Controls/ConversionJobControl.xaml`).
- Infrastructure errors: `Diagnostics/Debug.cs` `LogError` **pops a modal `MessageBox`** from whatever thread calls it, including the static logger and worker threads (#473 "FileNotFoundException stops further conversions until confirmation").
- Unhandled native/COM crashes are not caught (#790 silent process death, #717 crash on Word→PDF).
- Logging: `%LOCALAPPDATA%\FileConverter\Diagnostics-<HHhMMmSSs>\Diagnostics<threadId>.log`, one file per thread, **full command lines including user paths**, auto-deleted after one day (static constructor of `Debug`). The bug template asks users to upload these files plus `Settings.user.xml` and `Registry.xml`.

### 1.11 Configuration storage
- `%LOCALAPPDATA%\FileConverter\Settings.user.xml` (XmlSerializer), written via a temp file then `File.Copy` over the real one (`SettingsService.Save`). `Settings.default.xml` ships in Program Files. A second XML, `Registry.xml`, holds misc values (last update check) — the class is confusingly named `Registry` (`Application/FileConverter/Registry.cs`). Schema versioning via `SerializationVersion` with a migration partial (`SettingsService.Migration.cs`).
- Real registry: the MSI writes `HKCU\Software\FileConverter\Path` **for the installing user only** (`Installer/Product.wxs` RegistryEntries component), and the shell extension reads that key to find the exe. This is the root of the multi-user family (#22, #84, #87, #340, #378, #647, #666): other users get only "Configure presets…" or "can't retrieve executable path" (#642).
- The shell extension deserializes the user's full settings XML inside Explorer with its *own partial type* (`FileConverterExtension/ConversionPresetReference.cs`, `XmlHelpers.cs`) and swallows every exception. Any drift between what the app writes and what the extension can parse silently removes the menu; users found that renaming `Settings.user.xml` brings it back (#554, #633, #798, #792).

### 1.12 Explorer integration: technology, registration, constraints
- Technology: legacy `IContextMenu` handler, managed code (SharpShell + WinForms `ContextMenuStrip`). Menu text uniqueness is enforced by appending zero-width spaces (`CreateMenu`).
- Registration: the MSI runs `FileConverter.exe --register-shell-extension <dll>` as a **deferred, non-impersonated (SYSTEM) custom action**, which calls SharpShell's RegAsm wrapper (`Helpers.RegisterShellExtension`, requires admin). Uninstall mirrors it. `MSIRESTARTMANAGERCONTROL=Disable` is set so the installer does not notice Explorer has the DLL loaded (comment in `Product.wxs`), hence "appears only after restarting Explorer" (#403, #516).
- Constraints observed in the tracker: Windows 11's new context menu does not show legacy handlers ("Show more options" only; #211, #598, #633), the maintainer acknowledged SharpShell/managed handlers are unsupported there and a native rewrite would be needed (#211 comment); the handler is loaded into third-party processes and the .NET Framework CLR crashes CET/shadow-stack-enabled hosts (#783, Raycast crash, resolved only by uninstalling File Converter); first right-click after boot takes seconds to a minute because the CLR + XmlSerializer code-gen load inside Explorer (#675, #772); third-party file managers differ (Directory Opus #645, XYplorer #205), OneDrive folders #485; a `ShellExView`-visible "disabled" state recurs (#516, #554).

### 1.13 Installer, signing, update
- WiX 5 MSI, per-machine `Program Files`, launch condition on .NET Framework 4.8, major-upgrade. Custom actions execute the app during install; any failure surfaces as MSI error 1722 (#382, 25 comments; #401, #416, #526, #591).
- Signed with a Certum open-source certificate; SmartScreen still warned (#302), Defender flagged a download (#383), an EDR flagged a bundled component CVE (#730). Fake/scam clone websites appeared (#410, #451, #481, #672).
- Updater (`Services/UpgradeService.cs`): once a day fetch `version.xml` from the GitHub `master` raw URL, download the MSI to `%TEMP%`, and **launch it with `UseShellExecute` on app exit — no hash or signature verification of the downloaded MSI**. Reports: never updates (#747), nothing happens on exit (#734), where did it go (#759).
- No portable mode (#121, #231, #641, #695) because registration and `HKCU` lookup are mandatory.

---

## 2. Mechanism → what they do → consequence for us → why

| Mechanism | What FileConverter does | Consequence for us | Why |
|---|---|---|---|
| Explorer entry point | Managed in-proc `IContextMenu` shell extension (SharpShell), registered by RegAsm from an MSI custom action | **Reject** (and confirm our drag→trigger→overlay design, ADR-0003/0004) | 43 issues are "menu missing/disabled/Win11/slow", plus #783 (CLR injected into other processes crashes them). Code running inside Explorer is the single biggest liability of this product class. Our design injects nothing into Explorer. |
| Per-batch app process | Extension launches `FileConverter.exe` with paths on the command line; >8000 chars → temp list file | **Adapt** | Two transport paths caused #567/#614. We receive paths via OLE drop (`CF_HDROP`), so there is no command-line size problem; but a worker-process IPC must still carry N paths without a length cliff. Use one channel (pipe/JSON), never argv for file lists. |
| Engine selection by extension | Factory switches on extension; "Misc" extensions accept any output | **Reject** | Violates `security.md` (extension ≠ type) and produced #98, #120/#147/#167/#237 (tif vs tiff), #656, #633 (`.heif` vs `.heic`). Detect by magic bytes; feed the capability resolver with detected type, never the extension. |
| Preset = capability descriptor | Presets declare `InputTypes` + output type + settings; menu = presets matching selection | **Adopt concept, change representation** (ADR-0007) | Declarative descriptors driving the UI is the right idea. Keep ours typed (records, detected-type keys, operation × format), not a string-keyed dictionary re-seeded on output-type change. |
| Hard-coded category matrix in a static helper | `Helpers.GetExtensionCategory` + `IsOutputTypeCompatibleWithCategory` switches | **Reject** | Format logic in a god helper is what our capability-resolver rule forbids; it also made every new format a code change across three files. |
| Job state machine | Unknown/Ready/InProgress/Done/Failed; cancel = Failed("Canceled"); no timeout | **Reject; use ADR-0011 result model** | Users cannot distinguish cancel, crash, timeout, unsupported; #700-family hangs had no state. Explicit Cancelled/TimedOut/WorkerCrashed states plus actionable reason are required by the brief. |
| In-process engine (Magick.NET, Office COM) | Native ImageMagick + Ghostscript loaded in the UI process | **Reject; confirms ADR-0005** | #790: native delegate error kills the app silently, no log. Our worker-process boundary exists precisely for this. |
| ffmpeg as child process | `ProcessStartInfo`, no cmd.exe, both pipes redirected, only stderr read, args as one quoted string, no working dir, no timeout, no job object | **Adapt the good parts, fix the rest** | No cmd.exe is right. Everything else is a lesson: read *both* pipes asynchronously with bounded buffers (the unread stdout pipe is the probable cause of the 11-issue freeze cluster), `ArgumentList`, owned working dir, timeout, kill on cancel immediately, Windows job object so children die with the parent (#361, #463). V1 has no ffmpeg; the rules apply to any engine process. |
| Failure detection by stderr substrings | Grep for "Error" etc. after stripping file names | **Reject** | #247 (file named "Error") and the special-cased false positive show how brittle it is. Decide success from exit code + output validation (decode the produced file), never from log text. |
| Output written directly to final path | Engine writes final file; delete on failure; `File.Exists` after Done | **Reject; brief mandates temp → validate → atomic move** | A crash mid-write leaves a truncated file with the final name (#700 "0 byte file"), and "Done" is set before the existence check. |
| Collision naming | ` (2)`, ` (3)` loop on `File.Exists` + in-batch blacklist | **Adapt** | The user-facing convention matches our brief (`photo (1).webp` style); implement it with an exclusive-create (`FileMode.CreateNew`) reservation rather than check-then-use, and apply it at the atomic move, not before conversion. |
| Output name template | Token replacement over the whole path | **Reject for V1; investigate later** | #797 shows tokens colliding with real file names. If ever added, parse the template, never run replacements across the input name. |
| Original handling | Untouched by default; optional permanent delete; renames the original pre-conversion when names collide; copies timestamps | **Adopt "untouched by default" only** | Pre-conversion rename and hard delete both violate "originals are never destroyed". Timestamp copying is a reasonable user expectation (#33, #139) — record as a requirement candidate, not V1. |
| Path validation | Regex requiring `X:\` / UNC; no long-path support | **Reject** | Our rules require `\\?\`/LongPathsEnabled, Unicode, quotes, >260 chars. |
| Scheduler | Polling thread with `Sleep(50)`, per-job `Thread`, sync `Dispatcher.Invoke` from workers | **Reject** | Polling violates "idle means idle"; sync dispatcher calls from workers are a deadlock pattern. Use `Channel<T>`/`Task`, `CancellationToken`, and post UI updates asynchronously. |
| Cancellation | Flag checked when the next stderr line arrives; Office not cancellable | **Reject** | Cancel must be immediate and observable; a job type that cannot be cancelled must not exist in our pipeline (timeouts need the same path). |
| Progress reporting | Regex on ffmpeg stderr; Magick progress event; 40 ms polling tasks for composite jobs | **Adapt** | Fine as engine-side sources, but marshal through a single progress channel from the worker; no polling tasks. |
| Error surfacing | Modal `MessageBox` from the static logger on any thread; raw engine lines as user messages | **Reject** | Blocks batches (#473), leaks engine internals (#756 "See inner exception(s)"). Our notifications must be non-modal and messages mapped to actionable reasons. |
| Logging | Per-thread files with full command lines/paths, 1-day retention, users upload them | **Adapt** | Per-session diagnostics + a "report an issue" bundle is a good support loop; but `security.md` says log paths with care and never contents. Add redaction and an explicit user action to export. |
| Settings storage | XML in `%LOCALAPPDATA%`, temp-then-copy save, version + migration | **Adopt concept** | Reasonable local-first storage. Use an atomic replace (`File.Replace`) rather than `File.Copy`, and version from day one. |
| Settings read by the shell extension | Explorer deserializes the app's XML with a divergent type, swallowing errors | **N/A for us (no extension) — but general lesson** | Never let two binaries parse the same config with two schemas; silent fallback hid the real bug for years (#554 → #798). |
| Registry for exe path | `HKCU\Software\FileConverter\Path` written by a per-machine MSI | **Reject** | 20 admin/multi-user issues. Per-user data belongs to per-user install or runtime discovery, never written by an elevated installer into one user's hive. |
| Installer | WiX MSI, per-machine, custom actions run the app elevated during install | **Investigate (ADR-0008)** | Custom actions that run product code elevated are the cause of #382/#416/#526/#591 and the per-user registry split. For a private experimental build, a per-user install (MSIX or per-user MSI/portable) without elevation fits our "no admin" rule. |
| Signing / SmartScreen | OSS code-signing cert; SmartScreen reputation still blocks; AV false positives | **Investigate (ADR-0008)** | Expect SmartScreen friction for any new signed binary; plan for it (reputation takes downloads/time), and never bundle components with public CVEs (#730). |
| Auto-update | Fetch version XML from a Git branch, download MSI to `%TEMP%`, run it on exit, no verification | **Reject (out of V1 scope anyway)** | Unverified installer execution is a supply-chain hole; also #734/#747/#759 show silent failure. If ever added: signed manifests, hash check, explicit user action. |
| Engine bundling | ffmpeg 99 MB GPL build, Ghostscript AGPL 24.6 MB, Magick.NET native dll in Program Files | **Reject the licences; adapt the pattern** | GPL ffmpeg and AGPL Ghostscript are both forbidden by `dependencies.md`. The *pattern* "engines next to the app, loaded on demand, located relative to the exe" is fine. V1 needs images only; WIC first (ADR-0006). |
| Office conversion | COM automation of the installed Office | **Reject for V1; brief already excludes it** | 23 issues; un-sandboxable, licence-dependent, breaks on Office updates (#756). Confirms the brief's "research Office separately". |
| Presets re-merged on upgrade | Defaults re-added after every install | **Adapt** | Users resent it (#697). Separate "defaults" from "user state"; merge only additively with an opt-in. |
| Localization | Satellite assemblies + per-thread culture set manually | **Note** | 12 issues in 2.1/2.2 where the language setting stopped taking effect — a regression in a feature nobody tests automatically. Any UI string pipeline we add needs a smoke test. |

---

## 3. Lessons learned, by problem class (issue numbers and counts)

Counts are from the 579 issues retrieved on 2026-10-20; classification is by title/body/comments, so counts are approximate (±2) and an issue is placed in one class only.

### 3.1 Explorer / shell-extension integration — 43 issues (largest class)
Menu absent, absent after reboot, absent for one user, absent after editing presets, absent on Windows 11's new menu, absent in third-party file managers, extension shown as "disabled", crashes in other processes.
`#68 #103 #123 #149 #152 #165 #191 #205 #211 #271 #287 #288 #376 #389 #391 #403 #450 #462 #465 #485 #491 #496 #516 #532 #536 #549 #550 #554 #566 #587 #598 #604 #633 #645 #649 #653 #655 #685 #733 #767 #783 #792 #798`
Highlights: #633 (28 comments, 2.1/2.2, Win10 and Win11 — workaround: delete `Settings.user.xml`); #554 (adding a preset removes the menu); #211 (maintainer: SharpShell unsupported on Win11, native rewrite needed); #783 (CLR loaded into a CET-enabled host → `0xc0000409`); #675/#772 (10 s to 1 min context-menu delay).
**Lesson:** in-proc Explorer code is where this product class dies. Our activation path (global hook + our own overlay as an OLE drop target) keeps all our code in our process. The remaining Explorer-side risk for us is the drag protocol itself, which the interaction spike covers.

### 3.2 Admin rights, multi-user, settings persistence — 20 issues
`#4 #22 #40 #50 #81 #84 #87 #176 #183 #266 #274 #284 #340 #378 #405 #529 #647 #650 #666 #682`
Root causes visible in source: per-machine MSI writing `HKCU` for one user; settings in Program Files until v2.0; "Save" disabled with no explanation (#529, #650).
**Lesson:** never require elevation, never write per-user state from an elevated context, and every disabled control needs a visible reason.

### 3.3 Installer / uninstaller — 24 issues
`#7 #21 #39 #46 #47 #48 #106 #343 #382 #396 #399 #401 #416 #472 #526 #556 #579 #588 #591 #622 #626 #652 #662 #721`
#382 (25 comments): install fails with error 1722 because a custom action runs the app to register the extension; #396 files left behind; #579 user reports the installer wiped `C:\temp` (cause not visible in the source we read — treat as unverified but note that the app and installer both write to `%TEMP%`).
**Lesson:** installers must not execute product code elevated; keep install = copy files + per-user registration at first run.

### 3.4 Engine process hangs and orphaned processes — 16 issues
Freeze: `#700 #703 #716 #720 #722 #724 #739 #740 #749 #774 #780 #796 #800 #803` (14, all after 2.2 added stdout progress). Orphans on window close: `#361 #463`.
**Lesson:** drain both pipes asynchronously with bounded buffers; tie child processes to a job object; a hung engine must hit a timeout and be killed, and the UI must show "timed out", not stall.

### 3.5 Progress reporting — 5 issues
`#384 #525 #589 #603 #680` — progress bar empty then jumps to done; the fix for this introduced §3.4.
**Lesson:** progress is part of the job contract, tested with a slow fake engine, not bolted on by parsing logs.

### 3.6 Batch behaviour — 7 issues
`#86 #236 #321 #455 #542 #567 #614` — hard limits by command-line length, divergent code path above 16 files, one bad file aborting the batch, "every second file fails" with Office.
**Lesson:** one transport for N inputs; per-item isolation (one failure never cancels siblings); a batch summary that says which items failed and why.

### 3.7 Microsoft Office COM — 23 issues
`#67 #112 #143 #155 #164 #173 #181 #200 #350 #368 #456 #466 #631 #705 #714 #717 #728 #745 #754 #756 #757 #778 #799`
**Lesson:** confirms the brief: Office/document conversion is a separate research track; COM automation of the user's Office is not a shippable engine.

### 3.8 Colour, transparency and rendering surprises — 17 issues
`#63 #66 #69 #100 #108 #129 #248 #251 #292 #336 #356 #484 #553 #595 #676 #719 #782` — alpha → black/white backgrounds (PNG/SVG/PDF → JPG/ICO), red channel dulled on WebP (#356), HDR AVIF → grey (#719).
**Lesson:** our image adapter needs explicit, tested policy for alpha flattening (background colour choice), colour profiles/ICC, and bit depth — with golden-file tests, because users judge "it worked" by looking at the result. Directly relevant to V1 (JPG/PNG/WebP).

### 3.9 Metadata and timestamps — 7 issues
`#15 #33 #139 #430 #476 #568 #599` — EXIF lost, orientation applied/not applied (#476 WebP rotated), GPS lost, modified date changed.
**Lesson:** decide and document per operation whether EXIF/orientation/ICC/timestamps are preserved; "Convert" and "Compress" users expect preservation, "Resize" users expect orientation to be baked in. Needs a requirement entry and tests.

### 3.10 HEIC / codec library drift — 13 issues
`#96 #163 #228 #324 #369 #388 #460 #493 #512 #527 #543 #617 #755` — iOS 18 HEIC breaking until libheif was updated (#493 → fixed in 2.1), then again (#617, #755).
**Lesson:** engine version pinning must be paired with a sample-file corpus from real devices; HEIC is out of V1 scope and should stay out until the engine/licence question (libheif/libde265/x265) is settled in the matrix.

### 3.11 Engine crashes and malformed/unusual input — 16 issues
`#133 #159 #161 #216 #452 #560 #577 #674 #709 #711 #715 #727 #729 #765 #777 #790`
**Lesson:** malformed input must fail the job, never the app (#790 silent death). The worker boundary plus a hostile-file test set (rules/testing.md) covers this.

### 3.12 Naming, templates and post-actions — 5 issues
`#247 #473 #510 #636 #797` — "Error" in a file name failed conversions; template tokens mangled names; archive-move raised modal errors; users asked for overwrite.
**Lesson:** file names are data, never signals; keep V1 naming fixed and collision-safe; overwrite is an explicit, separate user choice if ever offered.

### 3.13 Signing, antivirus, impersonation — 8 issues
`#203 #302 #383 #410 #451 #481 #672 #730`
**Lesson:** budget time for SmartScreen reputation and false-positive submissions; publish checksums; own the download URL early.

### 3.14 Auto-update — 7 issues
`#5 #190 #353 #697 #734 #747 #759`
**Lesson:** out of V1 scope; when added, verify what you execute and tell the user where the installer went.

### 3.15 Portability — 4 issues: `#121 #231 #641 #695`
**Lesson:** a portable/private build is what our brief asks for first; our design has no mandatory registration, so this is achievable.

### 3.16 Localization regression — 12 issues: `#557 #593 #606 #609 #646 #667 #673 #690 #692 #735 #737 #750`
**Lesson:** any feature without an automated check regresses silently in a single-maintainer project.

### 3.17 Performance — 5 issues: `#70 #195 #309 #624 #793`
**Lesson:** users notice CPU and heat; our budgets doc must include engine CPU during jobs and idle, not only UI latency.

---

## 4. Failure modes we can avoid by design

1. **Context menu/extension disappears or slows Explorer** — we ship no shell extension; our code never runs inside Explorer or other hosts (§3.1, #783).
2. **Engine hang with no way out** — engine processes run under a worker with a timeout, a job object, and asynchronous bounded reading of *both* stdout and stderr; cancel kills immediately (§3.4).
3. **Native engine crash kills the app** — engines live in the worker process; the main app reports "worker crashed" as a first-class result (§3.11, ADR-0005).
4. **Truncated output presented as success** — temp → validate (decode the produced file) → atomic move; "Done" is only set after the move succeeds (§1.6).
5. **Original mutated or destroyed** — originals are never renamed, moved or deleted by V1; collision handling applies to the output name only.
6. **Success/failure decided by log text** — exit code + validation decide; engine text is diagnostic only (#247).
7. **Extension-based type decisions** — content detection feeds the capability resolver (#98, #120, #633 `.heif`).
8. **One bad file aborts a batch / modal error blocks the queue** — per-item results, non-modal notifications, batch summary (#542, #473).
9. **Per-user state written by an elevated installer** — no elevation, per-user install, runtime discovery of our own exe path (§3.2).
10. **Two binaries parsing one config with two schemas** — single settings model, single reader, versioned, atomic replace (#554, #798).
11. **Template tokens colliding with file names** — no template language in V1 (#797).
12. **Unverified auto-update execution** — no updater in V1; signed/hashed manifests when added.
13. **Alpha/colour surprises** — explicit flattening and colour-management policy with golden tests for JPG/PNG/WebP (§3.8).
14. **Metadata silently dropped or orientation double-applied** — explicit per-operation metadata policy with tests (§3.9).
15. **Hard-coded format matrix across static helpers** — declarative capability descriptors (ADR-0007).
16. **Command-line length cliff for many files** — paths travel over the drop (`CF_HDROP`) and the worker IPC channel, never argv (#86, #614).
17. **Idle polling loops** — no `Sleep`-polling scheduler; event-driven job pipeline (performance.md).

---

## 5. Open questions for our architecture

1. **Worker IPC for N files and progress**: FileConverter's stderr-regex progress and 40 ms polling are what we want to avoid; which channel (named pipe + length-prefixed JSON? `System.IO.Pipes` + `Channel<T>`) carries job start, progress, cancel, result, and survives worker death cleanly? (ADR-0005 detail.)
2. **Output validation depth**: "decode the produced file" costs time on large images; is a full decode in the worker acceptable within the job-latency budget, or is a WIC header probe enough for V1?
3. **Collision reservation**: reserve the final name with an exclusive create *before* conversion (guarantees the name shown to the user) or at the atomic move (guarantees no empty placeholder on crash)? FileConverter picks the name before conversion and races; we need one answer and a test.
4. **Alpha and colour policy for JPG output**: default background colour (white vs black vs user choice), ICC handling, chroma subsampling — FileConverter's 17 issues say users will ask; V1 must document a choice.
5. **Metadata policy**: which EXIF/XMP/ICC fields survive Convert/Compress/Resize with WIC, and is orientation baked in on Resize? Needs an experiment against ADR-0006's engine.
6. **Timestamps**: copy original timestamps to the output (FileConverter does; users wanted it, #33, #139) or leave creation time as "now"? Requirement entry needed.
7. **Diagnostics bundle**: FileConverter's per-session logs made bug reports useful but leak paths; what is our redaction rule and what does a user-triggered "export diagnostics" contain?
8. **Per-user install and first-run**: with no elevation, how does the app register autostart/tray presence and how is uninstall clean (FileConverter's #396 leftovers)? Feeds ADR-0008.
9. **SmartScreen strategy for a private build**: unsigned private builds vs an OSS certificate; what do we tell the friends-group testers? Feeds ADR-0008.
10. **Engine CPU budget during jobs**: FileConverter users complained about heat/CPU (#195, #793); do we cap engine thread count per job and limit concurrency, and what is the measured number?
11. **Batch semantics for the radial drop**: a multi-file drop is one gesture; do we run items concurrently (FileConverter: 2 threads default) or sequentially with one progress indicator, and how does the result summary look in a near-invisible UI?
12. **HEIC/AVIF later**: Magick.NET's native build bundles libheif/libde265/(x265?); the licence status of those sub-components must be checked in the dependency matrix before any "later" scope decision, given FileConverter's 13-issue churn on HEIC alone.

---

## Appendix — evidence index (file paths in the clone)

- Job model and lifecycle: `Application/FileConverter/ConversionJobs/ConversionJob.cs`, `ConversionState.cs`, `ConversionFlags.cs`
- Engine dispatch: `Application/FileConverter/ConversionJobs/ConversionJobFactory.cs`
- Scheduler/threads: `Application/FileConverter/Services/ConversionService.cs`; thread helper in `Helpers.cs`
- Capability matrix: `Application/FileConverter/Helpers.cs` (`GetExtensionCategory`, `IsOutputTypeCompatibleWithCategory`, `CompatibleInputExtensions`)
- Presets: `Application/FileConverter/ConversionPreset/ConversionPreset.cs`, `Settings.default.xml`, `Settings.cs` (`Merge`, `Clean`)
- Naming/collisions/paths: `Application/FileConverter/PathHelpers.cs`
- ffmpeg: `Application/FileConverter/ConversionJobs/ConversionJob_FFMPEG.cs`, `ConversionJob_FFMPEG.Converters.cs`
- ImageMagick/Ghostscript: `Application/FileConverter/ConversionJobs/ConversionJob_ImageMagick.cs`
- Composite jobs/temp files: `ConversionJob_Gif.cs`, `ConversionJob_Ico.cs`, `ConversionJob_Word.cs`
- Office: `ConversionJob_Office.cs`, `ConversionJob_Word.cs`, `ConversionJob_Excel.cs`, `ConversionJob_PowerPoint.cs`
- Errors/logging: `Application/FileConverter/Diagnostics/Debug.cs`, `DiagnosticsData.cs`
- Startup/args/update launch: `Application/FileConverter/Application.xaml.cs`
- Settings storage: `Application/FileConverter/Services/SettingsService.cs`, `SettingsService.Migration.cs`, `Registry.cs`, `XmlHelpers.cs`
- Updater: `Application/FileConverter/Services/UpgradeService.cs`, `version.xml`
- Shell extension: `Application/FileConverterExtension/FileConverterExtension.cs`, `PathHelpers.cs`, `ConversionPresetReference.cs`, `XmlHelpers.cs`, `app.manifest`
- Installer: `Installer/Product.wxs`, `Installer/Installer.wixproj`, `Installer/DebugInstaller.bat`
- Dependencies and sizes: `Application/FileConverter/FileConverter.csproj`, `Middleware/` (ffmpeg.exe 99,264,000 bytes; gsdll64.dll 24,608,256 bytes), `README.md` § Middlewares, `CHANGELOG.md`
