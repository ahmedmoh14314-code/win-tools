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
   - 100,000 entries;
   - 20 GB in all;
   - an entry beyond 200:1 past 1 MB is a bomb;
   - extraction stops before the drive has less than 2 GB free.
   - The archive itself may be at most 4 GB.
   - Each file's CRC-32 and length are checked against the directory. Any mismatch fails the whole job.
6. **Output safety (ADR-0005):**
   - Extraction writes into a staging folder created exclusively in the destination (`CreateDirectoryW`) and hidden until the whole archive succeeded; then it is renamed to the first free name.
   - Add to ZIP writes an exclusive temporary file, reads it back (every entry decompressed and checked), and renames it without overwriting.
   - Links and online-only files are skipped and never followed or downloaded. The ZIP being written is never added to itself.
   - Every output carries the strictest Mark-of-the-Web zone of its sources (D5).
7. **The host believes:**
   - an extraction from its **shape** on disk: one SHA-256 over every relative path and file size, with no link anywhere. The content is not read again (it may be 20 GB), because the worker checked each file's CRC-32 as it wrote it;
   - a ZIP from its name, `PK` signature and SHA-256;
   - Info and Test from bounded facts.

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
