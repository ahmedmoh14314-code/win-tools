# ADR-0006: Image conversion engine for V1 (JPG/PNG/WebP; convert, compress, resize)

- **Status:** Accepted (2026-10-21). Formats: PNG, JPEG, WebP, BMP and TIFF as both input and output. SVG and HEIC are deferred (ADR-0012). Resize, compression and rotation get their own update.
- **Date:** 2026-10-19
- **Related requirements:** SCOPE-001, ARCH-002, DEP-001, SEC-001

## Context
V1 needs decode/encode for JPEG, PNG and WebP plus resize and quality control. Candidates differ in license, binary size, WebP support, quality, metadata handling, security history and whether they are managed or native. The engine sits behind an adapter and runs in the worker (ADR-0005), so a later swap is acceptable, but the license decision is not reversible once shipped.

## Decision
**Two engines, both only in the worker, meeting at one neutral in-memory image** (`RasterImage`: 32-bit BGRA, straight alpha):
- **Windows Imaging Component** for PNG, JPEG, BMP and TIFF (decode and encode).
  - Codecs are created by CLSID: Microsoft's built-in codecs, never a third-party codec registered for the same container (SEC-003). A test asserts the vendor of all eight.
  - Interop is about 200 lines of COM declarations (`src/WinTools.Worker/Imaging/Wic.cs`).
- **SkiaSharp 4.153.1 (bundled libwebp)** for WebP (decode and encode).
  - Windows has no WebP encoder, and its WebP decoder is an optional Store extension, so one engine handles both directions on every machine.
  - Skia is never loaded by the resident host.

Pipeline (`ImagePipeline`):
1. **Signature:** the content signature decides the format, never the extension.
2. **Header and limits:** the header is read and the size limits checked (100 MP, 65,535 px per side, 256 MB input; revisited in hardening) before any pixel buffer is allocated.
3. **Structure check (`ContainerCheck`):**
   - PNG: chunks, CRCs, and the zlib stream inflating to exactly the size IHDR implies.
   - JPEG: markers and scans through to EOI.
   - WebP: RIFF size and chunks.
   - BMP: the pixel array fits the file.
   - TIFF: no structural walk (offsets can point anywhere); it relies on a full decode plus the codec diagnostics.
4. **One sequential decode to BGRA.** For WIC, any codec diagnostic on the worker's stderr pipe means `DecodeFailed`. For Skia, `IncompleteInput` and other non-success results fail.
5. **EXIF orientation** (JPEG, TIFF, WebP) is applied to the pixels in managed code. The flip-rotator is not used, because of its per-pixel re-reads (evaluator B1).
6. **Encode:**
   - PNG and TIFF are written as 32-bit BGRA only when a pixel is transparent, otherwise 24-bit BGR.
   - JPEG and BMP are composited onto **white**.
   - WebP is lossy at quality 90 by default, with alpha kept.
   - JPEG uses the WIC default quality (0.9); `ImageQuality` can be set through the encoder property bag.
7. **Validation:** the written temp file is read back, and its signature, structure, full decode and dimensions are checked.

**Not handled:** only the first frame is converted, including for animated WebP. Metadata (EXIF, GPS, ICC) is not copied.

## Alternatives considered (to be completed in the dependency matrix)
- **WPF imaging:** pulls WPF into the worker and hides codec selection.
- **ImageSharp:** its split licence charges companies above a revenue threshold, which the commercial-redistribution rule excludes.
- **Magick.NET / ImageMagick:** large native surface, many delegates and a licence review still needed. Not needed for these formats.
- **libvips:** LGPL and native.
- **Skia for every format:** Skia cannot write BMP or TIFF. WIC keeps the reviewed PNG/JPEG path and Microsoft's codecs.
- **WIC's WebP decoder plus Skia only for encoding:** the WIC decoder is a removable Store extension (Microsoft.WebpImageExtension), so WebP input would fail on some machines.

## Consequences
- The worker grows by `SkiaSharp.dll` (0.6 MB) and `libSkiaSharp.dll` (12.9 MB, win-x64). The 96 MB native `.pdb` is removed from build and publish outputs (`Directory.Build.targets`).
- Skia and libwebp are third-party native parsers of untrusted input. They run only in the disposable worker, with timeouts and a kill-on-close job object, and must be updated when libwebp CVEs appear (see the dependency matrix).
- Adding an output format means one row in `ImageFormats` and an encoder branch; the menu and resolver need no change.

## Evidence / experiments
- `FormatMatrixTests`: all 20 source→target pairs on real files, checked per quadrant against GDI+-generated samples (PNG, JPEG, BMP, TIFF). The WebP source is made losslessly from the PNG sample.
  - Alpha is kept for WebP and TIFF and composited onto white for JPEG and BMP.
  - A PNG→TIFF→PNG round trip is lossless.
  - Truncated WebP, BMP and TIFF are refused.
  - WebP content in a `.png` file is handled by content.
  - Also covered: Arabic and emoji names, 4000×3000 → WebP, TIFF and BMP in bounded time, and portrait shape.
- `ConversionJobTests` (PNG/JPEG safety cases), plus `CodecVendorTests` covering eight WIC codecs.
- Explorer end-to-end (Release, 2026-10-21): PNG→JPEG/WebP/BMP/TIFF, JPEG→PNG/WebP, BMP→WebP and TIFF→JPEG all 5/5; unsupported 3/3; Escape 3/3; fast release (25 ms) to WebP 10/10.

## Evaluator review
Evaluator for the first product slice (2026-10-21), working from the code, the tests, its own probes against the built worker, and the raw end-to-end logs.
- Pass 1: CHANGES REQUIRED. B1: a rotated JPEG took about 5 minutes through the flip-rotator. M1: a folder with a trailing dot converted its twin. M2: a lone surrogate was altered on the pipe. M3: damaged image data inside an intact container was reported as success. M4: grace/DragEnter race. M5: the harness could not see a lost drop. Plus 12 minor items.
- Pass 2: MERGE once the minor items are fixed or recorded. The evaluator re-probed and confirmed B1 and M1–M3 fixed; no blocker or major remains. The minor items found in pass 2 are fixed (alternate-data-stream paths, out of memory reported as ImageTooLarge, job-object assignment logged, path policy re-checked in the worker client) or recorded as known limitations in the PR.
