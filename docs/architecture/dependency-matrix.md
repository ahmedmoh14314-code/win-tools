# Dependency and license matrix

Every package and platform component the product or its tests use. Versions are pinned in `Directory.Packages.props`. `dotnet list WinTools.slnx package --vulnerable --include-transitive` runs in CI.

## Shipped product

| Component | Purpose | License / redistribution | Notes |
|---|---|---|---|
| **SkiaSharp 4.153.1** (+ `SkiaSharp.NativeAssets.Win32` 4.153.1: `libSkiaSharp.dll`, 12.9 MB, win-x64) | WebP decode and encode in the **worker only**. Windows has no WebP encoder, and its WebP decoder is an optional Store extension | MIT (SkiaSharp). Skia is BSD-3-Clause, libwebp BSD-3-Clause, plus the libraries listed in the package's `THIRD-PARTY-NOTICES.txt`. Redistribution and commercial use are allowed; the notices must ship with the binaries (packaging, ADR-0008) | Upstream: github.com/mono/SkiaSharp. Released 2026-09-30, actively maintained (pushed 2026-10-21, about 5.6k stars). Native, statically linked, needs no VC++ redistributable. Security: Skia and libwebp are fuzzed by Chrome/OSS-Fuzz, and libwebp had CVE-2023-4863 (heap overflow in lossless decoding), fixed in 1.3.2, which SkiaSharp has bundled since 2.88.6. They run only in the disposable worker. The 96 MB native `.pdb` is not needed at run time and is excluded from shipping. |
| **qpdf 12.4.2** (official `qpdf-12.4.2-msvc64.zip`; bundled: `qpdf.exe`, `qpdf30.dll` 7.4 MB, `msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`) | PDF structure in the **PDF worker only** (ADR-0022): info, merge, split, rotate; later protect, unprotect, optimize, linearize, repair. Run as a child process with an argument list | Apache-2.0 (qpdf, NOTICE: sphlib MIT, Rijndael public domain). Statically linked: zlib 1.3.2 (zlib), libjpeg-turbo 3.2.0 (IJG + BSD-3-Clause), OpenSSL 3.6.4 (Apache-2.0); the VC++ runtime DLLs are Microsoft redistributables. Redistribution and commercial use allowed; notices in `third-party/qpdf/` | Upstream github.com/qpdf/qpdf, released 2026-09-26, monthly cadence, two maintainers. Archive SHA-256 `db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d`, in the release's hash list PGP-signed by key `C2C96B10011FE009E6D1DF828A75D10998012C7E` (verified "Good"); per-file hashes pinned in `scripts/Get-Engines.ps1`. Security: a parser of hostile input; fuzzed upstream in OSS-Fuzz; runs only in the disposable worker with a time limit and the job's memory cap. `qpdf30.dll` imports WS2_32 (OpenSSL); no network use by the commands we run (not verified with a process monitor). |
| **FFmpeg 8.1.3** (BtbN `win64-lgpl-shared` build, release autobuild-2026-10-24-13-02; bundled: `ffmpeg.exe`, `ffprobe.exe`, `avcodec-62`, `avformat-62`, `avfilter-11`, `avdevice-62`, `avutil-60`, `swresample-6`, `swscale-9`; about 160 MB) | Video and audio in the **media worker only** (ADR-0028), run as a child process | LGPL-3.0 (configured `--enable-version3`, without gpl or nonfree); its libraries (dav1d, libaom, SVT-AV1, libvpx, OpenH264, Opus, Vorbis, LAME, libwebp...) under compatible licences; notices and source location in `third-party/ffmpeg` | Upstream FFmpeg, very active; BtbN's builds are the reference Windows builds. Archive SHA-256 `0c2e70f1…981c53` from the release's checksums.sha256, every file pinned in `scripts/Get-Engines.ps1`. Security: a large parser of hostile input with a steady CVE stream; it runs only in the disposable worker, with the file protocol only, probed inputs on an allow-list, and a time limit. Patents on H.264/HEVC/AAC are separate from the licence (ADR-0028). |
| **PDFium chromium/8111** (pdfium-binaries `pdfium-win-x64.tgz`; bundled: `pdfium.dll`, 7.6 MB) | PDF pages to PNG/JPG, PDF text to TXT/Markdown, and checking new PDFs, in the **image worker only** (ADR-0029), loaded by full path | BSD-3-Clause (PDFium); built in: FreeType (FTL), libjpeg-turbo (IJG + BSD-3-Clause), libpng, OpenJPEG (BSD-2-Clause), Little CMS (MIT), ICU, HarfBuzz, Highway, Abseil, AGG 2.3, fast_float, dragonbox, LLVM libc, all permissive; notices in `third-party/pdfium`. Redistribution and commercial use allowed | Upstream: Chrome's PDF engine; binaries from github.com/bblanchon/pdfium-binaries (weekly builds per Chromium release, widely used). Built with `pdf_enable_v8=false`, `pdf_enable_xfa=false`: no JavaScript, no XFA. Archive SHA-256 `3452d110…c7d23e`, DLL `a318c39c…eab89e8f`, both pinned in `scripts/Get-Engines.ps1`. Security: a large parser of hostile input with regular fixes upstream (fonts, JPEG 2000, JBIG2); fuzzed in OSS-Fuzz; runs only in the disposable worker with the job's memory cap and time limit; updated by moving the pin. Alternatives: Ghostscript/MuPDF (AGPL, refused), Windows.Data.Pdf (no text), PdfPig (text only, a second parser) |
| .NET 10 runtime (framework-dependent) | Host and worker runtime | MIT (.NET Foundation) | Not bundled in this slice. Packaging is ADR-0008. |
| Windows Imaging Component (Windows) | PNG, JPEG, BMP and TIFF decode and encode in the worker | Part of Windows; nothing is redistributed | Microsoft codecs are created by CLSID only (ADR-0006, SEC-003). |
| Win32 / OLE (user32, gdi32, ole32, shell32, shcore) | Hooks, overlay window, drop target, DPI | Part of Windows | Hand-written P/Invoke, isolated in `WinTools.Windows`. |
| GDI+ (gdiplus.dll) | Anti-aliased shapes of the overlay, toast, info panel and icon (ADR-0016) | Part of Windows; nothing is redistributed | Flat API by hand-written P/Invoke in `WinTools.Windows/Native/Gdiplus.cs`. Draws only our own vector shapes, never decodes a user file. |
| winmm (`PlaySound`), dwmapi, `Shell_NotifyIcon` | UI sounds, the settings window's dark title bar, the tray icon | Part of Windows | Sounds are synthesised in code; no audio asset or codec is involved. |
| WPF (`Microsoft.WindowsDesktop.App`, .NET 10) | The settings window only, in its own process (`WinTools.Settings.exe`) | MIT (.NET Foundation) | Part of the .NET desktop runtime, not a package. Never loaded by the resident host (test: `UiSurfaceTests.The_host_never_loads_wpf`). |

### Licence review of `libSkiaSharp.dll` (2026-10-21)
- **Notices file:** `THIRD-PARTY-NOTICES.txt` lists the whole Skia third-party tree. Some entries are copyleft or dual-licensed:
  - libmicrohttpd: LGPL/eCos;
  - the old GIF decoder: MPL-1.1/GPL-2.0/LGPL-2.1 tri-licence;
  - FreeType: FTL or GPL-2.0.
- **Which of them ship:**
  - libmicrohttpd is used only by Skia's debugger tools; SkiaSharp builds with tools disabled.
  - On Windows, Skia uses DirectWrite for fonts, not FreeType.
  - GIF decoding in this Skia is wuffs (Apache-2.0/MIT).
  - Stronger evidence comes from the package's native `libSkiaSharp.pdb` (evaluator, 2026-10-21). It contains no FreeType, libmicrohttpd, old GIF decoder or DNG SDK symbols. It does contain wuffs, libwebp, the DirectWrite font manager and **expat** (MIT), which also needs a notice.
- **Where a dual-licensed part is present,** we use it under its permissive option: MPL-1.1, FTL or zlib.
- **Conclusion:** no GPL/AGPL obligation was found.
- **Follow-up:** re-run this review at every SkiaSharp upgrade and before packaging.

## Tests only (never shipped)

| Package | Version | License | Purpose | Maintenance / security | Why chosen |
|---|---|---|---|---|---|
| xunit | 2.9.3 | Apache-2.0 | Test framework | Active, .NET Foundation project, no known advisories | Already used and audited in the spike. No product exposure. |
| xunit.runner.visualstudio | 3.1.1 | Apache-2.0 | `dotnet test` adapter | Active | Required by xunit for `dotnet test` |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | Test host | Microsoft | Required by `dotnet test` |

Alternatives considered for tests: MSTest (MIT) and NUnit (MIT) are equivalent. xunit was kept for continuity with the spike's 51 tests.

## Development tooling outside the build
- GitHub Actions `actions/checkout` v7.0.1 and `actions/setup-dotnet` v6.0.0 (MIT), pinned by commit SHA in `.github/workflows/ci.yml`.
- `tests/samples/New-Samples.ps1` uses Windows PowerShell 5.1 and System.Drawing (GDI+), both part of Windows, to regenerate the committed test images with an encoder independent of WIC.
- `tests/samples/New-EncryptedZipSamples.ps1` uses **WinRAR 6.24** (installed on the developer machine, signed by win.rar GmbH) to write the encrypted ZIP samples, an encoder independent of the worker's decryption (V2 PR 5.3). Its output is committed; nothing in the build or CI needs it, and none of its code ships.
- `tests/samples/New-WebPSamples.py` uses **Pillow** (HPND licence, with its own libwebp) to write the lossless WebP samples, an encoder independent of SkiaSharp. It is a developer-machine tool only; nothing in the build or CI needs it.
