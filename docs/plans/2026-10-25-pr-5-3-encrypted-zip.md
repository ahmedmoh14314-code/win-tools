# PR 5.3: extracting and testing encrypted ZIPs, with the password asked only when needed

**Status:** started by the owner on 2026-10-25 ("ابدأ فك ZIP المشفّر"). It reuses PR 2.2's dialog, policy and secret channel (ADR-0026).

**Out of scope:**
- creating encrypted ZIPs (V2 plan D4: deferred, and never ZipCrypto);
- 7z and RAR (D3, a later PR);
- a second password for entries locked with another one;
- PKWARE's "strong encryption" and encrypted central directories, which are refused as unsupported.

## A change from the V2 plan: no SharpCompress for this
The V2 plan (§7, the encrypted-ZIP row) names SharpCompress for reading encrypted ZIPs. This PR does it with **.NET's own cryptography and about 400 lines of our own code** instead (ADR-0027). The reasons:
- **Integrity.** WinZip AES's AE-2 entries carry no CRC. The HMAC-SHA1 code is their only check. Our code checks it before the files are kept. Whether SharpCompress checks it would itself need a review, and its reader hides the bytes it would be checked on.
- **One parser.** .NET's `ZipArchive` already reads every ZIP here, with the name policy and the limits built on it. A second, third-party ZIP parser used only for encrypted archives could disagree with it about entries (PR 5.2's TAR lesson).
- **Surface.** The plan itself records SharpCompress's steady advisory stream. Here we need two small, fully specified ciphers, and the platform has every primitive: AES, PBKDF2 and HMAC-SHA1.

SharpCompress stays the candidate for 7z and RAR (D3), behind its own review.

## Evidence it rests on (2026-10-25)
- **Samples made by WinRAR 6.24** (installed, signed by win.rar GmbH) in the scratchpad:
  - with AES-256, WinRAR writes AE-1 entries, and an AE-2 entry for a 1-byte file;
  - with ZipCrypto (`-mezl`), every entry has the data-descriptor flag, so its check byte is the high byte of the file time;
  - each has a folder entry and a 105 KB deflated file.
- **ZipCrypto with the password "سرّé1" opens only with the bytes `???\xE91`:** WinRAR used the ANSI code page (1252 here), with `?` for every character it cannot encode. The standard library's `zipfile` confirms it. UTF-8, 1256 and 720 all fail.
- **.NET 10's `ZipArchive`** lists both archives and reports `IsEncrypted`, the CRC and the sizes. `Open()` throws "unsupported compression method" for both kinds. It does not expose where an entry's data starts.

## Design
1. **When the password is asked: only when needed.**
   - Extract and Test run first without a password. The worker refuses a ZIP with locked entries **before writing anything**, with `PasswordRequired`.
   - When a job of one archive ends that way, the host shows no toast. It asks for the password: the same dialog, with the title "Archive password", typed once. It then runs the job again with that password.
   - With several archives dropped at once, those that need a password fail with a toast saying to drop each one on its own. A dialog with no file name cannot say which archive it is for.
   - A cancel runs nothing more and says nothing. A wrong password gets its own toast (`PasswordIncorrect`). The user drops again to retry.
   - `SecretPrompt` gains `WhenRequired`. The scheduler accepts such a command with no parameters, or with the password (and its NFKC form).
   - Archive info needs no password: a ZIP's directory is not encrypted. Info now names the method ("AES-256", "ZipCrypto, a weak, old method").
2. **Where an entry's data is: `ZipDirectory`** (worker, pure).
   - It reads the end record, ZIP64's when present, and every central-directory record, taking each entry's local-header offset, flags and AES field.
   - **It must agree with `ZipArchive`** on the entry count, and for each entry on the CRC, both sizes and the encrypted flag. Otherwise the archive is "damaged". The local header's signature is checked, and the data must lie inside the file.
   - It is read only when the archive has an encrypted entry.
3. **Ciphers: `ZipCrypto`, `WinZipAes`** (worker), read-only streams over the entry's packed bytes.
   - **ZipCrypto** (PKWARE APPNOTE §6.1):
     - the three keys;
     - a 12-byte header whose last byte must match the CRC's high byte, or the file time's high byte with the data-descriptor flag;
     - the CRC-32 check that already runs on every file.
   - **WinZip AES** (AE-1 and AE-2; strengths 128, 192 and 256):
     - salt;
     - PBKDF2-HMAC-SHA1 with 1,000 iterations for the AES key, the HMAC key and a 2-byte verifier;
     - AES-CTR with a little-endian counter starting at 1;
     - **HMAC-SHA1 over the encrypted bytes, its first 10 bytes compared in constant time when the entry ends.** An entry is never accepted before that.
     - AE-1 is checked by its CRC as well. AE-2's CRC field is 0 by design and is not checked.
   - Inside, only Stored and Deflate are accepted. Other methods are unsupported.
   - The bytes come out of the same `ReadAll` as now: the name policy, every limit, the bomb rules on the packed size, the free space, and the staging folder.
4. **Which bytes are the password.** ZIP never said how to encode one.
   - The candidates, without duplicates:
     - UTF-8 as typed (WinZip AES and Info-ZIP);
     - UTF-8 in NFKC form;
     - the system ANSI code page;
     - the OEM code page.
   - The code-page forms come from Windows' `WideCharToMultiByte` with its default best-fit mapping, which is what WinRAR produced in the measurement above.
   - **One archive, one password.** Before extracting, the ZipCrypto entries' 12-byte headers are checked (cheap), and only the candidates that every one of them accepts remain.
     - So a wrong candidate that passes one entry's 1-in-256 check byte does not decide the archive.
     - AES entries narrow the same set as they are read: their 16-bit verifier rarely passes for a wrong candidate.
   - **No candidate left:** `PasswordIncorrect`, nothing written.
   - **A check value or an HMAC that fails after the password check passed** is "damaged, or the password is wrong". It is never a success.
5. **The secret channel is ADR-0026's.**
   - The password goes from the dialog to the host, to this job's parameters only, and to the worker's stdin pipe.
   - It is never in a log or a message.
   - The worker keeps the derived keys only for the entry being read.
6. **Docs:**
   - ADR-0027;
   - ADR-0023 decision 4 amended (encrypted entries are now read, with a password);
   - threat-model rows;
   - the dependency matrix: SharpCompress is no longer the encrypted-ZIP reader;
   - two release-matrix rows.

## Acceptance criteria
- **Core:** Extract and Test declare an on-demand prompt; Info and Add to ZIP declare none. The scheduler accepts an on-demand command with no parameters or with the password, and nothing else. The feedback for several archives that need a password says to drop each on its own.
- **Worker** (WinRAR's samples, committed with provenance; more built in the tests):
  - AES-256 (AE-1 and AE-2) and ZipCrypto extract with the right password, byte for byte, and the host believes the folder;
  - with no password, `PasswordRequired` and nothing in the destination;
  - a wrong password, `PasswordIncorrect` and nothing in the destination;
  - an Arabic password on WinRAR's ZipCrypto sample (ANSI) and on its AES sample (UTF-8);
  - a flipped byte in an AE-2 entry fails on the HMAC; a flipped byte in an AE-1 or ZipCrypto entry fails on the CRC; nothing is kept;
  - an entry whose central record disagrees with `ZipArchive`, a local header in the wrong place, and packed data shorter than the cipher's header all fail cleanly;
  - an encrypted bomb is stopped by the same limits;
  - Test with the password checks every entry; Info names the method;
  - the password appears in no result message;
  - the real worker process runs it with the parameters.
- **Host:** a single-archive job that ends `PasswordRequired` asks and runs again with the secret; a multi-archive one does not ask; the secret is never logged.
- **Explorer:** a drop on Extract, then the dialog typed for real, then the folder checked; a drop with a wrong password, then the toast.
- **0 warnings** in Debug and Release. **One evaluator round.**
