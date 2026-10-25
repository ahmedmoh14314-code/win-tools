# Spike: does the dialogs process get the keyboard after a real drop? (throwaway)

**Question (ADR-0019, open since PR 1.3):** after a drop on "More…", the host starts `WinTools.Dialogs.exe` and calls `AllowSetForegroundWindow` for it. Does the dialog actually receive the keyboard? If it does not, Enter goes to Explorer, which opens the still-selected files.

The question gates every dialog that V2 still needs: passwords (PDF protect and unprotect, encrypted ZIP), batch rename, and a custom subtitle offset.

This is experiment code. It is not product code; findings go to `docs/research/2026-10-25-dialog-focus-after-drop.md`.

## How
- **`spike.patch`** (never merged) does two things:
  - with `WINTOOLS_SPIKE_MORE=1`, a category ring overflows at 3 commands, so PNG's Convert ring shows two commands plus "More…";
  - the Explorer harness accepts `--op menu.more`.
- **`watch.ps1`** logs foreground-window changes every 100 ms: process name, window class and focused control class only, never titles. With `-SendEsc`, it presses Esc 400 ms after `WinTools.Dialogs` comes to the front. Esc is harmless if it reaches Explorer instead.
- Each run:
  1. the watcher starts;
  2. the harness drags `photo.png` from a test Explorer window and drops on Convert → More…, with the patched host built next to `WinTools.Dialogs.exe`;
  3. the host log is read for the `more.pick` event.

## Results (`results/`)
| Run | The dialog came to the front | Win32 focus | Esc | `more.pick` |
|---|---|---|---|---|
| `run3` | yes, about 1 s after the drop | the dialog's own window | (not sent) | (host stopped by the harness) |
| `esc1` | yes | the dialog | sent while it was in front | `Cancelled`, then the foreground went back to Explorer |
| `esc2` | yes | the dialog | sent | `Cancelled` |
| `esc3` | yes | the dialog | sent | `Cancelled` |

"Cancelled" is the dialog's own Esc path. The alternatives would have been "Unavailable" (no program, the first attempt), a timeout, or the harness stopping the host.

## Limits
- The drag is injected with `SendInput`. Windows decides foreground rights by "who received the last input". A real mouse should behave the same (the button-up lands on the host's overlay window), but **one manual check by the owner** is still needed: drop by hand on a More… sector, then press Esc.
- Only Esc was sent. Enter, the arrows and typing are untested.
- One machine, one session, Windows 11 26200.
