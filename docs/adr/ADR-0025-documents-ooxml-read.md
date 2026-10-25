# ADR-0025: Office documents are read in house, as archives of XML, with no DTD, no resolver and no macro ever run

- **Status:** Proposed (V2 PR 6.1; PR 6.2 adds decision 6)
- **Date:** 2026-10-25
- **Deciders:** product owner (V2 plan §9 Documents, §14 document threats)
- **Related requirements:** V2 plan §9 (DOCX/XLSX/PPTX text and properties), §14 (macros, external links, ZIP and XML bombs)

## Context
- V2 needs text extraction and properties for Word, Excel and PowerPoint files.
- The plan names DocumentFormat.OpenXml, after a spike on DTD and billion-laughs rejection and a ZIP pre-scan.
- For reading text and properties, the parts needed are few and simple: `word/document.xml`, `ppt/slides/slideN.xml`, `xl/sharedStrings.xml` and `xl/worksheets/sheetN.xml`, `docProps/core.xml` and `docProps/app.xml`.

## Decision
1. **No dependency for reading.** .NET's own `ZipArchive` and `XmlReader` read the parts, in the text worker (`documents.*` ids), which is a separate process like the others.
2. **The package is an archive first:**
   - at most 10,000 parts;
   - each part's declared size within the archive ratio rule (ADR-0023), with the total bounded;
   - every part actually read is counted against 50 million characters.
   - It must start with `PK`, and its `[Content_Types].xml` must name the main part of the kind its name says (a deck named `.docx` is refused). The macro-enabled main types are accepted, and their macros are reported, never run.
3. **XML is read with:**
   - `DtdProcessing = Prohibit` (a DTD is an error, so no entity, local or external, ever expands);
   - `XmlResolver = null`;
   - `MaxCharactersFromEntities = 0`.
   - Relationships are never followed.
4. **Text:**
   - Word: paragraphs in body order, runs joined, tabs kept.
   - PowerPoint: slides in their number order (`slide10` after `slide9`), "Slide N", then each paragraph.
   - Excel: sheets in number order, "Sheet N", then each row's cells tab-separated (shared strings, inline strings, numbers, booleans).
   - The output is a new `<name> (text).txt`, UTF-8 with a BOM and CRLF. It goes through an exclusive temp, a read-back, the source's zone, and no overwrite.
5. **Info:**
   - type, title, author, last saved by, modified;
   - pages and words (Word), slides, sheets;
   - notices for macros (`vbaProject.bin`), embedded objects, and links outside the document (a relationship with `TargetMode="External"`): none is opened.
6. **Remove properties (V2 PR 6.2)** writes a new `<name> (no properties).<ext>` in the source's own type:
   - **The property parts are found through the package's relationships** (core, extended and custom properties, transitional or strict) and at their usual `docProps/` names. Each is replaced by its own root element, empty, in its own namespace.
   - **Every other part is copied byte for byte, in the same order.** Each copy's size and CRC-32 must match what the package declares: `ZipArchive` stops at the declared size without a word, so a part that inflates past it would otherwise be silently cut.
   - **The copy is checked before it is kept.** It is opened again under every rule above, with the same parts in the same order, each copied part's SHA-256 equal to the source's, and no property left. Then the usual temp, zone and no-overwrite rules apply.
   - **A signed package (`_xmlsignatures/`) is refused**: the copy would break its signature.
   - A package with no properties is "nothing to do".
   - **What it does not remove is said, as a notice:** comment and revision authors (they live in the content), and custom XML data such as SharePoint columns. Office's Document Inspector removes them.

## Alternatives considered
- **DocumentFormat.OpenXml 3.5.1** (MIT). It is the right tool for full fidelity. For reading these few parts it adds a dependency and its own spike without changing what is read.
  - It was reconsidered for Remove properties (decision 6) and not taken.
  - That command edits no content part: it replaces three whole parts and copies every other one byte for byte, which `ZipArchive` does and the check proves.
  - The SDK would load and save the package, re-serialising parts it does not need to touch.
  - It stays the candidate when a command must edit content (for example removing comment authors).
- **Office or LibreOffice automation.** Out of scope for reading; it is the optional, separately approved converter (plan D1).

## Consequences
- Not extracted in this slice:
  - headers, footers, footnotes, comments and text boxes in Word;
  - speaker notes in PowerPoint;
  - formulas (their cached values are), merged-cell layout, and sheet names (sheets are numbered) in Excel.
- TXT/Markdown/CSV → PDF is a later slice (it needs the rendering backend spike, plan D2).
- Remove properties leaves the names in comments and tracked changes, custom XML data, thumbnails, and the content itself (an author named in the text), and says so. Each part is recompressed, so the copy's size can differ slightly from the original's.

## Evaluator review
One round (2026-10-25), reproduced on the built worker with the reviewer's own probe. The four majors and most minors are fixed; each is a test that failed on the old code.
- **Majors:**
  - one bidi mark (RLM) in a title made the whole Info unbelievable;
  - a paragraph holding a text box lost its own text;
  - external links written with other quoting or case were not reported, and macros were found only by part name;
  - Excel cells lost their columns when cells were skipped, and a rich inline string became several cells.
- **Minors fixed:**
  - `[Content_Types].xml` is parsed, not searched (a type named in a comment is not a document);
  - headings alone are not text;
  - a password-protected (compound) file says so;
  - an unreadable source is not reported as an unwritable destination;
  - parts differing only in case are refused;
  - the whole package's declared size meets the archive ratio rule;
  - clipping never cuts a character in two.
- The Explorer `docx` scenario passed on the final build (runs of 3/3, 2/2 and 1/1).

## Evidence / experiments
`DocumentCleanTests` (9, written first and shown failing to build):
- properties emptied, every other part the same, and the original unchanged;
- Excel and PowerPoint;
- properties at a non-standard name found through the relationships;
- the notice for comment and revision authors;
- a signed package refused;
- no properties, so nothing to do;
- no overwrite;
- a part larger than it declares (refused, nothing kept);
- the menu.

Explorer `docx --op documents.clean` 2/2, with Extract text and Info regressions 1/1 each.

`DocumentWorkerTests` (8 at first, written first and shown failing to build; 18 after the review; 19 with the `app.xml` Office writes, whose vectors made Info fail until PR 6.2's work found it):
- Word paragraphs with Arabic and an entity;
- PowerPoint slide order to 10;
- Excel shared, inline and number cells;
- Info with title, author and the macros notice;
- a DTD with nested entities (refused, nothing written);
- a fake `.docx` and a deck named `.docx` (both refused);
- a 64 MB zero part (refused before parsing);
- the menu.
