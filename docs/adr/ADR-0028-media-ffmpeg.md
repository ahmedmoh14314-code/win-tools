# ADR-0028: Video and audio run the bundled FFmpeg (LGPL build) in a media worker

- **Status:** Proposed (V2 media sprint)
- **Date:** 2026-10-25
- **Deciders:** product owner (2026-10-25 direction: Tangerine parity, media in this Beta, mature engines)
- **Related:** ADR-0022 (qpdf as a child engine), dependency rules (LGPL needs dynamic use and an ADR)

## Context
- WinTools now aims at Tangerine's feature families on Windows, and video and audio are part of this Beta.
- Codecs are not something to write ourselves. FFmpeg is the mature engine, and BtbN's FFmpeg-Builds publishes official-style Windows builds in LGPL and GPL variants, with a checksum list per dated release.
- Rules: no GPL in the shipped product; LGPL only used dynamically and recorded in an ADR; nothing a file names may reach the network.

## Decision
1. **The engine:** FFmpeg 8.1.3, BtbN's `win64-lgpl-shared` build from release `autobuild-2026-10-24-13-02`.
   - The archive's SHA-256 is the release's own, and every shipped file's hash is pinned in `scripts/Get-Engines.ps1`.
   - It was configured with `--enable-version3` and without `--enable-gpl` or `--enable-nonfree`, so it is LGPL-3.0.
   - It ships as separate programs in a `ffmpeg` folder next to the worker, about 160 MB unpacked, without `ffplay`.
   - Notices are in `third-party/ffmpeg`, with the source location.
2. **A new worker, `WinTools.Worker.Media`,** runs `ffmpeg` and `ffprobe` as children, as the PDF worker runs qpdf:
   - an argument list only, with no shell and no command line we assemble;
   - a private environment, no window, and standard input closed;
   - a 55-minute limit, after which the process tree is killed;
   - bounded output.
   - The host gives each request 60 minutes, and runs one media job at a time, since an encode already uses every core.
3. **Nothing reaches the network:**
   - every path goes as `file:` plus the full path, so it is never read as a URL or another protocol;
   - only the `file` protocol is allowed (`-protocol_whitelist file`).
4. **Every input is probed first.** It must be a container on an allowed list: MP4/MOV/M4A, MKV/WebM, AVI, ASF (WMV/WMA), GIF, MP3, WAV, FLAC, OGG, AIFF, AAC.
   - It must have the streams its command needs, and be at most 20 GB and 12 hours long.
   - Playlists, concat lists, image sequences and devices are refused before any work.
   - A still picture stored as a stream (cover art) does not count as video.
5. **Results:** written to a temporary file in the destination, with the muxer given explicitly.
   - The result is probed again (the right streams, a duration) before it takes its first free name, and it never overwrites anything.
   - A failure leaves nothing behind.
   - The Mark-of-the-Web zone of the sources carries over.
   - A "smaller" copy must save 8 %, as image Compress does (ADR-0017).
6. **Encoders:**
   - H.264 and AAC go first through Windows' Media Foundation encoders (`h264_mf`, `aac`), which Windows licenses; OpenH264 is the fallback when Windows lacks them (N editions).
   - VP9 and Opus for WebM, MPEG-4 and MP3 for AVI, WMV2 and WMA for WMV.
   - The audio formats use their usual encoders (LAME, Vorbis, Opus, FLAC, PCM).
7. **What the wheel offers:**
   - **Video:** Convert, Compress (Smaller, 720p), Edit (2× speed, ½ speed, Join), Audio (Extract, Remove), Tools (Save frames, Video info).
   - **Audio:** Convert, Compress, Edit (Normalize volume, To mono, To stereo), Tools (Audio info).
   - Trim, Split and Crop need a dialog and come next.
8. **The host believes:**
   - a file from its allowed name and its SHA-256;
   - frames from the folder's shape;
   - info from bounded facts.

## Alternatives considered
- **The GPL build (x264, x265):** not allowed in the shipped product.
- **Windows Media Foundation alone:** too few formats (no WebM, MKV, FLAC or Opus writing), and uneven between Windows editions.
- **A .NET wrapper library (FFMpegCore, Xabe):** it adds nothing over our own runner, which already enforces the protocol, environment and limits rules.

## Consequences
- The installer grows by roughly 50 to 60 MB compressed.
- H.264, HEVC and AAC are patent-encumbered formats in some countries. The copyright licence is LGPL, and the patent question is separate. We encode through Windows where Windows offers it. This needs the owner's attention before a commercial release.
- No progress percentage yet: the toast shows the job working until it ends.

## Evidence
- `MediaWorkerTests` (25) on the bundled engine, with clips and tones it makes itself:
  - every video and audio conversion, probed afterwards;
  - speed (length halved or doubled);
  - extract and remove audio;
  - mono, stereo and normalize;
  - compress;
  - join (different sizes, name order);
  - frames and info;
  - an HLS playlist, a concat list and plain text named as media, all refused;
  - never overwriting;
  - the real worker process.
- **Explorer runs:** video to WebM 2/2; audio to MP3 2/2; video info 1/1.
