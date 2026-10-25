# ADR-0024: Subtitles (SRT and WebVTT) are parsed in house, in the text worker, and malformed timing is reported, not guessed

- **Status:** Proposed (V2 PR 7.1)
- **Date:** 2026-10-25
- **Deciders:** product owner (V2 plan §9 Subtitles, §7 "in-house parsers")
- **Related requirements:** V2 plan §9, §10 PR 7.1, §11 corpus (UTF-8, UTF-16, 1256, 1252, BOM, CRLF/LF, overlaps, Arabic, emoji)

## Context
- Subtitle files are small text files with sloppy, mixed encodings.
- Arabic subtitles are very often Windows-1256 from old editors.
- The maintained .NET parsers are LGPL-3 (SubtitlesParserV2) or unmaintained (V2 plan §7), so the parsers are ours.

## Decision
1. **Formats:** SRT and WebVTT, in Core (`WinTools.Core.Subtitles`, pure and bounded), run by the existing text worker, which also serves General Files. ASS/SSA is a later, optional PR.
2. **Commands:**
   - Convert: To VTT, To SRT.
   - Timing: Earlier 1 s, Earlier 0.5 s, Later 0.5 s, Later 1 s. A custom offset needs the dialog process (Stage 7).
   - Tools: Subtitle info; Clean up (UTF-8), which rewrites the same format as UTF-8 with uniform line endings.
   - A mixed SRT and VTT selection gets the timing and the tools, not the conversions.
3. **Encoding, decided in this order:**
   1. a BOM (UTF-8, UTF-16 LE, UTF-16 BE);
   2. strict UTF-8;
   3. Windows-1256 if most bytes above 0x7F sit next to another such byte (words in Arabic script), otherwise Windows-1252. The code-page provider is part of .NET.
4. **Output encoding and line endings:**
   - SRT is written as UTF-8 **with** a BOM and CRLF (Windows players need the BOM to show Arabic).
   - WebVTT is written as UTF-8 without a BOM and LF, as its specification says.
5. **Parsing:**
   - A cue is a timing line and its text up to a blank line.
   - A timing that does not match `[hh:]mm:ss,mmm` (WebVTT: `.`), a minute or second over 59, or an end before its start is reported with its line number.
   - The file is then not converted or shifted. The job fails with the first problem, and Info counts them.
   - WebVTT NOTE, STYLE and REGION blocks are skipped, and so are cue settings. Voice and class tags are removed, keeping `<i>`, `<b>` and `<u>`.
   - Bounds: 20 MB per file, 100,000 cues, 4,096 characters per line, and regexes with a 100 ms timeout. A hostile file cannot make it loop or grow without limit.
6. **Timing:**
   - A shift that would put a time before zero clamps it at zero.
   - Overlapping cues are counted for Info, never "fixed".
7. **Outputs follow ADR-0005:**
   - an exclusive temporary file, read back and parsed again to the same number of cues;
   - marked with the source's zone;
   - moved to the first free name (`film.vtt`, `film (1 s later).srt`, `film (UTF-8).srt`).
   - Clean up on a file that already is exactly its clean form writes nothing ("Already clean UTF-8").
   - The host believes a file only under an allowed name, with the validated SHA-256, and when it parses without a problem.

## Evaluator review
One round (2026-10-25), reproduced on the built code: three blockers and five majors, fixed with `SubtitleReviewTests` (written first, failing).
- **Blockers:**
  - **Encoding was guessed.** Cyrillic, Greek, Portuguese pairs and damaged UTF-8 were decoded wrongly and written out as "Saved as UTF-8". Decoding now succeeds only when it is certain (decision 3 below supersedes the earlier rule) and refuses otherwise, with the reason.
  - **A BOM followed by other bytes** was replaced silently. Decoders are strict now.
  - **A missing blank line** merged two cues. A timing line among a cue's text is now a problem.
- **Majors:**
  - non-ASCII digits crashed the worker (the timing regex now takes `[0-9]` only);
  - long lines were cut (now a problem);
  - a whitespace-only line after `WEBVTT` swallowed the first cue (the header ends at any blank line, and a timing in the header is a problem);
  - WebVTT to WebVTT dropped the header, blocks, ids and settings (now kept verbatim);
  - tags and entities were mishandled both ways (only WebVTT's own tags are removed; entities are decoded to SRT and escaped to WebVTT).
  - What a conversion cannot keep is told in the toast ("Not kept: cue positions, styles and notes").
- **Minors fixed:**
  - error codes;
  - one bounded read;
  - the read-back compares every cue;
  - unknown ids are refused before the file is read;
  - 1,000,000 lines at most;
  - a shift past 999 hours is refused.

**Decision 3, as revised:** a BOM with a strict decoder; strict UTF-8, refusing mostly-valid UTF-8 with damaged bytes; Windows-1256 only when at least 80 % of the decoded non-ASCII characters are Arabic script; Windows-1252 only when at least 95 % are Latin and fewer than half of the high bytes stand in runs of three or more (Cyrillic and Greek put over 90 % there). Anything else is refused: "save it as UTF-8 and try again".

## Alternatives considered
- **SubtitlesParserV2.** LGPL-3, rejected by the licence policy.
- **UTF.Unknown for detection.** MPL. Not needed for the two code pages that matter here.
- **Guessing broken timings** (swapping start and end, wrapping seconds). Rejected: a wrong guess silently desynchronises a film. The user is told which line.
- **UTF-8 without a BOM for SRT.** Rejected: common Windows players then show Arabic as mojibake.

## Consequences
- Other legacy code pages (1250, 1251, 1253, Shift-JIS…) are refused with the reason, not guessed; Info says "Not recognised".
- WebVTT styling is lost on conversion to SRT, which cannot hold it.

## Evidence / experiments
- `SubtitleTests` (Core, 14), written first and shown failing:
  - SRT and VTT parsing and the round trip;
  - VTT ids, settings, notes, styles and voice tags;
  - 4 malformed timings, each reported with its line;
  - shift and clamp;
  - overlaps;
  - 6 encodings (1256, 1252, UTF-8, UTF-8 with BOM, UTF-16 LE and BE);
  - line endings;
  - the cue bound;
  - the menu;
  - names.
- `SubtitleWorkerTests` (8, real files):
  - Arabic 1256 to UTF-8 VTT, believed;
  - Info with the encoding and the problems;
  - a malformed file left unchanged;
  - a VTT shift;
  - clean-up once, then "nothing to do";
  - a taken name kept and the zone carried;
  - forged claims refused;
  - the real text worker running subtitle and SHA-256 jobs.
