# The dialogs process gets the keyboard after a real drop (spike, 2026-10-25)

**Answers:** ADR-0019's open question ("More… keyboard focus: spike before a module relies on it"). **Code and logs:** `spikes/dialog-focus/`.

## Result
Four runs dropped a file from a real Explorer window on a "More…" sector, through the Explorer harness. The drag and the input were injected; the host and dialog were the real build with a spike-only overflow switch.
- In **every run**, `WinTools.Dialogs` became the foreground window about 1 s after the drop, with Win32 focus in its own window.
- In the three runs that pressed Esc while it was in front, the dialog received it. The host logged `more.pick` ending in `Cancelled`, which only the dialog's own Esc path produces. The foreground then returned to Explorer.

So, as built, the host's `AllowSetForegroundWindow` does pass the foreground right on. The likely reason is that the drop's button-up is delivered to the host's overlay window, which makes the host the process that "received the last input event" (`SetForegroundWindow`'s documented conditions).

## Not yet shown
- **A real, non-injected drop.** It needs one manual check: drop by hand on a More… sector, press Esc, and the list closes with Explorer unaffected.
- **Enter, the arrows and typed text.** A password dialog types text, so its PR repeats this measurement with text input.
- **Other drag sources** (the Desktop, other apps) and other Windows versions.

## Consequence
The dialog-dependent V2 items (PR 2.2 passwords, PR 7.2 batch rename, the custom subtitle offset) are no longer blocked on this question. They still need the manual check above before a module's commands overflow into More… in a release.
