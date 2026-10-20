# Dependency rules

- **No dependency without a matrix entry** in `docs/architecture/dependency-matrix.md` (created when the first dependency is proposed): purpose · license · redistribution rights · commercial-use implications · binary size · maintenance status · security history · alternatives · why chosen.
- **License policy**: MIT, BSD, Apache-2.0, MS-PL and .NET Foundation licenses are fine. LGPL requires dynamic linking and an ADR. **GPL/AGPL components are not allowed in the shipped product.** FFmpeg, if ever used, must be an LGPL-configured build; ImageMagick's license obligations must be reviewed before adoption. Any "free for non-commercial" or dual-licensed library needs an ADR.
- **Prefer the platform first**: Windows Imaging Component, Win32/COM via `CsWin32` or hand-written P/Invoke, `System.Threading.Channels`, and `Microsoft.Extensions.*` only where they pay for themselves.
- **No actor frameworks, no DI mega-frameworks, no MVVM frameworks** until an ADR shows a concrete need.
- **Engines load on demand** and sit behind adapters; the UI assembly must not reference an engine package.
- **Pin versions** (central package management once the solution exists). Record the exact version in the matrix.
- Run `dotnet list package --vulnerable` before any checkpoint that changes dependencies.
- Third-party development tooling (editor or automation plugins, generators) follows the same discipline: audit the source, pin the version, scope it to the project, keep a record of the audit.
