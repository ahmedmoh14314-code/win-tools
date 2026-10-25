# Release regression matrix: V2 checkpoint (main `b67ae8f`)

Run 2026-10-25 at 04:33 UTC on the development PC (unattended, idle desktop), with the Release harness next to the script.
Build under test: 1.0.0+b67ae8f3d6b000f62108f2861013f6c9cb49c455, SHA-256 WinTools.Host.dll E5244BB59956, WinTools.Core.dll ADC8FF6A0DD7, WinTools.Worker.dll D7B5DE0B7B49, WinTools.Windows.dll 5F981A46BD4E.

| Row | Proves | Cycles passed | Exit | Keys left down |
|---|---|---|---|---|
| first-use-png | first operation after a fresh host (cycle 1), then warm | 3/3 | 0 | none |
| photo-compress | compress a real-size photo: valid copy, saves >= 8 % | 2/2 | 0 | none |
| photo-compress-high | High on a photo already near its quality: nothing written, says so | 1/1 | 0 | none |
| ctrl-a-2 | 2 files, Ctrl+A, drag at once | 2/2 | 0 | none |
| ctrl-a-10 | 10 files, Ctrl+A, drag at once | 2/2 | 0 | none |
| ctrl-a-100 | 100 files, Ctrl+A, drag at once | 1/1 | 0 | none |
| ctrl-a-held-10 | Ctrl kept down through drag, trigger and drop | 1/1 | 0 | none |
| ctrl-click-held-2 | Ctrl-click 2, Ctrl held (owner report) | 2/2 | 0 | none |
| ctrl-click-held-10 | Ctrl-click 10, Ctrl held | 1/1 | 0 | none |
| ctrl-click-released | Ctrl-click 10, released, drag at once | 1/1 | 0 | none |
| shift-range-held-10 | Shift-click range 10, Shift held | 2/2 | 0 | none |
| shift-range-10 | Shift-click range 10, released, drag at once | 1/1 | 0 | none |
| shift-range-100 | range of 100: click, scroll with the wheel, Shift-click | 1/1 | 0 | none |
| alt-refused | Alt held: refused with a toast, nothing runs | 2/2 | 0 | none |
| escape | Esc cancels | 2/2 | 0 | none |
| outside | release outside the ring runs nothing | 2/2 | 0 | none |
| fast | fast releases follow the arm rule | 3/3 | 0 | none |
| category | a drop on a category runs nothing | 2/2 | 0 | none |
| edge | trigger in a screen corner | 1/1 | 0 | none |
| unicode-png | Arabic, emoji and spaces in folder and file names | 2/2 | 0 | none |
| unicode-batch-10 | Unicode names, 10 files | 1/1 | 0 | none |
| long-png | source path longer than 260 characters | 2/2 | 0 | none |
| long-batch-10 | long paths, 10 files | 1/1 | 0 | none |
| general-file-info | a file no module reads: File info under Tools, nothing written (V2 PR 1.4) | 2/2 | 0 | none |
| general-sha256 | SHA-256 of a file: the info panel, nothing written | 1/1 | 0 | none |
| folder-info | a dropped folder: File info, its contents unchanged (UX-009) | 2/2 | 0 | none |
| pdf-rotate | PDF rotate right: a new PDF, the original unchanged (V2 PR 2.1) | 2/2 | 0 | none |
| pdf-info | PDF info: the info panel, nothing written | 1/1 | 0 | none |
| zip-extract | ZIP extract: a new folder with exactly the archive's tree (V2 PR 5.1) | 2/2 | 0 | none |
| zip-test | ZIP test: the info panel, nothing written | 1/1 | 0 | none |
| tgz-extract | TAR.GZ extract: the same tree from a PAX TAR.GZ (V2 PR 5.2) | 2/2 | 0 | none |
| srt-to-vtt | Arabic SRT with an emoji to WebVTT (V2 PR 7.1) | 2/2 | 0 | none |
| srt-shift | subtitle timing 1 s later: a new SRT | 1/1 | 0 | none |
| docx-text | Word text: a new UTF-8 text file (V2 PR 6.1) | 2/2 | 0 | none |
| docx-info | document info with Office-shaped properties: the info panel | 1/1 | 0 | none |
| docx-clean | Remove properties: a copy without author or title (V2 PR 6.2) | 1/1 | 0 | none |

**All rows passed.**
