# ADR-0030: 7z and RAR are read with SharpCompress, behind the archive worker's own checks; split archives are refused

- **Status:** Proposed (Tangerine parity, 0.2.0 Beta)
- **Date:** 2026-11-07
- **Deciders:** product owner (2026-10-25 direction: Tangerine parity, mature engines; V2 plan D3)
- **Related:** ADR-0023 (archives), ADR-0026 (the secret channel), ADR-0027 (encrypted ZIP), ADR-0005 (worker isolation), dependency rules

## Context
- 7z and RAR are common downloads. Neither is in .NET, and neither format is one to parse by hand: 7z has a filter graph and solid blocks, RAR a closed format with four generations of decoder.
- ADR-0023 and ADR-0027 kept SharpCompress out of ZIP and left it as the candidate here, "behind its own review". That review is this ADR.
- What was measured on SharpCompress 0.50.7, with WinRAR 6.24's and py7zr 1.1.4's archives:
  - it does **not** check a 7z's CRC-32s: a changed byte in stored data reads as a clean file;
  - it checks a RAR's CRC-32 or BLAKE2 hash, **except for encrypted files**, where the check is off (`RarCrcStream._disableCrc`);
  - a protected RAR 5 file with visible names stores a keyed form of its CRC-32 (`ConvertHashToMAC` in UnRAR), and the library neither computes it nor exposes the salt to compute it with;
  - with `-htb`, `Crc` holds the first bytes of a BLAKE2 hash, with nothing public to tell it from a CRC-32;
  - a 7z's packed size per entry is 0, and a solid RAR's is meaningless (identical files pack to nothing);
  - `ExtractAllEntries` refuses a non-solid RAR, and opening entries one by one in a solid archive decompresses the block again each time, or fails;
  - `Volume.IsMultiVolume` is reliable for RAR and always true for 7z;
  - a wrong password gives four kinds of result, from "The password did not match" to `ArgumentOutOfRangeException`;
  - SharpCompress's `Attrib` holds only a 7z entry's low 16 bits; the Unix mode is in `ExtendedAttrib`.

## Decision
1. **SharpCompress 0.50.7 in the archive worker only**, pinned. It parses and decompresses; it never touches the disk. `WriteToDirectory`, the source of its traversal advisories, is not used. A test keeps it out of the host.
2. **Kinds and commands:** `7z` and `rar` join `FileKinds`, with Extract to folder, Test and Archive info. The worker decides by the signature (7z `37 7A BC AF 27 1C`, RAR 4 `52 61 72 21 1A 07 00`, RAR 5 `52 61 72 21 1A 07 01 00`), at the start of the file only. Self-extracting archives are not looked into.
3. **The same checks as ZIP and TAR:** each entry goes through `ReadAll`, so names (`ArchiveEntryPolicy`), the second fence, one budget, bytes counted as they come out, the staging folder, the atomic rename to a free name, Mark of the Web and the host's shape check are the code that ZIP uses.
4. **What the library does not do, the worker does (`LibraryArchive`):**
   - **Check values:**

     | Archive | Checked by |
     |---|---|
     | 7z | the worker, on the CRC-32 |
     | RAR without a password | the library, CRC-32 or BLAKE2 |
     | RAR 4 with a password | the worker, on the plain CRC-32 |
     | RAR 5 with a password | the worker when the stored value is the plain CRC-32; otherwise **not checked, and said so** |

   - **The last row is a limit, not a choice.** A mismatch on a protected RAR 5 file cannot be told from a keyed value or a BLAKE2 hash, so it cannot fail the job, or every sound protected RAR 5 would. The user gets a notice ("Password-protected RAR 5 files cannot all be checked for damage"), and Test does not claim "every file". A wrong password is still caught, by RAR 5's own password check. Reading the salt by reflection, or parsing RAR headers ourselves to compute the key, was rejected (see below).
   - **Declared sizes:** before anything is written, the headers are read and the sum of every declared size, kept or not, must meet the 20 GB and whole-archive rules, as TAR's first pass does. So a solid block that must be decompressed to skip a refused entry cannot run past the limits unseen, and an honest bomb is refused without decompressing a byte.
   - **Solid archives:** a 7z or a solid RAR is read once, in order, through one reader. The per-file ratio rule applies only to a non-solid RAR; the others have the whole-archive rule, on declared sizes first and on the bytes as they come out.
   - **Links:** RAR 5's redirections (symbolic links, junctions, hard links, file copies), reparse points, and Unix link modes (7z, RAR 4) are never created. Device modes in a 7z are skipped. Windows attributes are not read as Unix modes: `NOT_CONTENT_INDEXED` has the bits of a character device.
   - **7z's start header:** its directory must lie inside the file. Otherwise the archive is cut short, or is the first file of a split one, and the library is not asked (also a second fence for GHSA-7jcq-g98g-83v5). The CRC-32s of the start header and the directory are checked here too, before the library reads them. The library checks them as well, and checks a compressed directory's CRC-32 only when one is stored.
   - **RAR's end:** a RAR cut at a file boundary reads as a shorter, sound archive, and the library says nothing. Extract and Test walk the headers with the library's public `RarHeaderFactory`, encrypted ones included, to the end-of-archive header. A RAR 5 without one is damaged. A RAR 4 without one may be whole (very old archives) or cut, so it succeeds only with the notice "The end of this old RAR archive could not be confirmed; files may be missing". Data after the end header is ignored, as RAR ignores it.
   - **Whatever the library throws** on a hostile file is a damaged archive, never a crash. `OutOfMemoryException`, under the worker's heap limit, is `LimitExceeded`: RAR's window can be large, and an archive made with a dictionary beyond the limit is refused. This holds for entry streams too (`GuardedStream`). Our own writes stay outside it, so a full disk is still `DiskFull`. Closing an entry, the reader or the archive runs while such a failure unwinds, so it never throws in its place.
5. **Passwords take ADR-0027's path, unchanged:** Extract and Test run first without one; a locked archive is refused with `PasswordRequired` before the staging folder exists, and the host asks.
   - **Encrypted names** (7z's encrypted header, RAR's `-hp`) open with the password too. Without it, Extract and Test ask; Info says "Names and contents protected with a password", with no listing.
   - **Wrong password:** RAR 5 has a check value, so `PasswordIncorrect`. 7z and RAR 4 cannot tell it from damage, so `DecodeFailed` with both in the message, as ADR-0027 does for a check value that fails.
   - **Forms:** the library takes text and makes the format's bytes itself, so ADR-0027's code-page candidates do not apply. The password is tried as typed, then in its NFKC form. A retry costs a second run, only when the first failed that way.
6. **Split archives are refused, not guessed at:** `.part1.rar`, `.r00` and every other part give `SplitArchive`, a code of its own, so the toast says so and no other refusal does. The job was given one file, and the host believes one input; reading siblings it was not given would widen both for a rare case. Info says "One part of an archive split into several files". `.7z.001` names no kind, so the menu does not offer it; renamed to `.7z`, it is "cut short".
7. **Batch:** Extract is `PerFile`, so several archives get one request each and one folder each, with no change.
8. **Text:** a failure reaches the user through its error code, so a split archive has its own (`reason.SplitArchive`). New fact values are fixed sentences, because `UiText.Value` translates whole values only.

## Licences
- SharpCompress is MIT. Its source holds no GPL, AGPL or LGPL text (GitHub code search of the repository, 2026-11-07).
- **The RAR decoder is not MIT in substance.** It comes from UnRAR through junrar, and `Unpack15.cs` and `Unpack20.cs` say the unRAR licence applies. That licence allows any software to handle RAR archives, free of charge. It forbids making a RAR-compatible archiver or re-creating the compression algorithm, and it requires one paragraph to be carried along. WinTools only decompresses. The paragraph is in `third-party/sharpcompress/NOTICE.md`, which the installer joins into `THIRD-PARTY-NOTICES.txt`.
- The unRAR licence is not open source in the OSI sense, and Fedora refuses it for that reason. That matters to a distribution's policy, not to this product: it is not GPL, and it allows use and redistribution, also commercially, for extraction. **Consequence:** WinTools must never gain "Add to RAR" through this code.
- Deflate (Ms-PL), BZip2 (Apache-2.0), Deflate64, ADC and the Bouncy Castle parts (MIT) are listed in the same file.

## Alternatives considered
- **7-Zip's `7z.dll` or `7za.exe`.** A strong reader, but its RAR code is LGPL plus the unRAR restriction, and a native parser of hostile files would need its own review and a bundled binary. Kept in mind if SharpCompress's advisories keep coming.
- **RARLAB's `UnRAR.dll` with a managed 7z reader.** The reference decoder, with every check value, but a native dependency for one format, and two readers instead of one.
- **Our own RAR 5 key derivation** to check the keyed values: the salt is internal to the library, so it needs reflection on a third-party's private fields, or our own RAR header parser. A hand-written RAR parser is what this ADR exists to avoid, and reflection breaks at an upgrade. Better fixed upstream.
- **Refusing protected RAR 5 archives with visible names.** Honest, but this is WinRAR's default for a password, so the feature would miss its main case. A clear notice serves the user better.
- **Failing a protected RAR 5 file whose value does not match.** Would fail every sound archive of that kind.
- **0.51.0-beta.** Faster, with parallel decoding, but a prerelease with a large rewrite of the same code. 0.50.7 holds the advisory fixes.

## Security history
Advisories on SharpCompress, read 2026-11-07:
- GHSA-5wf8-cxh5-32fv (high, 2026-10-19): truncated LZMA2 spins forever. Fixed in 0.50.6.
- GHSA-7jcq-g98g-83v5 (medium, 2026-10-09): 7z directory allocated before it is bounded. Fixed in 0.50.5 (release note: "7z next-header bounds validation"); the advisory itself lists no patched version, so the worker checks it too.
- GHSA-rch7-vc9j-fw2v (medium, 2026-08-05) and GHSA-6c8g-7p36-r338 (medium, 2026-05-06), and the older GHSA-jp7f-grcv-6mjf and GHSA-fxh6-w476-hgr4: extraction to a directory. That API is not used here.

The stream is steady, as ADR-0023 expected. The library runs in the disposable worker, under its memory limit, a 30-minute job limit and the kill-on-close job. **Follow-up:** read the advisories at every upgrade.

## Consequences
- 7z and RAR 4/5 extract, test and show their info, with or without a password.
- **Limits, all said to the user:**
  - a damaged protected RAR 5 file can extract with wrong bytes, under the "not checked" notice, when its size stays right;
  - a RAR 5 archive without a password check value, which WinRAR does not write, could extract garbage with a wrong password, under the same notice;
  - a 7z entry with no CRC stored fails as damaged, if not empty: safe, and rare (py7zr stores one, and so does 7-Zip by default);
  - an encrypted 7z directory that stores no CRC-32 of its own, as py7zr's does, can pass Test with a changed byte: measured, a date changed. The files' own CRC-32s still hold, and every name still passes the name policy;
  - split archives are refused.
- A skipped entry in a solid block is still decompressed, to reach the next one: time, not disk, bounded by the declared sizes and the job limit.
- Anti-items in a 7z are not treated specially and were not tested; whatever they become stays inside the new folder.
- The worker grows by `SharpCompress.dll` (2.4 MB), with no dependency of its own on .NET 10.

## Evaluator review
One round on PR #42 (2026-11-07), all fixed test-first:
- **A RAR cut at a file boundary passed:** 20 of 353 cuts of `rar5.rar` and 46 of 382 of `rar4.rar`, and Extract committed 2 of 4 files as a success.
- **A 7z's header checksums:** the worker now checks them itself. Measured afterwards: the library already refused a changed name in a plain directory, so this is a second fence. The real gap that remains is below (Consequences).
- **The split-archive reason was shown for every unsupported archive**, TAR's included.
- **Closing an entry could replace `DiskFull` or `LimitExceeded` with "damaged".**

## Evidence / experiments
- `tests/WinTools.Worker.Tests/SevenZipRarWorkerTests.cs` on WinRAR 6.24's and py7zr 1.1.4's samples (`tests/samples/README.md`): RAR 4, RAR 5, solid and not, 7z; hostile names (7z from the library's writer, RAR by rewriting WinRAR's headers); links, junctions and hard links; a per-file bomb, a declared bomb, a declared 4 GB; truncation; damage only a CRC can see; every password case; Arabic and NFKC passwords; parts of split archives; a fake extension; no overwrite; the real worker process with three archives in a batch.
- `tests/WinTools.Core.Tests/SevenZipRarCapabilityTests.cs`: menus, names, the reason and the facts in both languages.
- **Mutations,** 10, one per safeguard, each fails at least one test: the 7z CRC, the RAR 4 protected CRC, the 20 GB rule on declared sizes, the whole-archive rule on them, the password retry, the split-archive refusal, redirections, the 7z start header, the per-file ratio on a non-solid RAR, and the wrong-password message. The 20 GB rule first survived, because no small sample can reach it without the ratio rule firing first; `DeclaredRefusal` is now tested on its own.
- **Not exercised:** the out-of-memory path (no test archive needs more memory than the limit), and an Explorer run.
