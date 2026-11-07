# ADR-0032: Copy to and Move to run in the host, never overwriting and verified across volumes; Show in folder and Open in Terminal build no command line from a path; Share stays out

- **Status:** Proposed (0.2.0 Beta)
- **Date:** 2026-11-07
- **Deciders:** product owner (Tangerine parity brief)
- **Related requirements:** SAFE-001, UX-009

## Context
Tangerine parity needs actions on the file itself: copy or move it to a folder, show it in its folder, open a terminal there, share it. A move is the first command that removes an original on purpose, so "originals are never destroyed" means that the original may go only once a checked copy has its final name. `File.Move` is not enough: it may copy and delete across volumes with no check. The Actions ring had four commands and no room for more.

## Decision
1. **Four commands in the General Files module, category Actions:** `files.move-to` (Move to…), `files.copy-to` (Copy to…), `files.show-in-folder` (Show in folder), `files.terminal` (Open in Terminal). They parse nothing, so they run in the host, like Batch rename and Copy path.
2. **The destination comes from Windows' folder window.** A new dialog kind, `folder`, in the dialogs process: WPF's `OpenFolderDialog`, titled in the host's language, its buttons in Windows' language; Esc, Cancel or closing it answers cancelled. The request still carries no path: a client id makes the window remember the last folder. The answer is a path, the first the host takes from that process, so it passes `TransferPlan.Destination`, must exist, and must not be or lie inside a link or junction. It is the job's one dialog parameter and is never logged.
3. **Each item is its own request** (`PerFile`): the window is asked once, and a failure stays that item's ("2 of 3 completed").
4. **Nothing is overwritten.** Names are the product's own: "report.pdf", then "report (1).pdf" (`TransferPlan.Candidates`; a folder keeps its whole name). A name is claimed by `MoveFileExW` with no flags, which fails when it is taken.
5. **A move on one volume is that rename, atomic.** `MOVEFILE_COPY_ALLOWED` is never passed. Across volumes the same call answers `ERROR_NOT_SAME_DEVICE`, and the move becomes:
   - a file: held open against writers until it is gone (shared for delete, and removed while still held, so no write fits between the check and the removal), `CopyFileExW` to `~wintools-<id>.tmp` in the destination, size and SHA-256 compared, the name claimed, the original removed. If it cannot be removed, the copy is removed and the move has failed;
   - a folder: its tree listed first, copied into a temporary folder, each file's size and SHA-256 compared, the name claimed, the original renamed aside (which Windows refuses while a file in it is open; the copy is then removed), listed again and compared with what was checked (names, sizes, write times), then deleted. If it changed, it goes back to its name untouched, the checked copy stays, and the job fails with `Incomplete`. The same if the delete fails part way: what is left goes back to its name.
6. **A copy** goes the same way without the hash and the removal: `CopyFileExW` under a temporary name, the size compared, the name claimed.
7. **A folder never goes into itself:** refused before the job by its strings, and in the job by file identity (volume serial and file id, walking up from the destination, then the temporary folder and the claimed name), for Copy too. Strings prove nothing: `\\localhost\C$\A\` and `C:\A\sub` differ, and a move through them would land inside the source and delete both. A folder move refuses when the identities cannot be read; a copy, which destroys nothing, goes on. **Links and junctions inside a folder are never followed:** a copy skips them and says how many, a move across volumes refuses them, and a dropped link is not moved across volumes.
8. **The host believes the result from the disk** (`FilesJobModule`): the output is in the picked folder, under a name the item may take, of the same kind; a moved original is gone, a copied one is there.
9. **Show in folder** calls `SHOpenFolderAndSelectItems`, on its own thread with COM initialised and a 10 s wait, one window per folder with all its items selected. This replaces the brief's `explorer.exe /select,<path>`: the working form puts the quote after the comma (`/select,"path"`), which `ArgumentList` cannot produce, and it would quote the whole argument, the form reported to open Documents. A command-line string from a path is against the security rules.
10. **Open in Terminal** starts Windows Terminal's alias (`%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe -d .`) when it exists, otherwise Windows PowerShell, with the folder as the working directory. This replaces the brief's `wt.exe -d <folder>`: `wt` reads `;` as a command separator, and a folder may hold one. No path is an argument and no cmd.exe is involved.
11. **`CapabilityOutput.Window`:** both run at once beside the module's queue and say nothing when the window opens, only when it did not. At most 5 windows for one drop.
12. **`CapabilityCategory.MostOnRing`:** Actions takes at most four sectors, so three commands and "More…". The ring is Copy path, Move to…, Rename files; "More…" lists Copy to…, Copy name, Show in folder, Open in Terminal and Add to ZIP.
13. **Share is left out.** See below.

## Alternatives considered
- **Share through the CsWinRT projection in the dialogs process** (`net10.0-windows10.0.19041.0`). It builds, and `DataTransferManagerInterop` exists, but it was not added:
  - `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57 is under the Windows SDK licence, not MIT, and requires licence acceptance. Its distribution terms include "require distributors and external end users to agree to terms that protect it at least as much as this agreement". That is outside the policy's list, and accepting it is the owner's decision (ADR-0009);
  - `Microsoft.Windows.SDK.NET.dll` is 24.9 MB, plus `WinRT.Runtime.dll` 0.5 MB, for one command;
  - the host would have to send file paths to the dialogs process, which ADR-0019 rules out, and the process would have to stay alive until the share ends, with no reliable event for a dismissed sheet;
  - nothing of it can be checked without a person at the desktop.
- **Share through hand-written WinRT calls.** It avoids the licence and the size, but needs parameterized interface ids, `IIterable<IStorageItem>` and a delegate implemented by hand, around 400 lines of unsafe code. Rejected without a spike the owner can run.
- **A worker for Copy to and Move to.** Rejected: there is nothing to parse, and a worker would need write access to the original for no isolation gained.
- **A streamed copy with the hash taken as it reads.** One read fewer, but it loses the Mark of the Web, the other streams, the attributes and the times. `CopyFileExW` keeps them, at the price of a third pass over the bytes on a move across volumes.
- **Hash every copy.** Rejected for Copy to: the original stays, so a damaged copy loses nothing, and reading everything twice makes a large copy several times slower.
- **Show in folder in Tools, beside Folder path.** Tried: "Document info" and "SHA-256" then failed `UiSurfaceTests` and `ArabicWheelTests`. So did a Tools ring of six.
- **Five, six or seven sectors in Actions.** Tried: with two categories Back takes half the ring, and at five sectors even "More…" did not fit (36 px at 96 DPI).
- **A separate module and queue for transfers.** Deferred: a long move waits behind a long hash in the module's queue, and File info behind a long move, as ADR-0020 already records for a hash.

## Consequences
- Copy to…, Copy name and Add to ZIP take one step more, through "More…". The order is one line of catalog to change. Rename files stays on the ring, so the Explorer harness's `rename` scenario is unchanged.
- A move across volumes reads every byte three times: the copy, then both sides of the check.
- A long copy shows "Working…" with no fraction: progress counts items.
- Cancelling stops between chunks and removes the copy. It happens only when the host exits: no window offers it yet.
- Copying from a cloud folder downloads what it copies, as Explorer does, unlike SHA-256, which refuses (ADR-0020).
- A folder copy gets new folder times and attributes; files keep theirs.

## Evidence / experiments
- `FileActionsTests` (Core, 32): destinations, names, refusals, menus, toasts in both languages, the request.
- `FileActionsHostTests` (Worker tests, 43): real files; collisions; simulated moves across volumes through a test seam; a damaged copy; an original that cannot be removed; files and folders in use; junctions; paths beyond 300 characters in Arabic and emoji; cancellation; believability; the window commands through a recorder that opens nothing.
- `A_move_to_another_device_is_refused_as_a_rename_and_takes_the_checked_copy`: no seam. `\\localhost\C$` is another device to Windows: a probe of `MoveFileExW` on this machine answered error 17, and the test takes the whole copy path through it. It returns early where the administrative share is absent. This machine has one volume, so no move between two physical drives has been run.
- `UiSurfaceTests` and `ArabicWheelTests`: every ring at every DPI, in both languages.
- `explorer.exe` quoting: [PhotoStructure's report](https://forum.photostructure.com/t/open-file-in-folder-does-not-always-work-on-windows/448), one field report, not Microsoft documentation. `wt` and `;`: [Windows Terminal command line arguments](https://learn.microsoft.com/en-us/windows/terminal/command-line-arguments); `-d .` itself is not shown on that page.
- **Not yet run:** the folder window (`DialogsProcessTests` are added for the owner's desktop run), Show in folder, Open in Terminal, and any of it from a real drop in Explorer.

## Evaluator review
PR #41 review (2026-11-07), findings fixed with a failing test first: a folder moved across volumes was deleted whole, losing what was made or edited during the copy (decision 5); a share or mapped-drive alias passed the string check, so a move deleted both source and copy (decision 7); the original was let go before its removal (decision 5); Rename files returned to the ring (decision 12). Residual: a write landing between the second listing and the delete of a folder moved across volumes; the rename aside narrows it to files opened after that point.
