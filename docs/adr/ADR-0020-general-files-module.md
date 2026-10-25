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
3. **One worker per module.** File info and SHA-256 run in the new `WinTools.Worker.Text` (no codec, writes nothing; later also subtitles). The line loop is shared (`WorkerLoop`, used by both workers). File info has a 30 s per-file limit; SHA-256 has 30 minutes and reads one file at a time (a large file on a slow disk or a share; parallel reads would thrash a hard disk). The image worker keeps 60 s.
   - **Nothing online-only is read.** A cloud placeholder (offline, recall-on-open or recall-on-data-access attributes) gets File info from the file system alone ("Online only (not downloaded)") and SHA-256 refuses it (`OnlineOnly`): reading it would download it.
   - **A link is followed for the facts.** File info and SHA-256 describe the link's final target, and say "(through a link)".
   - Windows may update a file's last-access time when it is read, as for any reader; nothing else about a file changes.
   - Short magic numbers count only with the structure behind them: "MZ" needs a PE header, "BM" a BMP header whose size matches, "ID3" a tag header. HEIF and AVIF are told apart from MP4.
4. **Copy path runs in the host, at once.** It parses nothing. It never waits in the module's queue: behind a long hash it would overwrite whatever the user copied meanwhile (the scheduler runs clipboard commands beside the queue). The clipboard is set on the UI thread, which owns the window it needs, retried while another program holds it; the waiting happens off the UI thread, and a write that comes after the wait gave up is dropped. The text is prepared before the clipboard is emptied, so a failure never wipes what the user had copied, and it is marked not to be uploaded to the cloud clipboard (the paths carry folder names). A folder is copied without the drop target's mark; several paths go one per line.
5. **Words are module data.** `CapabilityDescriptor.Feedback` (`FeedbackWords`) carries what the toasts say; the presenter no longer asks the image module.
6. **File info says when a file is not what its name says.** A definite signature that disagrees with a known extension is shown as a notice ("Windows program, but named .pdf"). Text and unknown content never count as a mismatch.
7. **The info panel wraps long values** onto at most three lines, so a SHA-256 (64 digits) and a notice are shown whole. Copying a hash needs a window that takes input; it comes with the dialogs (Stage 7). Until then a hash is read from the panel, and a batch shows the first three files' hashes ("+N more files").
8. **"Not for folders" only when the folders are the reason:** nothing is offered, and without the folders something would be.
9. The host starts without the text worker; its commands then fail with a plain reason, and images keep working.

## Alternatives considered
- **A "File" category of its own.** Rejected: it broke V1's top ring layout (above).
- **Copy path in the text worker.** Rejected: the clipboard needs a window and a message loop; the worker has neither, and a process start for a string the host already has adds latency and nothing else.
- **Copying the SHA-256 to the clipboard as well.** Deferred: it would overwrite the clipboard as a side effect of another command. The hash is shown in the info panel; copy name, path or folder variants come with Stage 7 (V2 plan §10 PR 7.2).
- **Keeping "no kind" for unknown files.** Rejected: every general command would then need a special case in the resolver.

## Amendment, V2 PR 7.2: Copy name, Copy folder path, Batch rename
- **Copy name** and **Copy folder path** run in the host like Copy path. The folder path is copied once for all the files.
- **Batch rename…** asks a dialog first. This is a new, general `CapabilityDescriptor.Dialog`: the dialog's answer is the job's one `dialog` parameter. A command with a dialog takes exactly that parameter, and no other command takes it.
  - **The dialog** gets the names, never the folder or the paths. It previews them with the same pure plan the host applies (`BatchRename`): prefix, suffix, find and replace (any case), spaces and underscores, case, and numbering in the names' natural order. Extensions are kept.
  - **The host renames** in two steps through `~wintools-<id>-<n>.tmp` names in the folder, so a file can take a name the batch frees, and a change of case alone works.
  - **What refuses the rename:**
    - a name held by a file outside the batch refuses all of it, so nothing is ever overwritten;
    - an invalid or clashing name;
    - files from several folders;
    - more than 1,000 files.
  - **A failure** puts every name back.
  - **The result** is believed from the disk.
- **Tools is ordered by everyday use:** File info, Copy path, Batch rename, Copy name, Copy folder path, then SHA-256. On an image or a PDF the last ones go under "More…".

## Consequences
- Every file now gets a menu. Image commands still never accept a file of another kind; the resolver's intersection rule is unchanged.
- Tests that specify the image module's own menu (the V1 golden file among them) now build it from `CapabilityResolver.Images`; the combined menu is covered by `GeneralFilesTests`.
- The harness builds its menu with the host's resolver, so its ring positions match the host's for every module.
- Copy path is not exercised end to end: it would overwrite the user's clipboard. It is unit-tested through `FilesExecutor`, and a manual check is in the release matrix protocol.
- The presenter's progress belongs to long-running jobs: File info and Copy path never reset it or replace a batch's progress toast (Copy path then only plays its sound). Two long-running jobs of different modules still share one progress toast; per-job feedback is deferred.
- SHA-256 of one large file shows "Working…" without a fraction: the worker sends no progress lines yet (ADR-0018 deferred them to PDF).

## Evidence / experiments
- `GeneralFilesTests` (Core, 10): kinds, menus, shapes, words, the copied text.
- `TextWorkerTests` (8): the FIPS 180-2 "abc" vector, content type and mismatch notice, folder contents, failures, no writes, and the real worker process over the pipe, believed by the host.
- `FilesModuleTests` (5): believability (hash shape, info, failures), Copy path through the executor, a busy clipboard.
- Clipboard calls follow Microsoft's documentation of `OpenClipboard`, `EmptyClipboard`, `SetClipboardData` (the system owns the block after success) and `CF_UNICODETEXT`.
- `InfoPanelFitTests`: a SHA-256 and notices fit at 96–192 DPI with nothing lost; the panel grows with wrapped rows. `UiSurfaceTests` renders the one-category ring and the folder ring.
- Explorer: `file` and `folder` harness scenarios (File info on a text file and on a dropped folder). **Not yet run:** an Adobe Acrobat window held the foreground and the harness refused to inject.

## Evaluator review
One round (2026-10-24): one blocker and five majors, all fixed. Blocker: the SHA-256 and notices were cut in the single-line panel (decision 7). Majors: the `file`/`folder` harness scenarios could not run (a null image operation; a folder missing from the listing); Copy path waited behind the module's queue (decision 4); online-only files were read and so downloaded (decision 3); folders had a blank name in the summary; plain text starting "MZ", "BM" or "ID3" was flagged as disguised (decision 3). Minors fixed: links, the 10,000-entry count, "Not for folders", progress sharing, the clipboard's order and late writes, wording ("Could not copy the path", a module-neutral crash reason), the log for Copy path, the host starting without the text worker, a publish check that looked at the wrong folder, the host-reference test, the one-category and folder rings in the UI test. Kept: last-access updates (documented), batch hashes beyond three and hash copying (decision 7), one progress toast for two long-running modules. Second look (the blocker made one due): everything fixed, no blocker or major; its minors fixed too (a failed File info never replaces a batch's progress toast; Copy path no longer rewrites a running job's log values; an AVIF that lists its brand second; "Online only" not repeated; the empty-ring reason worked out only for an empty ring). Open: File info can wait behind a long hash in the module's queue (it shows no progress; nothing is harmed), and the recall attributes are untested on a real cloud placeholder.
