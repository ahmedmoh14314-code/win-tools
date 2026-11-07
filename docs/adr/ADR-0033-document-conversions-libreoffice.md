# ADR-0033: Tables and Markdown convert in house; Office to PDF runs the user's own LibreOffice, offered only when it is found

- **Status:** Proposed (0.2.0 Beta)
- **Date:** 2026-11-07
- **Deciders:** product owner (document conversions brief)
- **Related requirements:** V2 plan §9 Documents, plan D1 (LibreOffice as an optional, separately approved converter), §14 document threats
- **Amends:** ADR-0025 ("Consequences": TXT/Markdown/CSV to PDF was left to a later slice)

## Context
- Users expect CSV to Excel, Excel to CSV, Markdown to HTML, and Office documents to PDF.
- Tables and Markdown are simple enough to convert safely in the text worker.
- Faithful Office layout is not: it needs a full office suite. WinTools cannot bundle one, and Pandoc is GPL.

## Decision
1. **New file kinds:** CSV, TSV, Markdown, text, DOC, RTF, ODT, XLS, ODS, PPT, ODP (`FileKinds.Documents`). General Files commands still apply to them.
2. **To Excel (CSV, TSV) needs no library.** The workbook is written with `ZipArchive` and `XmlWriter`, and read back cell for cell before it is kept.
   - Encoding: BOM, strict UTF-8 or unmistakable Arabic Windows-1256 (the subtitles decoder). Anything else is refused, not guessed.
   - Separator: Excel's `sep=` line, else the most frequent of comma, semicolon and tab in the first record. TSV is always a tab.
   - **A cell is a number only if it is plainly one:** optional minus, no leading zero, optional decimals, at most 15 significant digits. `007`, long IDs, `1E5`, `1,000` and dates stay text, as written.
   - Everything else is inline text, so `=SUM(...)` in a CSV is never a formula.
   - Excel's limits are refused, never cut: 1,048,576 rows, 16,384 columns, 32,767 characters a cell.
   - A line break in a cell becomes LF; a character XML cannot hold becomes U+FFFD, and the toast says so.
   - A sheet whose text is mostly right to left opens right to left.
3. **To CSV (XLSX): every sheet with data, one CSV each.**
   - One such sheet gives `book.csv`; several give a folder `book (CSV)` with one file per sheet, named after the sheet (made safe, unique). Writing only the first sheet would lose data without a word.
   - Sheets come in the workbook's order, through its relationships, on ADR-0025's package rules. Shared strings and styles are found through the relationships too, in any case.
   - **Damage is not a blank:** a cell pointing at text the workbook does not hold, a listed sheet the package lacks, or a named part that is missing fails the job.
   - Hidden sheets are exported, and the toast says so.
   - UTF-8 with a BOM, comma, CRLF, RFC 4180 quoting, every row as wide as the widest.
   - Values, not display: `0.25`, not `25%`. Dates as ISO 8601, a formula as its cached value. Formulas are never evaluated.
   - **CSV injection:** a text cell starting with `=`, `+`, `-`, `@`, tab or CR gets a leading apostrophe (OWASP), and the toast says so. This changes such a cell's text; running it when the CSV is opened would be worse.
   - Fields holding a semicolon or tab are quoted too, so Excel with another list separator cannot split a formula out of one.
   - A workbook spreading past 50 million cells, gaps included, or 200 million characters of CSV, is a limit, reached before anything is written.
4. **To HTML (Markdown) uses Markdig 1.4.0** (BSD-2-Clause, no dependencies) in the text worker.
   - `DisableHtml`: raw HTML becomes visible text.
   - A fixed list of extensions: tables, task lists, footnotes, strikethrough, autolinks, heading ids. Not `UseAdvancedExtensions`, which includes generic attributes (`{onclick=...}`).
   - Every address passes `LinkRewriter`: http, https, mailto, in-page and relative stay; `javascript:`, `data:`, `file:` and the rest become `#`.
   - The page carries a Content-Security-Policy allowing no script, frame, form or outside style. Images from the web load when the user opens the page, as the Markdown asked.
   - The worker scans the page before keeping it: no active element, no event handler, no style but the two Markdig writes (a cell's alignment, a grid column's width). The host checks the policy is there and there is no `<script`.
   - Each block takes its direction from its own text (`unicode-bidi: plaintext`), so Arabic and English paragraphs sit together correctly.
   - Nesting past Markdig's limit is refused as a limit. The 20,000-level test found this crashing the worker first.
5. **TXT and Markdown to each other are left out.** TXT to Markdown is a rename, and Markdown to TXT loses structure for little use.
6. **To PDF runs the user's installed LibreOffice,** in a module of its own (`office`, id `office.to-pdf`), from the text worker.
   - **LibreOffice is MPL-2.0 and is not bundled.** It is the user's own program: WinTools never ships, installs or downloads it, and runs it as a separate process. No LibreOffice code is in the product, so no licence obligation of it applies to WinTools.
   - **Pandoc is GPL: never bundled and never called.**
   - **Found** in this order: App Paths (user, machine; 64- and 32-bit views), `SOFTWARE\LibreOffice\UNO\InstallPath`, the default Program Files folders. A candidate counts only as a local, fully qualified path named `soffice.exe` with `soffice.bin` beside it. No disk search. The worker looks again itself; a request never carries a program path.
   - **Checked first, as its kind:** OOXML on ADR-0025's rules (limits, content types); ODF by the same limits and its `mimetype` entry; DOC, XLS, PPT and RTF by signature; text must decode. An encrypted OOXML file is said to be one.
   - **Opened with its kind's own filter** (`--infilter`, node names read from `filter/source/config/fragments/filters`), so LibreOffice's content detection picks no importer we did not mean to expose. A file only the older importers read (Word 95, Excel 95) fails rather than being guessed.
   - **Run on a copy** under a fixed name in a private temp folder, so no user text reaches the command line and no lock file appears next to the original.
   - **Text** is decoded by the worker and rewritten as UTF-8.
   - **A CSV becomes a workbook first, on To Excel's rules** (decision 2), and LibreOffice prints that. `007` stays text, and `=WEBSERVICE(...)` is text, so it fetches nothing. This trusts no CSV filter option.
   - **A throw-away profile** (`-env:UserInstallation`), so the user's own LibreOffice is untouched and an open LibreOffice window does not take the job. Its `registrymodifications.xcu` sets, from LibreOffice's schema (`officecfg`):

     | Key | Value | Effect |
     |---|---|---|
     | `Common/Security/Scripting/MacroSecurityLevel` | 3 | Highest level |
     | `.../DisableMacrosExecution` | true | Basic, Python, JavaScript and BeanShell off; the level is ignored |
     | `.../BlockUntrustedRefererLinks` | true | No linked content from a document outside trusted locations |
     | `.../DisableActiveContent` | true | No OLE embedded content and no DDE |
     | `.../DisableOLEAutomation` | true | No OLE Automation interface |
     | `Writer/Content/Update/Link` | 2 | Never (Writer's numbering) |
     | `Calc/Content/Update/Link` | 1 | Never (Calc's numbering) |
     | `Calc/Formula/Load/OOXMLRecalcMode`, `ODFRecalcMode` | 1 | Never recalculate on load |
     | `Inet/Settings` proxies | manual, 127.0.0.1:9 | A closed port for anything that still tries |
     | Update check job, `AutoCheckEnabled` | false | No update check |

   - **Environment:** only what a Windows program needs, TEMP in the work folder, and the proxy variables libcurl reads pointing at the same closed port.
   - **Time:** 3 minutes in the worker, then the process tree is killed; 5 minutes in the host. The worker is in the host's kill-on-close job, so LibreOffice dies with it, and the 1.5 GB per-process cap applies.
   - **Leftovers:** the host retries the work folder after a kill, and sweeps `wintools-office-*` when it starts, because each holds a copy of a user's document.
   - **One at a time:** the module's own queue, with one worker.
   - **Validated:** LibreOffice must exit 0. The PDF must be the only file written, start `%PDF-`, pass `qpdf --check` with exit 0 and have at least one page, then goes through the module's temp, zone and no-overwrite steps. Exit 3 is refused: qpdf reads a cut PDF that way, with pages lost. The host checks name, header, hash and page count.
7. **When LibreOffice is not found, To PDF is not on the wheel.**
   - `CapabilityDescriptor.Requires` names a program the user installs; `CapabilityResolver.For(id, installed)` leaves such a command out unless the set names it. The default is none, so every existing menu is unchanged.
   - The host looks at start and on a settings change (a few registry reads), never on the trigger path.
   - The Features page lists To PDF when found; otherwise it says "Office to PDF needs LibreOffice (free), which was not found on this PC".
   - A job that still arrives after LibreOffice was removed fails with that plain reason.
   - This is one optional field, not a plugin system.

## Alternatives considered
- **DocumentFormat.OpenXml** (MIT): reconsidered for writing and reading sheets, as ADR-0025 foresaw. A one-sheet workbook of text and numbers needs a handful of parts, and the read-back proves them. It would add a large dependency without changing the result.
- **Only the first sheet in To CSV:** simple, and loses data silently.
- **Showing To PDF greyed out or failing on use:** a button that cannot work. The radial menu has no disabled state, and the Features page is the right place to explain.
- **Finding LibreOffice in the worker only:** the wheel would then offer a command it cannot run.
- **Passing the path from host to worker:** a job request would carry a program to run.
- **Microsoft Office automation:** COM into the user's Office, with macros and prompts; not headless by design.
- **Bundling LibreOffice:** several hundred MB. Rejected by the brief.

## Consequences
- Excel to CSV does not show what the user sees in Excel (percent, currency, rounding): it writes values. Numbers carry 15 significant digits, as Excel does.
- `12.50` in a CSV becomes the number 12.5, displayed by Excel without the zero.
- `.txt` is now a kind of its own; the test that named it as an unknown file uses `.log` instead.
- LibreOffice's parsers are a large attack surface we neither ship nor update. The user's version decides; the ADR's settings limit what a document can reach, not parser bugs.
- `DisableActiveContent` and `BlockUntrustedRefererLinks` exist only in recent LibreOffice versions; an older one ignores unknown keys. No minimum version is enforced.
- **The "no network" claim is defence in depth, not proof.** Public advisories on a LibreOffice wrapper describe fetches that bypass a proxy, which is why the closed proxy is not the main control.
  - The proxy covers HTTP and HTTPS only. An SMB link (`\\host\x.png`) is outside it, and whether every format's external image takes the path `BlockUntrustedRefererLinks` guards was not traced.
  - It has not been measured with a process monitor. Binary `.xls` has no recalculation key.

## Open questions
- **The end-to-end run with real LibreOffice has not been done.** It is not installed on the build machine and must not be installed by the work. Read from its sources, but unproven until the owner runs it:
  - that `soffice.exe` waits for `soffice.bin` (its loader does, with no time limit);
  - that a successful run exits 0, since any other code now fails the job;
  - each `--infilter` name against a real document of each kind, the macro-enabled variants included;
  - fresh-profile start time against the 3-minute limit;
  - that a password-protected document fails rather than waits (convert loads are silent, with no interaction handler).
- **A canary for the network claim:** a `\\host\x.png` and an `http://` image linked from a docx, odt and pptx, under a process or network monitor.
- **No minimum LibreOffice version is enforced,** so an old one ignores the keys it lacks. If the canary shows a gap, a version check belongs here.

## Evaluator review
One round (2026-11-07), with the reviewer's own runs of Markdig and qpdf, and LibreOffice's sources. Verdict before fixes: do not merge. Each fix below is a test.
- **Blocker, fixed:** To CSV read shared strings at one exact name and turned a bad index into an empty cell, so a valid package at `xl/SharedStrings.xml` lost all its text and still succeeded.
- **Majors, fixed:**
  - a table with column alignment, and every grid table, always failed the page check;
  - To Excel's read-back used the limits for a stranger's parts, so about 65,000 rows of text failed validation;
  - qpdf's exit 3 was accepted, and a cut PDF was caught only by accident; LibreOffice's exit code was never read;
  - CSV to PDF would have lost `007` and long IDs (now through a workbook);
  - any ZIP or compound file under an office name reached LibreOffice's content detection (now the kind's checks and filter);
  - a semicolon field could split a formula out under another list separator;
  - a killed job could leave a copy of a document in the temp folder.
- **Minors, fixed:** table bombs as a limit; archive limits for To PDF; the legacy code page notice in To PDF; wrong reasons (LibreOffice present but not starting, a full temp disk); format 46 and `[Magenta]`; exact long integers; hidden sheets said; `$` in the temp path; tests that proved less than their names.
- **Found by the reviewer to hold:** the 16 profile keys and their value meanings, argument and path handling, the environment allow-list, the output steps and the host's checks.
- **Not done:** a focused re-review (the cadence is one round; re-review only on a blocker, so one is due before merge).

## Evidence / experiments
- Key names and value meanings read from LibreOffice's sources (2026-10-28): `officecfg` `Common.xcs`, `Writer.xcs`, `Calc.xcs`, `Inet.xcs`. Filter names from `filter/source/config/fragments/filters` (2026-11-07). These are `master`, not a pinned release.
- Tests: `DocumentConvertTests`, `OfficeConvertTests` (a stand-in plays LibreOffice; qpdf, naming and host checks are real), `DocumentConversionTests`.
