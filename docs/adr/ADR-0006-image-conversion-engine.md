# ADR-0006: Image conversion engine for V1 (JPG/PNG/WebP; convert, compress, resize)

- **Status:** Accepted (2026-10-21) for the first product slice (scope: PNG↔JPEG; WebP, resize and quality are separate decisions); evaluator review recorded below
- **Date:** 2026-10-19
- **Related requirements:** SCOPE-001, ARCH-002, DEP-001, SEC-001

## Context
V1 needs decode/encode for JPEG, PNG and WebP plus resize and quality control. Candidates differ in license, binary size, WebP support, quality, metadata handling, security history and whether they are managed or native. The engine sits behind an adapter and runs in the worker (ADR-0005), so a later swap is acceptable, but the license decision is not reversible once shipped.

## Decision
**Windows Imaging Component (WIC), called directly from the worker through about 150 lines of COM interop (`src/WinTools.Worker/Imaging/Wic.cs`).** No package and no WPF imaging.
- **Codec selection:** codecs are created by CLSID: `CLSID_WICPngDecoder2`, `CLSID_WICJpegDecoder`, `CLSID_WICPngEncoder`, `CLSID_WICJpegEncoder`. This always selects Microsoft's built-in codec, never a third-party codec registered for the same container (SEC-003). A test asserts each codec's vendor GUID.
- **Memory and limits:** all decoding and encoding happens in memory (`CreateStreamOnHGlobal`); .NET does the file I/O, which covers long and Unicode paths. Inputs are capped at 256 MB, 100 MP and 65,535 px per side.
- **Before decoding pixels:** the content signature must match the operation's source format, and the header must pass the size limits. The container must also be complete (`ContainerCheck`): PNG chunk lengths, CRCs and IHDR…IEND, with the IDAT stream inflating to exactly the byte count IHDR implies; JPEG markers and scans walked through to EOI. Decoders hide damage: the PNG decoder fills the rows of a cut zlib stream, and the JPEG decoder pads a cut scan, reporting it only as text on stderr. The worker therefore points its stderr, including C runtime descriptor 2, at a pipe it owns, and any codec text during a decode is a `DecodeFailed`.
- **One decode:** the frame is converted once, sequentially, into an in-memory bitmap (`CreateBitmapFromSource`, cache on load). A flip-rotator placed directly over the JPEG decoder re-reads scanlines per pixel: a 12 MP portrait photo took about 5 minutes (evaluator B1). From the in-memory bitmap it is fast, which a 4000×3000 orientation-6 test bounds.
- **PNG → JPEG:** BGRA is composited onto opaque white in 64-row bands and encoded as 24-bit BGR. The JPEG encoder uses its default quality.
- **JPEG → PNG:** the EXIF orientation is applied with `IWICBitmapFlipRotator` over the in-memory bitmap; all 8 values are tested on pixels. Output is 24-bit BGR.
- **Validation:** the output is read back from disk, then its signature and structure are checked, its size compared, and every pixel decoded.
- **Not handled:** only the first frame is converted. Metadata (EXIF, GPS, ICC) is not copied, so colours of wide-gamut images may shift.

## Alternatives considered (to be completed in the dependency matrix)
- **WPF imaging (`System.Windows.Media.Imaging`):** smaller interop surface. Rejected because it pulls WPF into the worker and hides codec selection, and Spike 002C showed costs we would rather not carry.
- **ImageSharp:** split license (commercial terms above a revenue threshold), so an ADR would be needed. Not needed for PNG/JPEG.
- **SkiaSharp:** native binaries add size and more security surface. Not needed for PNG/JPEG.
- **libvips:** LGPL and native. Not needed.
- **ImageMagick, FFmpeg:** excluded for this slice (owner decision). Both need a license review.
- **WebP:** WIC has a WebP decoder (with the Windows WebP extension) but no built-in encoder. WebP is a separate decision for the next format.

## Consequences
- No third-party code in the conversion path, and no package to patch. Codec fixes arrive through Windows Update.
- We own the COM declarations: vtable order matches `wincodec.h`, and each method is exercised by the conversion tests.
- JPEG output quality is the WIC default. A quality setting needs its own decision and a compression UI.

## Evidence / experiments
- `tests/WinTools.Worker.Tests/ConversionJobTests.cs` covers 32 conversion and safety cases on real files: GDI+-generated samples in `tests/samples/` and inputs crafted by the tests. They include colour checks per quadrant, alpha over white, all 8 EXIF orientations, a truncated PNG and JPEG, a corrupt JPEG, a zero-byte file, mislabelled content, a 60,000×60,000 header, Unicode and emoji names, and a path longer than 260 characters.
- `CodecVendorTests` checks that all four codecs are Microsoft's.

## Evaluator review
Evaluator for the first product slice (2026-10-21), working from the code, the tests, its own probes against the built worker, and the raw end-to-end logs.
- Pass 1: CHANGES REQUIRED. B1: a rotated JPEG took about 5 minutes through the flip-rotator. M1: a folder with a trailing dot converted its twin. M2: a lone surrogate was altered on the pipe. M3: damaged image data inside an intact container was reported as success. M4: grace/DragEnter race. M5: the harness could not see a lost drop. Plus 12 minor items.
- Pass 2: MERGE once the minor items are fixed or recorded. The evaluator re-probed and confirmed B1 and M1–M3 fixed; no blocker or major remains. The minor items found in pass 2 are fixed (alternate-data-stream paths, out of memory reported as ImageTooLarge, job-object assignment logged, path policy re-checked in the worker client) or recorded as known limitations in the PR.
