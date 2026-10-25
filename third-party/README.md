# Third-party software bundled with WinTools

The licence and notice texts of every bundled component, as each project publishes them. The installer build joins
every file here (except this one) into `THIRD-PARTY-NOTICES.txt` in the installed folder. The dependency matrix
(`docs/architecture/dependency-matrix.md`) records versions, hashes and why each component was chosen.

| Folder | Component | Version | Licence | Source of these texts |
|---|---|---|---|---|
| `qpdf/` | qpdf (PDF worker, ADR-0022) | 12.4.2 | Apache-2.0; NOTICE (sphlib MIT, Rijndael public domain) | `LICENSE.txt`, `NOTICE.md` at tag v12.4.2 of github.com/qpdf/qpdf |
| `qpdf/` | zlib, statically linked into qpdf | 1.3.2 | zlib | `share/zlib/copyright` in the release's `vcpkg.zip` (SHA-256 in the signed hash list) |
| `qpdf/` | libjpeg-turbo, statically linked into qpdf | 3.2.0 | IJG and BSD-3-Clause | `share/libjpeg-turbo/copyright`, same archive |
| `qpdf/` | OpenSSL, statically linked into qpdf | 3.6.4 | Apache-2.0 | `share/openssl/copyright`, same archive |
| `dotnet/` | .NET runtime and Windows Desktop runtime (self-contained in the installer) | 10.0.13 | MIT; the notices of the libraries they include | `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` of the runtime packs the payload is published with (Microsoft.NETCore.App.Runtime.win-x64, Microsoft.WindowsDesktop.App.Runtime.win-x64) |
| `skiasharp/` | SkiaSharp and its native library (image worker) | 4.153.1 | MIT; the libraries in its notices file | `LICENSE.txt` of the SkiaSharp package; `THIRD-PARTY-NOTICES.txt` of SkiaSharp.NativeAssets.Win32 |

The Visual C++ runtime DLLs next to qpdf (`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`) are files from
Microsoft's Visual C++ Redistributable, which the qpdf project ships app-locally with its own build; WinTools passes them on
unchanged, next to the program that needs them, under the terms that let a Visual Studio licensee redistribute them (the
"REDIST" list). Installing the official Visual C++ Redistributable instead is the alternative if those terms are ever in doubt.
