# Dependency and license matrix

Every package and platform component the product or its tests use. Versions are pinned in `Directory.Packages.props`. `dotnet list WinTools.slnx package --vulnerable --include-transitive` runs in CI.

## Shipped product

| Component | Purpose | License / redistribution | Notes |
|---|---|---|---|
| **SkiaSharp 4.153.1** (+ `SkiaSharp.NativeAssets.Win32` 4.153.1: `libSkiaSharp.dll`, 12.9 MB, win-x64) | WebP decode and encode in the **worker only**. Windows has no WebP encoder, and its WebP decoder is an optional Store extension | MIT (SkiaSharp). Skia is BSD-3-Clause, libwebp BSD-3-Clause, plus the libraries listed in the package's `THIRD-PARTY-NOTICES.txt`. Redistribution and commercial use are allowed; the notices must ship with the binaries (packaging, ADR-0008) | Upstream: github.com/mono/SkiaSharp. Released 2026-09-30, actively maintained (pushed 2026-10-21, about 5.6k stars). Native, statically linked, needs no VC++ redistributable. Security: Skia and libwebp are fuzzed by Chrome/OSS-Fuzz, and libwebp had CVE-2023-4863 (heap overflow in lossless decoding), fixed in 1.3.2, which SkiaSharp has bundled since 2.88.6. They run only in the disposable worker. The 96 MB native `.pdb` is not needed at run time and is excluded from shipping. |
| .NET 10 runtime (framework-dependent) | Host and worker runtime | MIT (.NET Foundation) | Not bundled in this slice. Packaging is ADR-0008. |
| Windows Imaging Component (Windows) | PNG, JPEG, BMP and TIFF decode and encode in the worker | Part of Windows; nothing is redistributed | Microsoft codecs are created by CLSID only (ADR-0006, SEC-003). |
| Win32 / OLE (user32, gdi32, ole32, shell32, shcore) | Hooks, overlay window, drop target, DPI | Part of Windows | Hand-written P/Invoke, isolated in `WinTools.Windows`. |

### Licence review of `libSkiaSharp.dll` (2026-10-21)
- **Notices file:** `THIRD-PARTY-NOTICES.txt` lists the whole Skia third-party tree. Some entries are copyleft or dual-licensed:
  - libmicrohttpd: LGPL/eCos;
  - the old GIF decoder: MPL-1.1/GPL-2.0/LGPL-2.1 tri-licence;
  - FreeType: FTL or GPL-2.0.
- **Which of them ship:**
  - libmicrohttpd is used only by Skia's debugger tools; SkiaSharp builds with tools disabled.
  - On Windows, Skia uses DirectWrite for fonts, not FreeType.
  - GIF decoding in this Skia is wuffs (Apache-2.0/MIT).
  - A string scan of the shipped DLL found no libmicrohttpd or FreeType entry points. Symbols are stripped, so this supports the build configuration but does not prove it.
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
