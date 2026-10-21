# Dependency and license matrix

Every package and platform component the product or its tests use. Versions are pinned in `Directory.Packages.props`. `dotnet list WinTools.slnx package --vulnerable --include-transitive` runs in CI.

## Shipped product

The product ships **no third-party packages**.

| Component | Purpose | License / redistribution | Notes |
|---|---|---|---|
| .NET 10 runtime (framework-dependent) | Host and worker runtime | MIT (.NET Foundation) | Not bundled in this slice. Packaging is ADR-0008. |
| Windows Imaging Component (Windows) | PNG/JPEG decode and encode in the worker | Part of Windows; nothing is redistributed | Microsoft codecs are created by CLSID only (ADR-0006, SEC-003). |
| Win32 / OLE (user32, gdi32, ole32, shell32, shcore) | Hooks, overlay window, drop target, DPI | Part of Windows | Hand-written P/Invoke, isolated in `WinTools.Windows`. |

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
