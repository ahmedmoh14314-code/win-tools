# Threat model (V1 image conversion: PNG, JPEG, WebP, BMP, TIFF)

Scope: the resident host, the worker process and the files they touch. Everything runs locally as the interactive user. There is no network, no elevation and no telemetry.

## Assets
1. The user's source files. They must never be modified, moved or deleted.
2. Other files in the destination folder. They must never be overwritten.
3. The user's trust in a result: a file that looks like a successful conversion must be one.
4. The responsiveness of the desktop. The global hooks run in every input path.

## Entry points and trust boundaries
| Entry point | Untrusted input | Boundary / control |
|---|---|---|
| OLE drop on the overlay | Data object from any drag source; CF_HDROP path list | Only CF_HDROP is read, and only if the source allows COPY. At most 10,000 paths are parsed and more than 50 get no commands. Paths must pass `PathPolicy`: canonical (no trailing dots or spaces that Win32 would strip, which would name a different file), well-formed UTF-16, not verbatim or device paths. The effect is COPY or NONE, never MOVE |
| File content | Any bytes under any name | The extension is a hint only. The worker checks the signature and enforces size limits (256 MB, 100 MP, 65,535 px per side). It checks the container structure: PNG chunks, CRCs, and IDAT inflating to the size IHDR implies; JPEG markers and scans through to EOI; WebP RIFF chunks; the BMP pixel array (RLE refused); TIFF strips and tiles (bounds, plus decompressed size for uncompressed, Deflate, PackBits and LZW). Any codec diagnostic on the worker's own stderr pipe counts as damage. Multi-page and animated inputs are refused. WebP payloads and JPEG/CCITT TIFF strips carry no checksum, so damage inside them can go undetected (residual). Decoding happens in a separate process |
| Image codecs (WIC, SkiaSharp/libwebp) | Parsers are a classic attack surface | WIC: Microsoft codecs only, created by CLSID (SEC-003); a test asserts the vendor. WebP: SkiaSharp's bundled libwebp (version tracked in the dependency matrix; CVE-2023-4863 class). Both run only in the worker, never in the host |
| Host ↔ worker pipe | Worker output after it has parsed hostile input | Lines are limited to 64 KiB, checked after reading. Malformed or mismatched results are rejected. The host re-checks every success claim: the name must match the naming policy and the file's SHA-256 must equal the hash the worker validated. There is a per-job timeout and kill; a kill-on-close job object ends any worker that outlives the host |
| Output path | Destination folder contents can change concurrently | The temp file is created exclusively (`CreateNew`) in the destination folder, then renamed to the first free name without overwriting. A folder that is, or sits inside, a symbolic link or junction is refused. Nothing is written outside the source's folder or the configured output folder |
| Settings file (`%APPDATA%\WinTools\settings.json`) | Hand-edited or corrupted JSON, written by the user or another program running as the user | Parsed with `System.Text.Json` into a fixed record. Every value is repaired to a safe default: trigger key from an allow-list (no modifiers), qualities clamped, an output folder that is not canonical and absolute falls back to the same folder. The worker re-validates the folder (canonical, existing, no link). A corrupt file is moved aside; a newer schema is never overwritten. No secrets are stored |
| Batch drops | Up to 1000 paths, possibly the same file twice or many with one stem | At most 4 workers; each file gets its own exclusive temp and no-overwrite name. A crash or hang fails only that file and restarts that lane at most 3 times |
| Global input hooks | Every keystroke and mouse event on the desktop | The callbacks read three fields, swallow nothing, log nothing and allocate nothing |

## Codec-loading risk (SEC-003)
`CreateDecoderFromFilename`/`FromStream` would pick any registered WIC codec, including third-party ones installed by camera or RAW packs. That would run unknown parsers in our process. The worker never uses the auto-selecting factories. It creates the four built-in codecs by CLSID, and `CodecVendorTests` asserts that each one reports `GUID_VendorMicrosoft`.

## Residual risks (accepted for this slice)
- The worker runs with the user's full token. A restricted token or AppContainer is future hardening.
- If something other than the host kills the worker between writing the temp file and renaming it, a hidden `~wintools-<id>.tmp` can remain in the destination folder. It cannot be mistaken for a result.
- Codec diagnostics are captured by re-pointing the worker's standard error, including C runtime descriptor 2 in msvcrt.dll and ucrtbase.dll, at a pipe it owns. If a future codec reports damage some other way, the structural checks and full decode remain the defence.
- A folder named like an image (`x.png`) is offered a command and then fails with `SourceNotReadable`.
- Reading the source at conversion time hydrates a OneDrive placeholder. Capability resolution never reads files, so dragging alone never hydrates anything.
- A source deleted after it was read still produces its output, converted from the bytes the user dropped.
- Logs contain output file names, operation ids and error codes. They never contain file contents or full source paths. They stay under `%LOCALAPPDATA%\WinTools\logs`.
- UIPI: input to elevated windows is not seen by the hooks. This is documented, not worked around.
- Remove metadata is not a forensic anonymiser. Pixel content (faces, visible text), the file name and file-system timestamps are untouched, and unknown private data inside the pixel stream is not inspected.
- A batch of N files at the maximum image size can use up to 4× the per-worker memory. Lower limits and a job-object memory cap are planned hardening (TASK 5).
