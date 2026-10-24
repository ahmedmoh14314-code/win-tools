# ADR-0020: General Files as the second module: any file is a kind, Tools is shared, Copy path runs in the host

- **Status:** Proposed (V2 PR 1.4)
- **Date:** 2026-10-24
- **Deciders:** product owner (V2 plan §16–17, recommended defaults)
- **Related requirements:** UX-009 (folders); V2 plan §10 PR 1.4, §6 (module boundaries)

## Context
V2 PR 1.4 is the walking skeleton of a second module: File info, SHA-256 and Copy path for any file, before PDF adds engine risk. It must show that a module is added by data and adapters only (ADR-0007, ADR-0018, ADR-0019). Until now a file whose extension no module knew had no kind, and the resolver gave it nothing.

## Decision
1. **Any file is a kind.** `FileKinds.AnyFile` ("file") is the kind of every path whose extension no kind claims, or that has none; `FileKinds.Declared` lists every kind a rule may name. `FileKinds.ByExtensionOf` answers the narrower question "which kind does the extension name", which the folder lookup asks.
2. **The module shares Tools.** File info, SHA-256 and Copy path sit in the image module's Tools category. A sixth top-level category narrowed V1's ring until "Compress" reached into the hub gap (UiSurfaceTests found it), and categories are designed to be shared. A file no module reads gets a one-category menu (Tools), which ADR-0019's Back rule keeps usable.
3. **One worker per module.** File info and SHA-256 run in the new `WinTools.Worker.Text` (no codec, writes nothing; later also subtitles). The line loop is shared (`WorkerLoop`, used by both workers). Its jobs have a 10-minute per-file limit (hashing a large file on a slow disk), the image worker keeps 60 s.
4. **Copy path runs in the host.** It parses nothing. `FilesExecutor` routes it to the clipboard (on the UI thread, which owns the window the clipboard needs; retried while another program holds it; the waiting happens off the UI thread) and everything else to the text worker. A folder is copied without the drop target's mark; several paths go one per line.
5. **Words are module data.** `CapabilityDescriptor.Feedback` (`FeedbackWords`) carries what the toasts say; the presenter no longer asks the image module.
6. **File info says when a file is not what its name says.** A definite signature that disagrees with a known extension is shown as a notice ("Windows program, but named .pdf"). Text and unknown content never count as a mismatch.

## Alternatives considered
- **A "File" category of its own.** Rejected: it broke V1's top ring layout (above).
- **Copy path in the text worker.** Rejected: the clipboard needs a window and a message loop; the worker has neither, and a process start for a string the host already has adds latency and nothing else.
- **Copying the SHA-256 to the clipboard as well.** Deferred: it would overwrite the clipboard as a side effect of another command. The hash is shown in the info panel; copy name, path or folder variants come with Stage 7 (V2 plan §10 PR 7.2).
- **Keeping "no kind" for unknown files.** Rejected: every general command would then need a special case in the resolver.

## Consequences
- Every file now gets a menu. Image commands still never accept a file of another kind; the resolver's intersection rule is unchanged.
- Tests that specify the image module's own menu (the V1 golden file among them) now build it from `CapabilityResolver.Images`; the combined menu is covered by `GeneralFilesTests`.
- The harness builds its menu with the host's resolver, so its ring positions match the host's for every module.
- Copy path is not exercised end to end: it would overwrite the user's clipboard. It is unit-tested through `FilesExecutor`, and a manual check is in the release matrix protocol.
- The presenter's single set of progress counters (ADR-0018 deferred minor) is shared by both modules; a General Files job running beside an image batch can mix their progress text. Fixed when per-job feedback arrives.

## Evidence / experiments
- `GeneralFilesTests` (Core, 10): kinds, menus, shapes, words, the copied text.
- `TextWorkerTests` (8): the FIPS 180-2 "abc" vector, content type and mismatch notice, folder contents, failures, no writes, and the real worker process over the pipe, believed by the host.
- `FilesModuleTests` (5): believability (hash shape, info, failures), Copy path through the executor, a busy clipboard.
- Clipboard calls follow Microsoft's documentation of `OpenClipboard`, `EmptyClipboard`, `SetClipboardData` (the system owns the block after success) and `CF_UNICODETEXT`.
- Explorer: `file` and `folder` harness scenarios (File info on a text file and on a dropped folder). **Not yet run:** an Adobe Acrobat window held the foreground and the harness refused to inject.

## Evaluator review
Pending.
