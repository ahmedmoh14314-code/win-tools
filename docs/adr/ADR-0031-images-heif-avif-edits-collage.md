# ADR-0031: HEIC and AVIF are read through the bundled FFmpeg behind our own structure check; To AVIF, flips, crops, adjustments and Collage join the image module

- **Status:** Proposed (Tangerine parity, 0.2.0 Beta)
- **Date:** 2026-10-28
- **Deciders:** product owner (2026-10-25 direction: Tangerine parity, mature engines)
- **Related:** supersedes the HEIC part of ADR-0012 (SVG stays deferred there); ADR-0028 (FFmpeg), ADR-0006 (image engines), ADR-0015 (limits), ADR-0014 and ADR-0019 (the ring)

## Context
- ADR-0012 deferred HEIC: WIC needs the HEVC extension, a paid Store download, and libheif is LGPL with no other use in the product.
- The condition it set for coming back, "a licence-clean decoder is chosen in an ADR", is now met. The product already ships FFmpeg 8.1.3 (LGPL build, ADR-0028) with its native HEVC decoder and dav1d, and FFmpeg 8's mov demuxer reads HEIF and AVIF stills, tile grids included.
- FFmpeg knows less than a HEIF file can say. It drops alpha planes unless asked, says nothing about HDR transfer functions, and an unknown essential property does not stop it. "A failed job must never look like a success" needs a check of our own.

## Decision
**Input.**
1. `Heif` (Core) reads the boxes itself, bounded: primary image, grid, rotation, mirroring, colour, alpha, Exif and XMP items. It refuses what cannot be shown exactly (the list is in its summary): sequences, clap, layered AV1, unknown essential properties, PQ and HLG, alpha on a grid, premultiplied alpha, item data in another file or outside this one.
2. The bytes already checked go to a private folder under `%TEMP%\WinTools`, and FFmpeg reads that copy, not the original path. There is no gap between what was checked and what is decoded. The folder is removed afterwards; a killed worker's folder is swept by a later run after an hour.
3. FFmpeg writes a lossless 8-bit RGBA PNG, which joins the existing pipeline. The size is held against the size read in step 1, so a thumbnail or a gain map can never pass as the picture.
4. Orientation:
   - A single picture is decoded with `-noautorotate` and turned by the pipeline, exactly like a JPEG.
   - A grid is turned by FFmpeg, which cannot be told not to.
   - A test runs all 12 rotation and mirror combinations through both paths and requires the same picture.
   - An Exif orientation in a HEIF file is not applied: irot and imir decide, as the format says and as libheif does. iPhones write both.
5. The colour profile is taken from the `colr` box (FFmpeg does not carry it to the PNG).

**Output.**
- HEIC cannot be written: no HEVC encoder ships. An edit of a HEIC writes a JPEG (`ImageFormats.EditedAs`).
- To AVIF runs libaom (still picture, 4:4:4, CRF 20, full range, BT.709 signalled), with an alpha plane when the image has transparency. The ICC profile is added to the file by `HeifEdit`, because the muxer drops it. The result is decoded and compared with its source before it can be written.
- Edits of an AVIF stay AVIF, re-encoded, and refuse 10- and 12-bit sources as other formats refuse 16-bit; converting stays possible.

**Not offered on HEIC and AVIF.**
- Compress: a HEIC re-coded as JPEG is almost never smaller, and the AVIF gates of ADR-0017 are not calibrated (a search would run an engine per step).
- Remove metadata: it never re-encodes, and rewriting HEIF boxes is a separate piece of work.

**Edits.**
- The rotations, two flips, four centred crops (1:1, 4:3, 16:9, 9:16) and seven one-click presets are one Edit category, in Rotate's old place. Its ring shows the rotations and flips; the crops and presets are under "More…".
- A top ring holds six groups: seven are within `RingCapacity`, but "Compress" no longer fits (48 px at 96 DPI, any sector tried, `UiSurfaceTests`), and video's ring is already limited to six for this reason.
- Pixels stay in the source's colour space and the profile is kept, as resize and rotate do. Tone changes leave alpha alone; blur works premultiplied.

**Collage.** 2–16 images under Tools (the whole selection, name order), in a grid as square as possible, on white, at most 6000 px on the longer side.
- JPEG when every input is one, otherwise PNG.
- Images are reduced as soon as they are decoded, so only one full image is in memory.
- A profile shared by all inputs is kept. Different profiles give sRGB and a notice in the toast: pixels are not converted, because the product has no colour management.

**Time.** The image worker's request limit grows from 60 s to 120 s; each engine run has 40 s.

## Alternatives considered
- **libheif + libde265:** a second LGPL engine for what FFmpeg already does.
- **WIC per machine:** rejected in ADR-0012, and nothing changed.
- **Decoding in the media worker:** it has no pixel pipeline, and moving one there would split the image module.
- **Reading the original path with FFmpeg:** rejected for the gap between check and decode.
- **Raw BGRA instead of PNG:** faster, but needs the grid's size from FFmpeg before the run; a PNG states its own size, and the existing checks apply to it.
- **Separate Crop and Adjust categories, or Edit beside Rotate:** every preset nearer, but seven or eight top groups, whose labels do not fit (tried and measured by the label test).
- **SVT-AV1:** about as fast, but it clips presets at 4K and aims at video; libaom's still-picture mode gave the smaller file.

## Consequences
- The image worker needs the `ffmpeg` folder. Both workers declare the same files; a publish carries them once.
- A copy of the picture exists briefly in the user's temporary folder.
- Displays: pixels with nclx primaries other than BT.709 and no ICC profile are shown as sRGB, slightly less saturated.
- HDR photos in PQ or HLG are refused rather than shown washed out. An iPhone's SDR photo with a gain map is read as its SDR picture; the gain map is not carried to the output.
- A grid with alpha is refused. Phones do not write them; web AVIFs with alpha are single pictures.
- The menu golden file and the tests that pin the ring change, deliberately. The label "Rotate" becomes "Edit" on images, and crops and presets take a pick from the "More…" list.

## Evidence / experiments
Test files are made on the spot: FFmpeg encodes tiles (HEVC with kvazaar, AV1 with libaom) and `HeifBuilder` in the tests packs them like a camera: 512 px tiles, the grid descriptor in `idat`, irot 3 with Exif orientation 6, thumbnail, gain map, ICC profile.

| Check | Result |
|---|---|
| 4032 × 3024 from 48 tiles, against the reference | Upright, 42.4 dB PSNR, 0.5 s in FFmpeg alone |
| The same file through the job (`Phone_size_heic_and_avif`) | To JPEG 2.5 s, 50 % resize 1.7 s, To AVIF 7.7 s, from a 455 KB HEIC to a 1,037 KB JPEG or a 221 KB AVIF |
| 3840 × 2400 wallpaper, 4:4:4 CRF 18 AVIF against FFmpeg JPEG `-q:v 2` | 134 KB at 45.8 dB against 709 KB at 46.5 dB; source JPEG 1.6 MB |

One real file from outside the product was checked by hand: `image1.heic` of github.com/tigranbs/test-heic-images (2.9 MB, 3992 x 2992, 48 HEVC tiles, ICC profile, Exif item, no rotation). To PNG took 4.3 s and the picture was complete, with no seams, looking at it. It is not committed because its licence is unknown, and whether it came straight from an iPhone camera is not known: a rotated camera original has only been tested on the built files above.

## Evaluator review
Pending: the pull request's review.
