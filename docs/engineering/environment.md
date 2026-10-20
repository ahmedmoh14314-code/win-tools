# Development environment snapshot (updated 2026-10-19, evening)

| Item | Found | Note |
|---|---|---|
| OS | Windows 11 Pro 10.0.26200 | |
| .NET SDK | **10.0.402** (installed 2026-10-19 via winget, `Microsoft.DotNet.SDK.10`), 8.0.200 still present | Pinned by root `global.json` (`rollForward: latestPatch`, no prerelease). Runtime 10.0.13. `dotnet --info`: MSBuild 18.9.15, RID win-x64. |
| Visual Studio | 2022 present | MSBuild not on PATH; use `dotnet` CLI or the VS developer shell |
| Windows SDK | 10.0.22621 | |
| git / gh | present | identity configured globally |
| winget | present | |
| ffmpeg | present (winget) | Not a project dependency; must not be assumed on user machines |
| ImageMagick | absent | Deliberately not installed (license review required first) |

Update this file when the toolchain changes; keep it factual.
