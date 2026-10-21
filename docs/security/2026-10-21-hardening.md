# V1 hardening (TASK 5, 2026-10-21)

The roadmap's hardening task covered six things: a security review of hostile input, lower image limits and a memory cap, a real-world corpus, performance and leak runs, multi-monitor, and a final evaluator. This file records the review and the corpus. Limits: ADR-0015 and `docs/performance/2026-10-21-worker-memory.md`. Leaks and UI stalls: `docs/performance/2026-10-21-soak.md`.

## Security review
An independent reviewer read the code and ran the Release worker against files it crafted (in a scratch folder, never personal files). Its brief covered:
- malformed input in every format, mislabelled files, alternate data streams, reparse points and hard links;
- races between check and write, decompression bombs, memory and CPU exhaustion through the pool;
- the host–worker pipe, path forms (long, Unicode, UNC, device names), settings tampering, log privacy and privilege.

Verdict: no blocker. Nothing it tried modified or destroyed an original, overwrote an existing file, or left a temporary file behind.

| Finding | Severity | Outcome |
|---|---|---|
| M1: no worker memory cap; four parallel workers on a tiny 100 MP PNG could commit about 5.5 GB | major | Fixed (ADR-0015): 50 MP limit, 1.5 GB per-worker cap, workers bounded by available commit. `ResourceLimitTests` |
| M2: a 1.7 MB TIFF whose strip table repeats one Deflate blob 200,000 times kept a worker busy for the full 60 s timeout | major | Fixed: the strip or tile table must have exactly the entries the image needs, so the check's work is bounded by the image's own size. `Tiff_with_more_strips_than_the_image_needs_is_refused_at_once` (fails without the fix) |
| m1: an EXIF IFD0 offset near `int.MaxValue` wrapped past a bounds check; Image info and Remove metadata failed as `Internal` | minor | Fixed: the check is done in 64-bit arithmetic. `Exif_offset_at_the_end_of_the_range_is_handled_not_a_crash` (fails without the fix) |
| m2: a crafted PNG (invalid filter bytes with valid CRCs, palette indices past the palette) or BMP (indices past the palette) still converts | minor | Accepted, recorded in the threat model. Real PNG damage fails its CRC |
| m3: the host read worker lines without a bound, and opened the claimed output before checking its name | minor | Fixed: a bounded line reader, stderr drained into a fixed buffer, and the name checked against the naming policy before any file is opened. `Worker_lines_are_bounded`, `A_claim_outside_the_naming_policy_is_refused_even_with_the_right_hash` |
| m4: "a lane stops after 3 failures" in the docs, while the code allows 4 (the first worker and 3 restarts) | minor | The code matches its constant `MaxRestartsPerLane` and the existing test. The docs now say 4 |
| m5: a kill right after the final rename leaves a valid output reported as a timeout | minor | Accepted (never a false success), recorded |
| m6: the link check and the write both go by path; a same-user process could swap in a junction between them | minor | Accepted, recorded: that process can already write there |
| m7: a folder with the output name might stop the job | minor | Not reproduced. The no-overwrite move reports such a folder as taken and the next name is used. Pinned by `A_folder_with_the_output_name_is_skipped` |
| m8: the settings file was read without a size limit | minor | Fixed: over 64 KB it is not read, moved or overwritten. `An_oversized_settings_file_gives_defaults_and_is_left_untouched` |
| m9: Remove metadata keeps a colour profile byte for byte; its text can name a device | minor | Accepted, recorded next to "not a forensic anonymiser" |

Checked and sound, according to the review:
- **iCCP and zTXt bombs:** WIC bounds its own inflation, and the profile is capped at 16 MB.
- **Clean failures** for truncated, OS/2, PNG-in-BMP and 6-byte files.
- **Collisions:** naming never overwrote anything, and no temp file remained after a timeout kill.
- **Device names** (`CON.png` and the like) open no device.
- **Path policy:** alternate data streams, `\\?\`, `\\.\`, `\??\`, trailing dots, lone surrogates and 8.3 names are refused.
- **Workers** start with no shell and no arguments, at below-normal priority, and die with the host.
- **Logs** hold no file contents.

## Real-world corpus
69 files, each through 6 operations (to PNG, to JPEG, to WebP, resize 50 %, rotate right, image info), 414 jobs in the Release worker. The files:
- 36 photos and images that ship with Windows (`C:\Windows\Web`), copied to a scratch folder and not committed (they are Microsoft's);
- 27 variants written with Pillow;
- 5 JPEGs with typical real-world damage;
- 1 multi-page TIFF.

No personal files were used.

| Group | Result |
|---|---|
| 36 Windows JPEGs | Every applicable job succeeded |
| JPEG: progressive, 4:4:4, grayscale, CMYK, EXIF orientation 6, ICC, restart markers | Every applicable job succeeded |
| PNG: palette, palette with transparency, gray, gray with alpha, RGBA, interlaced, ICC | Every applicable job succeeded |
| PNG 16-bit gray | Converted. Resize and rotate refused with an actionable message (they would reduce it to 8 bits), by design |
| TIFF: uncompressed, LZW, Deflate, JPEG-compressed, CMYK, RGBA | Every applicable job succeeded |
| TIFF with two pages; animated WebP | Refused: `UnsupportedContent` ("multi-page images are not supported yet") |
| BMP 8-bit and 24-bit; WebP lossy, lossless, with alpha | Every applicable job succeeded |
| JPEG truncated (90 %), progressive JPEG truncated (60 %), JPEG missing only its EOI | Refused: `DecodeFailed` "truncated scan" |
| JPEG with data after EOI; JPEG with stray bytes before EOI | Succeeded: the codec reported no damage |

Totals: 326 succeeded, 60 refused as the same format (`ContentMismatch`, by design), 15 refused as damaged, 13 refused as unsupported content. **No crash, no `Internal` error, every source unchanged by hash, no temporary file left behind.**

On JPEGs with codec warnings (an open question since TASK 1): none of the 36 real photos made the codec report damage. So the strict rule (any codec diagnostic means damage) refused no real file in this corpus. Truncation is caught by the structural check before the codec warns.

## Multi-monitor
Not testable here: the development machine has one monitor. This is recorded as a V1 limitation.

The code places and clamps the ring on the monitor under the pointer and uses that monitor's DPI (`RadialOverlay.ShowAt` and `MoveTo`, `RingGeometryTests`, and a DPI change test in `MenuNavigatorTests`). Mixed-DPI layouts and dragging across a monitor boundary stay on the manual protocol (UX-005).
