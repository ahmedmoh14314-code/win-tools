# PDFium

- **What:** `pdfium.dll`, PDFium built by the pdfium-binaries project, release `chromium/8111` (PDFium 158.0.8111.0), Windows x64.
- **Source:** https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8111 (archive `pdfium-win-x64.tgz`, SHA-256 `3452d11031050ec65e57f180215b57b9e6d465e3fbedafa8a77ad90885c7d23e`; `bin/pdfium.dll` SHA-256 `a318c39c137482dc4687c2973b7ec1c27fc1c20253cf2dc31928f922eab89e8f`).
- **Build:** `pdf_enable_v8 = false`, `pdf_enable_xfa = false`: no JavaScript engine and no XFA forms.
- **Licence:** PDFium is BSD-3-Clause (`LICENSE`). The libraries built into it keep their own licences, all permissive (`licenses/`): FreeType (FTL), libjpeg-turbo (IJG and BSD), libpng, OpenJPEG (BSD-2), Little CMS (MIT), ICU, HarfBuzz, Highway, Abseil, AGG 2.3, fast_float, dragonbox and LLVM libc.
- **Use:** loaded by full path by the image worker only, to render PDF pages and read their text (ADR-0029).
