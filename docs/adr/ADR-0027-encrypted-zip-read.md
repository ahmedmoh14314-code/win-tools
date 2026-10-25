# ADR-0027: Encrypted ZIPs are read with .NET's own AES, PBKDF2 and HMAC, and the password is asked only when an archive needs one

- **Status:** Proposed (V2 PR 5.3)
- **Date:** 2026-10-25
- **Deciders:** product owner (started the PR, 2026-10-25); the reader choice is this ADR's, against the V2 plan's SharpCompress row
- **Related:** ADR-0023 (archives), ADR-0026 (the secret channel and the password policy), V2 plan §7 (the encrypted-ZIP row), §9, D4

## Context
- ADR-0023 refused encrypted entries (`PasswordRequired`): .NET 10's `ZipArchive` lists them, with their CRC and sizes, but `Open()` throws "unsupported compression method" for both ZipCrypto and WinZip AES (measured). It does not expose where an entry's data starts.
- The V2 plan names SharpCompress for encrypted ZIP reading.
- ZIP has two encryption schemes in use:
  - PKWARE's traditional one ("ZipCrypto": weak, still written by 7-Zip and WinRAR on request);
  - WinZip's AES (AE-1 with a CRC, AE-2 without one; 128, 192 or 256 bits).
- ZIP never specified how a password becomes bytes. WinRAR 6.24's ZipCrypto used the ANSI code page, with "?" for what it cannot encode: "سرّé1" became `???\xE91` (measured, confirmed with Python's `zipfile`). Its AES used UTF-8.
- A drop on Extract cannot know in advance whether the archive is encrypted: the menu sees a name, not content.

## Decision
1. **Our own reader, on the platform's cryptography, with no new dependency.**
   - `ZipDirectory` reads the end record and every central record, only for an archive with encrypted entries. It takes ZIP64's end record on the same fields .NET does: this disk, the entry count, the directory's offset.
   - **It must agree with `ZipArchive`** on the entry count and, entry by entry, on the name, the CRC, both sizes and the encrypted flag; otherwise the archive is damaged. .NET decodes every name as UTF-8, flag or not (measured), and so does this.
   - Each entry's local header must:
     - be where the directory says;
     - be encrypted too;
     - give the same method and the same AES description;
     - have its data inside the file.
   - Encrypted entries may not share data.
   - Unsupported:
     - PKWARE's strong encryption;
     - encrypted central directories;
     - an AES entry without its field;
     - an AES field on an entry of another method;
     - any method but Stored or Deflate inside.
2. **The ciphers** (worker): ZipCrypto per APPNOTE §6.1, and WinZip AES with PBKDF2-HMAC-SHA1 (1,000 iterations), AES-CTR with a little-endian counter from 1, and HMAC-SHA1.
   - **The HMAC's first 10 bytes are compared in constant time when the entry's data ends**, before the end is reported. After Deflate ends, the rest of the encrypted bytes are read, so the code is always checked.
   - AE-2's CRC field is 0 by design and is then not checked; the HMAC is its check. A CRC that is not 0 is checked whatever the version says. AE-1's CRC is checked too, and so is ZipCrypto's (its only real check). ZipCrypto's bytes after the end of its Deflate stream are not read.
   - **WinZip AES authenticates only the encrypted bytes,** not the method, the sizes or the name. The headers must agree, and every length and CRC is checked. Still, someone who rewrites both headers of an AE-2 entry consistently (Deflate to Stored and the size) makes it extract as the raw deflate stream. That is a limit of the format, which every reader shares; the bytes are the archive's own, and nothing outside the folder is reached.
   - The bytes go through ADR-0023's `ReadAll`: names, every limit, the bomb rules on the packed size, free space, the staging folder.
3. **Which bytes are the password:** UTF-8 as typed, UTF-8 of its NFKC form (ADR-0026), then the system ANSI and OEM code pages through Windows' `WideCharToMultiByte` with its default best-fit mapping, without duplicates.
   - **One archive, one password, in each scheme's byte form.** Before anything is written, every ZipCrypto entry's 12-byte header narrows the ZipCrypto candidates, and the first AES entry's verifier narrows the AES ones. The two lists are kept apart, because WinRAR writes AES as UTF-8 and ZipCrypto in the code page.
   - A wrong candidate that passes one 1-in-256 check byte therefore does not decide the archive: tests build exactly that case.
   - When an entry still accepts more than one candidate, each is **proven** before it is used: ZipCrypto by decrypting the entry to its size and CRC, within its size and the bomb rule, with nothing written; AES by its authentication code.
   - No candidate left for a scheme the archive uses: `PasswordIncorrect`. A check value or an HMAC that fails after that is "damaged, or the password is not its own", never a success.
   - The worker works on its own copies of the password bytes and zeroes them on every path.
4. **The password is asked only when needed.**
   - Extract and Test run first without one. A locked archive is refused before the staging folder exists, with `PasswordRequired`.
   - `SecretPrompt.WhenRequired` marks such a command. When the run of **one** archive ends that way, the scheduler marks the job (`SecretNeeded`) and tells the user nothing, and the coordinator opens ADR-0026's dialog ("Archive password", typed once). The job is then queued again with the password.
   - A job that had a password never asks again: a wrong one is a toast, and the user drops again. Nor does a job cancelled while it ran (the host may be exiting).
   - Several archives that need a password get a toast saying to drop each on its own: one dialog that names no file could not say which archive it is for.
   - Info needs no password (the directory is not encrypted), and names the method.
5. **The secret channel is ADR-0026's**: from the dialog to this job's parameters only, on the worker's pipe, never logged or in a message. The worker zeroes derived keys and the password bytes it made when it is done with them (the .NET strings remain, as recorded there).

## Evaluator review
One round (2026-10-25), every finding reproduced on the real worker with the reviewer's own hostile archives. No blocker; four majors, all fixed, each now a test that failed on the reviewed code (12 of 13):
- **An AES field on an entry whose method is not 99** was used: strength 0, 4 or 255 crashed the worker (`CryptographicException`), and Info said "AES-64".
- **The directory was trusted where the local header disagreed:** WinRAR's own AES sample, with `sub/big.bin`'s central record relabelled AE-2, Stored and 306 bytes, passed Test and Extract with the deflate stream as the file. The CRC rule took the record's word for AE-2.
- **Two directories in one file:** a 32-bit end record with disk fields 0xFFFF pointing to directory A and a ZIP64 one pointing to B. .NET read B's names, the worker A's data, and the cross-check compared no names.
- **The drain** read ZipCrypto's padding for every record pointing at one entry: 50 records took 10 s, and 100,000 would reach the timeout.

Minors, fixed:
- the right password read as damage once in 256 on a single ZipCrypto entry;
- AES as UTF-8 beside ZipCrypto in the code page refused;
- password bytes not zeroed on failure, and the caller's zeroed in place;
- no test of the cross-check;
- a job cancelled at exit could still ask.

The file is now read through one handle, which closes the reviewer's unreproduced concern of a file swapped between two opens.

After the fixes, all of the reviewer's probes fail cleanly or extract consistent data. 12 mutations, one per safeguard, each fail a test. Three needed tests of their own, because two safeguards caught the same case.

## Alternatives considered
- **SharpCompress (the V2 plan's row).** Not chosen for this:
  - whether it checks AE-2's HMAC before an entry is accepted would itself need a review, and its reader hides the bytes the check is on;
  - it would be a second, third-party ZIP parser used only for encrypted archives, which may disagree with `ZipArchive` about entries;
  - the plan records its steady advisory stream.
  
  It stays the candidate for 7z and RAR (D3).
- **Waiting for .NET 11's ZIP encryption.** Not released; decryption is wanted now.
- **Asking for the password before every Extract.** Rejected: most archives are not encrypted, and a dialog on each drop would be noise.
- **Only UTF-8 bytes.** Rejected: WinRAR's own ZipCrypto archives with a non-ASCII password would then never open.

## Consequences
- Encrypted ZIPs made by WinRAR, 7-Zip, WinZip or Info-ZIP open with their password, ZipCrypto or AES; creation stays deferred (D4).
- An archive whose entries use different passwords opens only as far as the first password goes: `PasswordIncorrect` past it.
- WinZip AES costs one PBKDF2 per entry (1,000 SHA-1 iterations), as in every reader of the format.
- A ZipCrypto archive made with a non-ASCII password on a machine of another code page may not open here; the code page is a property of the archive's maker.

## Evidence / experiments
- **WinRAR 6.24's samples** (`tests/samples/locked-*.zip`, with `New-EncryptedZipSamples.ps1`): AES-256 AE-1 and AE-2, ZipCrypto with the data-descriptor flag, and each with "سرّé1".
- **`ArchivePasswordWorkerTests` (48 cases, 14 from the review):**
  - WinRAR's archives with the right, no, a wrong and an Arabic password;
  - AES-128, 192, 256, AE-1 and AE-2, ZipCrypto, Stored and Deflate (built from the specifications in `LockedZipBuilder`);
  - a changed byte in each scheme and place, including one only the HMAC sees and one only the CRC sees;
  - a moved local header and data shorter than the cipher's fields;
  - encrypted bombs;
  - the lucky check byte;
  - the NFKC form;
  - strong encryption;
  - Test, Info and the policy;
  - the real worker process.
- **Mutations**, 12, one per safeguard. Each fails at least one test:
  - the HMAC comparison;
  - the narrowing;
  - the CRC on encrypted entries;
  - the AE-2 CRC rule;
  - the header comparison;
  - the AES field on another method;
  - the names;
  - shared data;
  - the separate lists;
  - the proving;
  - the copies;
  - the cancel check.

  The stored ZipCrypto CRC test was added when its mutation first passed.
- **`JobSecretTests` (host):** an on-demand command runs without a password or with one and nothing else; one locked archive asks instead of a toast; several archives, or a job that had a password, never ask.
- **Explorer**, the real dialog typed with Unicode input: Extract with "سرّé1" 2/2 and 1/1 on the final harness; a wrong password 2/2, with the toast; Test 1/1; ZIP Extract 1/1 as a regression. No password in any host log.
