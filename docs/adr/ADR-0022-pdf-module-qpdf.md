# ADR-0022: The PDF module's structural commands run the official qpdf build in the PDF worker

- **Status:** Proposed (V2 PR 2.1; PR 2.3 adds decision 8)
- **Date:** 2026-10-25
- **Deciders:** product owner (V2 plan §7, §9; unattended-session authorisation to fetch official engine releases)
- **Related requirements:** V2 plan §9 (PDF), §10 PR 2.1, §14 (PDF threats); ADR-0005 (output safety), ADR-0018 (job contract)

## Context
- V2 adds PDF commands. The plan's shortlist names qpdf for structure (merge, split, extract, rotate, encryption, linearize, repair) and PDFium for rendering and text.
- This PR takes the first slice: PDF info, Merge, Split into pages, Rotate.
- They need no rendering, no password and no dialog.

## Decision
1. **qpdf 12.4.2, the project's own Windows x64 release** (`qpdf-12.4.2-msvc64.zip` from github.com/qpdf/qpdf/releases), is bundled with the PDF worker.
   - It runs as a child process. Only five files are taken: `qpdf.exe`, `qpdf30.dll`, and the Visual C++ runtime it imports (`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`).
   - Provenance:
     - The archive's SHA-256 (`db87077e…25d`) matches the release's hash list.
     - That list is PGP-signed by the maintainer's key `C2C9 6B10 011F E009 E6D1 DF82 8A75 D109 9801 2C7E`. The key was taken from the maintainer's GitHub account, and the signature verified "Good".
     - `scripts/Get-Engines.ps1` pins the archive hash and each extracted file's hash. Any mismatch stops the build.
   - The binaries are not committed. The build fails with an instruction when they are missing, and CI fetches them.
2. **Launch:**
   - `ProcessStartInfo.ArgumentList` only, with fully qualified paths (no argument can start with `-` or `@`).
   - A private environment (`SystemRoot`, `windir`, `TEMP`, `TMP` only); the qpdf folder as working directory; standard input closed (no prompt can wait).
   - Output read up to 64 KB; a 4-minute limit per run; the process tree killed when the limit passes.
   - The PDF worker itself is a disposable process in the host's kill-on-close job, so qpdf is inside it too.
3. **Before qpdf sees a file:**
   - The worker checks that it is a local file (not a folder, not an online-only placeholder), at most 1 GB and non-empty.
   - It must be a PDF by its header (`%PDF-` in the first 1024 bytes), whatever its name says.
   - A file that needs a password to open is refused with `PasswordRequired`. The worker never guesses or prompts; protection comes in PR 2.2 with the password dialog.
4. **Outputs follow ADR-0005:**
   - qpdf writes a temporary file that the worker created exclusively in the destination.
   - The worker checks it: qpdf can open it, and it has the expected page count (the sum for Merge, the same for Rotate).
   - It marks it with the source's zone (for Merge, the strictest zone of the sources), then moves it to the first free name without overwriting.
   - **Split** writes into a hidden staging folder with qpdf's fixed `page-%d.pdf` pattern, so a user's file name never reaches qpdf's `%d` expansion. Then each page is checked (header, end marker, count equal to the source's pages), marked, and renamed `<name> - page NN.pdf`, and the folder is renamed to the first free `<name> (pages)`.
   - A failure deletes whatever was written. A killed worker leaves only the deterministic temp name, which the host removes, file or folder.
5. **The host believes a success only from the disk:**
   - the allowed name;
   - a `%PDF-` header;
   - the SHA-256 the worker validated;
   - for Split, a manifest hash over every page's name and SHA-256 (`FolderManifest`), recomputed from the folder, with exactly the expected page names.

   The manifest keeps a 2,000-page split within one 64 KB protocol line.
6. **Menu:**
   - "Organize" (Merge, Split into pages) is the module's own category. Rotate (Left, Right, 180°) and Tools (PDF info) are shared with the image module.
   - Merge needs two PDFs or more (at most 200) and joins them in Explorer's order.
   - A selection with any non-PDF gets no PDF command.
   - At most two PDF workers run at once.
7. **Failure wording follows the module's noun:** "The PDF is damaged or incomplete", not the image module's sentences (`FeedbackText.Reason(error, words)`).

8. **Optimize, Fast web view, Repair (V2 PR 2.3)** sit in their own "Optimize" category.
   - **Smaller (lossless):** `--object-streams=generate --compress-streams=y --recompress-flate --compression-level=9`. No image is re-encoded, so nothing visible changes. qpdf's `--optimize-images` is not used: it re-encodes images as JPEG, which is lossy.
     - The copy is kept only when it saves at least 1 % and 1 KB. Otherwise the result is "Already optimized" and nothing is written.
     - It is never promised to shrink a file. A generated 300-page PDF went from 68.9 KB to 39.5 KB; the same file again saved nothing.
   - **Fast web view:** `--linearize`. A file that already passes `--check-linearization` is left alone ("already set up").
   - **Repair:** a rewrite of a file that `--check` flags. qpdf rebuilds a damaged cross-reference table on reading.
     - It is labelled best effort: content lost in the damage stays lost.
     - A file that `--check` passes is reported as having no damage, and nothing is written.
     - A protected file is refused before the check.

## Alternatives considered
- **PDFium for these commands.** It cannot write encrypted files, and its provenance spike (V8 and XFA off, a reproducible build) is still to come. qpdf is the right engine for structure; PDFium comes in Stage 3 for rendering only.
- **Building qpdf from source.** It is reproducible, but it needs a C++ toolchain in CI for no gain over the signed official build. Revisit if the project's builds stop.
- **Committing the binaries.** Rejected: binary churn in a public repository. A pinned fetch with hashes gives the same reproducibility.
- **The mingw build.** It is the same code with a different runtime. The msvc build's runtime DLLs ship with it, and our other components already target the MSVC world.
- **Passing a password on the command line.** Never: PR 2.2 uses `--password-file=-` on standard input.

## Consequences
- **Notices** (`third-party/`, collected into `THIRD-PARTY-NOTICES.txt` by the installer build):
  - qpdf is Apache-2.0, with its NOTICE (sphlib MIT, Rijndael public domain);
  - statically linked: zlib 1.3.2 (zlib), libjpeg-turbo 3.2.0 (IJG and BSD-3-Clause), OpenSSL 3.6.4 (Apache-2.0);
  - Microsoft's Visual C++ runtime DLLs are redistributable files.
  - No GPL or AGPL component.
- **Size:** about 8.6 MB of engine in the installer payload (the archive is 28 MB).
- **Network:** `qpdf30.dll` imports `WS2_32.dll` through OpenSSL's I/O layer. qpdf has no network feature, and no command we run opens a URL, but this is not verified with a process monitor. It is listed in the threat model.
- **Servicing:** qpdf releases about monthly (V2 plan D10). An upgrade means:
  1. change the version and hashes in `Get-Engines.ps1`;
  2. re-check the signature;
  3. re-run the PDF tests.

## Evidence / experiments
- `PdfWorkerTests` (16, real qpdf):
  - info facts;
  - rotate (each page object has `/Rotate`);
  - merge (page count, sources unchanged);
  - split (one file per page, manifest);
  - a "%d" and "@" name;
  - Arabic, emoji and spaces;
  - a taken name kept;
  - a renamed program refused before qpdf;
  - a truncated file fails cleanly;
  - an encrypted file refused for every command, and Info says it is protected;
  - the zone carried;
  - forged claims not believed;
  - the real worker process through `WorkerClient`.
- `PdfCapabilitiesTests` (Core): the menu for one PDF, two PDFs, mixed selections and images; names; extensions.
- qpdf behaviour measured on generated files (2026-10-25):

  | Command | Result |
  |---|---|
  | `--show-npages` | 0 on a good file; 2 on a truncated or non-PDF file; 2 "invalid password" on an AES-256 file |
  | `--requires-password` | 0 when protected, 3 with an owner password only, 2 otherwise |
  | `--check-linearization` | "no linearization errors" on a linearized file |
  | `--show-crypto` | openssl, native |
