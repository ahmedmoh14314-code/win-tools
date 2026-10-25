# ADR-0023: Archives start with ZIP on .NET's own System.IO.Compression, behind a name policy and decompression-time limits

- **Status:** Proposed (V2 PR 5.1)
- **Date:** 2026-10-25
- **Deciders:** product owner (V2 plan §9 Archives, §14 threats, D13 limits)
- **Related requirements:** V2 plan §9, §10 PR 5.x, §14 (archives), D5 (Mark of the Web), D13 (limits)

## Context
- Archives are a common need, and a classic attack surface: path traversal (zip-slip), absolute and device paths, links, bombs, damaged data.
- .NET 10 reads and writes ZIP natively. `ExtractToDirectory` is unsafe on untrusted input (V2 plan §7), and the runtime does not verify an entry's CRC-32 on read.

## Decision
1. **ZIP first, with no third-party code:** `System.IO.Compression.ZipArchive` in a new disposable worker, `WinTools.Worker.Archive`, run like the other workers.
   - TAR, TAR.GZ and GZIP (`System.Formats.Tar`, `GZipStream`) come next.
   - 7z and RAR extraction need SharpCompress (D3) and its own review.
2. **Commands:**
   - "Archive" category on a `.zip`: Extract to folder, Test, ZIP info.
   - "Add to ZIP" for any files and folders, last in the shared Tools, so no other menu gains a ring sector.
   - Extract always makes a new folder named after the archive (`photos`, `photos (1)`…). It never spills files into the current folder and never merges into an existing folder.
   - Add to ZIP names one item's ZIP after it (`report.zip`, `holiday.zip`). Several items get the name of the folder that holds them (as 7-Zip does), placed next to them.
3. **Names (`ArchiveEntryPolicy`, Core, pure):**
   - **Refused:**
     - empty names;
     - absolute, drive-relative and UNC paths;
     - any `..` component;
     - a `:` anywhere (drives, alternate data streams);
     - control characters;
     - components over 255 characters;
     - more than 32 levels.
   - **Renamed and reported:**
     - trailing dots and spaces trimmed (Windows would merge `a.` and `a`);
     - reserved device names get a leading `_` (`_NUL.txt`);
     - `<>"|?*` become `_`.
   - Two entries that are one name on Windows get `name (1)`.
   - **A second fence:** the final full path must lie inside the staging folder.
4. **Entries that are never created:**
   - links (Unix mode `lrwxrwxrwx` in the external attributes);
   - devices, pipes and sockets;
   - encrypted entries, which fail the job with `PasswordRequired`, since .NET cannot decrypt them. A password dialog is a later PR.
5. **Limits (`ArchiveLimits`, D13), counted on bytes as they decompress:**
   - 100,000 files **and folders created**, implicit parent folders included (a small archive of nested folder names must not create millions of folders);
   - 20 GB in all;
   - an entry beyond 200:1 past 1 MB is a bomb, **and so is the whole archive beyond 200:1 of its own size**. Declared sizes can lie, and entries can share one stored stream;
   - extraction stops before the drive has less than 2 GB free.
   - A file and a folder of the same name keep the first one; the other is skipped and reported.
   - The archive itself may be at most 4 GB.
   - Each file's CRC-32 and length are checked against the directory. Any mismatch fails the whole job.
6. **Output safety (ADR-0005):**
   - Extraction writes into a staging folder created exclusively in the destination (`CreateDirectoryW`) and hidden until the whole archive succeeded; then it is renamed to the first free name.
   - Add to ZIP **lists everything first**:
     - A file that is online only, in use, or in a folder that cannot be read fails the job with the reason, with nothing written. No archive silently lacks a file the user asked for.
     - The same goes for a tree deeper than 32 levels, more than 100,000 files or more than 20 GB.
     - Only real links (symbolic links and junctions) are left out, and the toast says how many. OneDrive's files carry a reparse point too and are packed like any other.
     - Files are opened shared for reading only, so a file being written is never packed torn.
   - It then writes an exclusive temporary file, reads it back (every entry decompressed and checked, **without the bomb rules**, since these are the user's own files), and renames it without overwriting.
   - The ZIP being written, and other jobs' temporaries, are never added.
   - Every output carries the strictest Mark-of-the-Web zone of its sources (D5).
7. **The host believes:**
   - an extraction from its **shape** on disk: one SHA-256 over every relative path and file size, with no link anywhere. The content is not read again (it may be 20 GB), because the worker checked each file's CRC-32 as it wrote it;
   - a ZIP from its name, `PK` signature and SHA-256;
   - Info and Test from bounded facts.

8. **Notices reach the user:** a believed success that carries a notice fact (entries skipped or renamed, links not followed) shows it in the toast.

## Evaluator review
One round (2026-10-25): one blocker and four majors, three of them reproduced by the reviewer on the built worker. All fixed, with tests that fail on the old code (5 of 19 worker tests).
- **Blocker:** Add to ZIP dropped files it could not read (links, files in use, and every OneDrive file inside a folder, because they carry a reparse point) and still said "ZIP created".
- **Majors:**
  - the read-back check applied the bomb rules to the user's own compressible files;
  - overlapping entries cheated the per-entry ratio;
  - folders were not counted (a folder bomb);
  - OneDrive's reparse points made a finished extraction unbelievable.
- **Minors fixed:**
  - a file and a folder of the same name;
  - COM0 and LPT0 (one list shared with `PathPolicy`);
  - depth in Add to ZIP;
  - an unreadable subfolder's reason;
  - the host's walk bounded by the limit;
  - a longer rename backoff;
  - torn reads;
  - other jobs' temporaries.
- **Open:**
  - the central directory is read in full before the entry count is checked (the job's memory cap contains it);
  - an extraction into OneDrive still needs an Explorer check on a synced folder.

## Alternatives considered
- **`ZipFile.ExtractToDirectory`.** Rejected: no hook for the name policy, the limits or links, and a partial result on failure.
- **SharpCompress for ZIP too.** Not needed for ZIP. It has a steady advisory stream; it is kept for 7z and RAR, behind its own review.
- **Extract Here into the current folder.** Rejected for now: a hostile or careless archive scatters hundreds of files among the user's own. One new folder is always safe to delete.
- **Hashing the extracted content in the host.** Rejected: double reads of up to 20 GB. The shape check refuses any added, removed, resized or linked entry.

## Consequences
- Encrypted ZIPs can be inspected (Info says so) but not extracted until the password PR.
- ZIP64 and Unicode names work as far as .NET supports them; names from very old DOS tools may decode differently.
- Extraction time grows with content. The host allows 30 minutes per archive and runs one archive job at a time.

## Evidence / experiments
- `ArchiveCapabilitiesTests` (Core):
  - 13 refused name forms;
  - 11 renamed or accepted forms (deterministic);
  - depth and length;
  - the second fence (a sibling folder with the same prefix);
  - the bomb rule;
  - the menu;
  - names.
- `ArchiveWorkerTests` (14, real files):
  - extract with Unicode, and the host believes it;
  - hostile names (5 skipped, 2 renamed, nothing outside);
  - a link entry;
  - case-colliding names;
  - a corrupted stored entry (CRC mismatch: nothing left);
  - a 64 MB-of-zeros bomb (`LimitExceeded`, nothing left);
  - an encrypted flag (`PasswordRequired`);
  - a fake `.zip`;
  - an existing folder kept;
  - Test and Info;
  - Add to ZIP of a folder and a file, and back;
  - the zone carried both ways;
  - a planted file disbelieved;
  - the real worker process.
- With the name policy disabled (mutation), `Hostile_names_never_reach_outside_the_folder_and_are_reported` fails, and the second fence alone still kept every file inside.
