# ADR-0029: PDF pages to images and text, and images to PDF, with the bundled PDFium

- **Status:** Proposed (Tangerine parity, 0.2.0 Beta)
- **Date:** 2026-10-28
- **Deciders:** product owner (2026-10-25 direction: Tangerine parity, mature engines)
- **Related:** ADR-0022 (qpdf for structure), ADR-0005 (worker isolation), ADR-0006 (image pipeline), dependency rules

## Context
- qpdf (ADR-0022) edits a PDF's structure but cannot draw a page or read its text. Tangerine turns PDFs into pictures and text, and pictures into a PDF.
- Writing a PDF renderer is out of the question. PDFium (Chrome's PDF engine, BSD-3-Clause) is the mature engine with a C API, and the pdfium-binaries project publishes Windows builds per Chromium release, without V8 (JavaScript) and without XFA.

## Decision
1. **The engine:** `pdfium.dll` from pdfium-binaries `chromium/8111`, Windows x64, built with `pdf_enable_v8 = false` and `pdf_enable_xfa = false`.
   - The archive's SHA-256 and the DLL's are pinned in `scripts/Get-Engines.ps1` (`.tgz` unpacked with Windows' own `tar.exe`).
   - Notices in `third-party/pdfium` (PDFium and the permissive libraries built into it).
2. **Where it runs:** in the image worker only, loaded by full path (`NativeLibrary` resolver, never the DLL search path), through `LibraryImport` declarations in `Pdf/Pdfium.cs`. The host never loads it.
3. **Commands (module `pdfrender`):**
   - On a PDF, under Convert: To PNG, To JPG (each page at 150 dpi, smaller when a page would pass the image limits; one page gives a file, more a folder), To TXT and To Markdown (the text PDFium reads, page by page, with Markdown marks escaped so no text becomes markup or HTML; a scanned PDF has no text and says so).
   - On images, under Convert: To PDF (one page per image in name order, each the size of its picture; an upright 8-bit JPEG is embedded as is, anything else decoded by the image pipeline and stored Flate-compressed RGB).
4. **Limits:** a PDF is read whole, at most 512 MB, must start with `%PDF-` in its first kilobyte, and at most 500 pages are rendered or read; at most 200 images make one PDF. A PDF that needs a password is refused.
5. **Results:** a temporary file or hidden folder in the destination, checked (each picture decoded again, the new PDF opened by PDFium with the right page count), marked with the source's zone, then moved to the first free name without overwriting. A failure leaves nothing. The host believes a result only from the disk (`PdfRenderJobModule`).
6. **The PDF ring:** rotations move into Organize ("Rotate right/left/180°"), so Convert gains the new commands without another sector.

## Alternatives considered
- **qpdf alone:** cannot render or extract text.
- **Ghostscript / MuPDF:** AGPL; not allowed in the shipped product.
- **Windows.Data.Pdf (WinRT):** renders pages but has no text extraction and needs a WinRT projection in the worker.
- **A .NET PDF library (PdfPig, Apache-2.0)** for text: possible, but a second parser of hostile input when PDFium already reads text.

## Consequences
- About 6 MB more in the payload; the installer checks `pdfium\pdfium.dll` is present.
- PDFium is a large parser of hostile input with regular security fixes upstream: it runs only in the disposable worker, with the job's memory cap and time limit, and is updated by changing the pinned release.
- Images → PDF does not embed ICC profiles (colours are those the image pipeline decodes to).
- Text extraction is what the PDF contains: no OCR, no layout reconstruction; Markdown output is the text, not a document structure.

## Evidence / experiments
- `PdfRenderTests` (Worker.Tests): rendering, text, Markdown escaping, Images → PDF round trip, password-protected and malformed PDFs refused, limits, no overwrite, host believability.
