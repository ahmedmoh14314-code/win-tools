# FFmpeg 8.1.3 (LGPL build)

WinTools runs FFmpeg and ffprobe as separate programs (in the `ffmpeg` folder next to `WinTools.Worker.Media.exe`) for
its video and audio commands. They are not linked into WinTools.

- **Licence:** GNU Lesser General Public License, version 3 (LGPL-3.0); the text follows as LICENSE.txt. The build was
  configured with `--enable-version3` and without `--enable-gpl` or `--enable-nonfree`.
- **Binaries:** `ffmpeg-n8.1.3-24-g92611900bb-win64-lgpl-shared-8.1.zip` from https://github.com/BtbN/FFmpeg-Builds,
  release `autobuild-2026-10-24-13-02` (SHA-256 0c2e70f180ae48a748acf2308229ae19eafc734515e9876c75cfe36998981c53).
- **Source:** FFmpeg at https://github.com/FFmpeg/FFmpeg (release branch 8.1, commit 92611900bb); the build scripts,
  with every library's version and configuration, at https://github.com/BtbN/FFmpeg-Builds (the release above). On
  request, the WinTools project provides the corresponding source.
- **Libraries in this build** (each under its own licence, compatible with the LGPL build): dav1d, libaom, SVT-AV1,
  libvpx, OpenH264, Opus, Vorbis, LAME, libwebp, zlib, FreeType, HarfBuzz, FriBidi, libass, and others listed by the
  build scripts. Windows' own Media Foundation provides the H.264 and AAC encoders WinTools uses first.
- **Codec patents** (H.264, HEVC, AAC) are a separate matter from the copyright licence. WinTools encodes H.264 and AAC
  through Windows' Media Foundation where Windows offers it.
