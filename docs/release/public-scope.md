# Public release scope

Owner decision, 2026-11-11: the public app is a converter for **Images**, **PDF & Office** and **Video**. It is not a
file manager, an archive manager, a subtitle editor or a media editor. Nothing was deleted. Every command stays in the
code and in `CapabilityResolver.For`, ready for a later version. The wheel, Home and Features use
`CapabilityResolver.Public`, which offers only the allowlist in `src/WinTools.Core/Capabilities/PublicRelease.cs`.

The host runs only a command that the wheel offered for the dropped files (`InteractionCoordinator`, the `offered`
check). So no other menu can reach a hidden command.

Audited on `main` at `fc04b3a`, which includes PR #51 (PDF to Excel, already merged) and PR #52 (the Arabic features
document). One PR is open: #49, a draft for watermark, page numbers and crop margins. It is not merged and not part of
this release.

## 1. Inventory of the code (123 commands)

These counts come from the catalog itself, with every external program present, not from the progress notes.

| Family | Commands | State |
|---|---|---|
| Images | 35 | Implemented and tested: convert (6 outputs), compress (4), resize (6 presets and the size set in Settings), rotate and flip (5), crop (4), adjustments (7), metadata (2), collage |
| PDF (qpdf) | 12 | Implemented and tested: merge, split, rotate (3), edit pages, optimize, linearize, repair, info, protect, unlock |
| PDF (PDFium) | 7 | Implemented and tested: to PNG, JPG, TXT, MD, PPTX, XLSX, and images to PDF |
| Office | 3 | Need an external program: Office to PDF and PDF to Word (Microsoft Office or LibreOffice); HTML to PDF (Microsoft Edge) |
| Documents | 6 | Implemented and tested: text, info, clean, CSV to XLSX, XLSX to CSV, MD to HTML |
| Video | 17 | Implemented and tested: convert (7), compress (3, one of them new here), trim, split, join, speed (2), mute, crop (3), frames, info, extract audio |
| Audio | 16 | Implemented and tested: convert (8), compress, trim, split, normalize, mono, stereo, info |
| Subtitles | 10 | Implemented and tested |
| Files | 10 | Implemented and tested: info, SHA-256, copy path, name and folder, move, copy, rename, show in folder, terminal |
| Archives | 5 | Implemented and tested: extract, test, gunzip, info, add to ZIP |

Implemented, but with quality limits measured below: PDF to Word (Arabic), PDF to PowerPoint (pictures only), and PDF to
Excel (tables without borders, number and date types).

Not implemented: OCR, redaction, editing PDF text, comparing PDFs, and PDF signing. Signing is a work-in-progress branch,
`feat/pdf-sign`, with no PR.

## 2. Public allowlist

| Family | Commands |
|---|---|
| Images | To JPG, PNG, WebP, BMP, TIFF (HEIC and AVIF are accepted as input). Compress: high quality, balanced, smallest, lossless. Resize: 25%, 50%, 75%, 1920×1080, 1280×720, and the exact size set in Settings. Images to PDF. |
| PDF & Office | Word, PowerPoint, Excel (and RTF, ODF, TXT, CSV) to PDF. PDF to Word. PDF to PowerPoint, labelled "PPTX (images)". PDF to Excel. PDF to JPG and PNG. Images to PDF. Merge. Split. Make smaller (lossless). Rotate right and left. |
| Video | To MP4, WebM, MKV, MOV (AVI, WMV and GIF are accepted as input). Compress: smaller file (720p, about 35% of the bit rate), balanced (50%), higher quality (70%). |

The video presets reuse the existing compress path; only the bit-rate share differs. Measured on an 8-second 1080p test
clip of 8.57 MB:

| Preset | Output size |
|---|---|
| Smaller file | 2.70 MB |
| Balanced | 4.53 MB |
| Higher quality | 6.31 MB |

## 3. Hidden from this release

| Hidden | Why |
|---|---|
| Archives: ZIP, extract (ZIP, TAR, 7z, RAR), gunzip, test, info | Not part of a converter |
| Subtitles, all 10 commands | Out of scope |
| Files: copy path, name and folder, move, copy, rename and batch rename, show in folder, terminal, info, SHA-256 | Not part of a converter |
| Image rotate, flip, crop, adjustments, collage, metadata, to AVIF | Editing, not converting. AVIF stays as an input. |
| Video trim, split, join, speed, mute, crop, frames, extract audio, info, to GIF, to AVI, to WMV | Editing. AVI and WMV outputs were dropped to keep the ring readable; both stay as inputs. |
| All audio commands | Audio is not one of the three families |
| PDF repair, linearize, protect, unlock, info, edit pages, rotate 180°, to TXT, to MD | Advanced or utility tools |
| Markdown to HTML, HTML to PDF, CSV and Excel conversions, document text, info and clean | Utility conversions |
| PR #49 (watermark, page numbers, crop margins) | Stays a draft and is not merged |

## 4. Compatibility matrix

| Conversion | Engine | On a clean Windows PC |
|---|---|---|
| Image convert, compress, resize | WIC, libwebp, libheif (bundled) | Works |
| Images to PDF, PDF to JPG and PNG | PDFium (bundled) | Works |
| PDF merge, split, rotate, make smaller | qpdf (bundled) | Works |
| PDF to PowerPoint | PDFium (bundled), plus our own PPTX writer | Works. The slides are pictures. |
| PDF to Excel | PDFium (bundled), plus our own table reader and XLSX writer | Works on PDFs that have text. Scans are refused. |
| Word, PowerPoint, Excel to PDF | Microsoft Office (COM), else LibreOffice | **Needs Office or LibreOffice.** Without one, these commands are not on the wheel and Features shows them dimmed. |
| PDF to Word | Microsoft Word, else LibreOffice | **Needs Word or LibreOffice.** LibreOffice's PDF import makes text boxes, not flowing text. |
| Video convert and compress | FFmpeg, LGPL build (bundled), with H.264 through Windows Media Foundation, or OpenH264 on Windows N | Works |

**Recommended distribution.** Ship the bundled engines as they are now, and do not bundle or download Office software.
Before download, the README and the release notes say plainly that the four Office conversions need Microsoft Office or
the free LibreOffice. After install, the Features page says the same, and it names what is missing on that PC.

Bundling LibreOffice would add about 350 MB and a second update channel. It is the only route to Office conversions on a
PC without Office; if the owner wants that, it needs its own ADR.

## 5. Quality findings: PDF to Word, PowerPoint and Excel

The test documents were generated for this review and are not anyone's personal files:

- a Word document that is English only, one that is Arabic only, and one that is mixed. Each has headings, a bordered
  table, a picture, two pages and a list;
- a workbook with a merged title, a bordered table (Arabic, dates, negative and grouped numbers, a SUM row), a table
  without borders, and a 70-row list that runs onto a second page;
- a two-slide PowerPoint deck with Arabic text and a picture.

The PDFs were made with WinTools' own Office to PDF, through Microsoft Office 16 on this PC.

**Word to PDF, PowerPoint to PDF and Excel to PDF** (Microsoft Office): correct in every case. Office does the layout.

**PDF to Word** (Microsoft Word's PDF reflow). Each result was converted back to PDF and compared page by page with the
original.

- English: the layout, table, picture and page break all match. Heading styles become plain formatted text, and a list
  becomes text with "•" characters. The result is editable.
- Arabic: the layout matches, but Word **corrupts some text**. It splits words ("الربعي" became "الربع ي", and "الرياض"
  became "الريا ض"). It also **turns a number around in an Arabic sentence: "12.5%" became "5.12%"**. WinTools cannot
  correct Word's output.
- Verdict: reliable for English and other left-to-right text. For Arabic, the user must check the result. The Features
  page says the layout may move; whether to add a stronger Arabic warning, or to hold the command back for Arabic, is the
  first blocker below.

**PDF to PowerPoint.** Each page becomes one slide holding a picture of the page. The slides look identical to the PDF,
but their text cannot be edited. The command is labelled "PPTX (images)" on the wheel, and Features says so. A truly
editable conversion would need a mature licensed layout engine; none is bundled, and we will not write one.

**PDF to Excel** (WinTools' own reader). Two defects were found and fixed in this change, each with a regression test:

1. Arabic words in a mixed page were reversed: "أحمد علي" came out as "علي أحمد", and an Arabic sentence came out word
   by word backwards. The direction came from the page. Each cell and line is now read in its own direction.
2. On a page with one bordered table, every line outside that table collapsed into a single cell. Those lines are now
   cut into cells by their gaps.

After the fixes, on the test workbook:

- Bordered table: every value is in its right row and column, in English and Arabic, and the SUM row is a number.
- Table without borders: the columns and the Arabic are right. One header cell joined its neighbour ("الاسم Hours"),
  because the gap between them is narrow.
- Second page: the 70-row list continues with nothing lost.

Known limits, listed in Features:

- Numbers with thousands separators ("1,250.50") and dates are written as text, not as numbers and dates, so the values
  are kept but cannot be summed until they are retyped.
- Merged cells are not rebuilt.
- In a table without borders, two cells close together can merge.
- Scans are refused ("no text"). There is no OCR.

## 6. What changed in the code

- `PublicRelease` (the allowlist) and `CapabilityResolver.Public`.
- Host: the wheel uses `Public`.
- Settings app: Home shows the three families, each with one line. Features lists only the public commands, under
  those families. A command that needs a missing program is dimmed and says what it needs. The PDF notes are shown
  (Word, PPTX pictures, Excel tables, lossless compression).
- How to use: the text no longer mentions subtitles, ZIP or the terminal.
- New video preset "Higher quality" (`media.compress-high`).
- The other two video presets were relabelled "Smaller file" and "Balanced".
- The PDF "Organize" category was renamed "Pages".
- The categories' ring with two or three categories places its labels 4 px further from the hub, so that "Compress" and
  "الصفحات" clear it. `UiSurfaceTests` now renders the public wheel at 4 DPIs, in English and in Arabic.
- PDF to Excel: reading direction per cell, and cells for lines outside a ruled table.

## 7. Remaining blockers

1. **PDF to Word with Arabic** changes some text (split words, and one reversed number) through Word's engine. The owner
   decides: ship it with a warning, or hold PDF to Word back for Arabic PDFs.
2. **The installer is unsigned**, so SmartScreen warns on first run. It needs a code-signing certificate, which only the
   owner can obtain.
3. **No run on a clean PC yet.** Install, first launch and uninstall have not been tried on a Windows PC without Office
   and without the developer tools.
4. **The Explorer drag has not been run by hand on this build.** The wheel is covered by render tests, but no real drag
   from Explorer has been made on the new rings.
