# ADR-0016: V1 user interface surfaces: rendering, motion, sound, feedback, tray and settings

- **Status:** Proposed (2026-10-21), V1 UI/UX phase
- **Date:** 2026-10-21
- **Deciders:** product owner (V1 UI/UX brief, 2026-10-21)
- **Related requirements:** UX-003, UX-006, UX-007, SAFE-003, SAFE-005, ARCH-004, PERF-001, PERF-002

## Context
Functional V1 proved the interaction: drag, trigger, two-level radial menu, drop, worker. The interface around it was deliberately primitive:
- flat GDI pies with jagged edges;
- the error beep as the only failure feedback;
- a modal message box for "Image info";
- a console window, and no tray icon or settings interface.

The owner's brief for this phase asks for a polished, fast, quiet, premium utility without touching what works:
- the native Win32 overlay (ADR-0001), the interaction rules of ADR-0014, the worker model and the file safety;
- the latency budgets: warm trigger to usable overlay ≤ 50 ms p95, first activation ≤ 120 ms.

Platform facts this design depends on (Microsoft documentation, *Window Features*, "Layered Windows"; *PlaySound*):
- **Hit testing of a layered window follows its pixels.** Areas whose alpha value is zero let mouse messages through, and with `WS_EX_TRANSPARENT` the whole window does. For the overlay this means any alpha-0 pixel inside the disc would let the OLE drag reach the window below (the ADR-0001 rule).
- **`PlaySound` with `SND_ASYNC` returns immediately, and a new call stops the sound playing in the process.** With `SND_MEMORY` the buffer must stay valid until the sound ends.

## Decision

### 1. Overlay rendering
The radial overlay becomes a per-pixel-alpha layered window (`UpdateLayeredWindow`). It keeps the same class, the same `IOverlayEvents` contract, the same geometry (`RingGeometry`) and the same navigator.
- **Shapes:** anti-aliased with GDI+ (part of Windows), into a 32-bit premultiplied DIB.
- **Text:** GDI ClearType on top. The pixels under the text are then made opaque again.
- **Opaque disc:** every frame paints an opaque base disc over the whole circle. Only the corners outside it are transparent, as with the old window region. A test checks every pixel of every ring state at 96–192 DPI.
- **Content first:** the frame is set before the window is shown, so a stale frame of the previous session is never seen.
- **Failed frames:** if drawing fails, the disc is not opaque at its centre and just inside the rim, or `UpdateLayeredWindow` refuses the frame, the ring is not shown (or is hidden) rather than shown with holes.

### 2. Visual states
The states differ by more than colour:
- **Hovered:** a raised surface plus an accent lip along the outer edge.
- **Armed** (a drop would run the command now): the sector is filled in the armed green, grows towards the rim and has dark text.
- **Back:** a darker sector with an arrow, never green.
- **Waiting:** before the first DragEnter the ring is empty, so it never says "No actions" for files that do have commands.

### 3. Motion
Motion is decorative only. `RingAnimation` animates:
- the appearance: 120 ms, starting from a readable frame;
- hover: 90 ms;
- arming: 70 ms.

A drop is still judged only by `MenuNavigator` (ADR-0014): what is under the pointer and how long its ring has been on screen. The drop effect is unchanged.

Animation frames come from a timer while something moves, and stop when everything has settled.

The new setting **Animations** takes "Follow Windows" (the default), On or Off. "Follow Windows" reads *Animation effects* (`SPI_GETCLIENTAREAANIMATION`) at each show; when motion is reduced, every change is immediate.

### 4. Sound
UI sounds are synthesised in code (`SoundBank`): short, quiet sine partials with no recorded or third-party audio. They are played with `PlaySound(SND_MEMORY | SND_ASYNC)` on a dedicated thread, so a call never blocks the overlay thread.
- **When ticks play:** `NavigationSounds` gives one tick per change to a different sector of the same ring. Leaving to the dead zone and coming back to the same sector is silent.
- **Level changes:** a level sound plays when a category opens or Back is taken.
- **Rate limit:** ticks are at least 45 ms apart, with 80 ms of quiet after a level sound. A tick that is held back is dropped, never queued.
- **Results:** success, failure and "nothing to do" sounds end a job.
- **Setting:** "UI sounds", on by default. Off, no audio thread starts.

### 5. Feedback
- **Toast:** a click-through (`WS_EX_TRANSPARENT`), non-activating, topmost card in the bottom-right corner of the work area of the monitor where the drop happened.
  - It shows a progress ring and bar for a batch still running after 450 ms.
  - It then shows one summary: the new file's name, or "N images completed", the failures counted with their most common reason, and the size change.
  - It disappears on its own after 3.5 s for success or 7 s for a failure.
  - Reasons come from `FeedbackText`, which maps error codes to plain words. It never shows codes, worker text or paths.
- **Image info:** shown in a non-activating panel beside the drop point: as many files as fit the work area's height, up to three, then "+N more". Any click on it closes it, and so do the next radial menu, `WM_CLOSE` and a 30 s timeout. GPS location is highlighted.
- **A live ring stays on top.** The info panel is not click-through and not a drop target, so one that is ready while a ring is open waits until the ring closes. A toast that appears during a drag is put back below the ring.
- **Settings during a drag:** the ring keeps the menu it opened with. A change saved during a drag applies from the next one, and a drop that no longer matches the files plays the "nothing" sound.

Neither surface ever becomes the foreground window. The Explorer harness checks this on every cycle.

### 6. Tray and process
- The host is a windowless `WinExe`.
- Its tray icon (`Shell_NotifyIcon` version 4, re-added on `TaskbarCreated`): a left click opens Settings, and a right click opens the menu with Settings…, Open logs folder and Exit WinTools.
- An exception in a tray command or in an action posted to the UI thread is logged instead of ending the host.
- A second start of the host exits quietly with code 2. The first instance's icon is already there.

### 7. Settings
Settings are edited in **a separate WPF process**, `WinTools.Settings.exe`. WPF is allowed for ordinary windows (ADR-0001), but here it never loads into the resident host.
- **Saving:** the window saves through the same atomic `SettingsStore` (moved to `WinTools.Windows`), then raises a named auto-reset event, `Local\WinTools.SettingsChanged`.
- **Applying:** the host waits on that event on the thread pool, with no polling. It reloads the file and applies the change at once: trigger key, menu custom size, sounds, motion, qualities and output folder.
  - Only a file that was actually read is applied. A failed read (locked, corrupt, missing) keeps the settings in use instead of falling back to defaults.
  - The window retries a failed save, since the file can be briefly locked. If the change still cannot be saved when the window closes, it stays open once and says so; closing again discards the change.
  - If the event name cannot be used, the host runs on and applies changes at the next start.
- **Theme:** V1 ships the dark theme only, and the window says so.
- **Start with Windows:** writes or deletes only the `WinTools` value under `HKCU\…\Run`, holding the quoted path of this copy's host. That value is replaced only when the user turns the option on.

### 8. Identity
The product mark is a five-segment ring with one segment lit, drawn from the same vector code as the menu. The icon (`Assets/WinTools.ico`) is generated from it by `tests/WinTools.Preview --icon`.

The palette is dark green-tinted neutrals with emerald as the only accent; restrained red and amber are used for failures and warnings only. See `docs/design/ui-v1.md`.

## Alternatives considered
- **Direct2D/DirectWrite for the overlay:** better text and GPU composition, but a large hand-written COM vtable surface. GDI+ plus GDI already renders a frame in about 0.6 ms at 120 DPI, so it was not needed.
- **Keep the window region and only restyle the GDI paint:** jagged edges, and no alpha for motion or depth.
- **Fade the whole overlay with the window's constant alpha:** risks the window falling through to the window below while it is faint. Instead the content animates over an always-opaque disc.
- **Windows toast notifications (WinRT):** need an AppUserModelID and a Start-menu shortcut for an unpackaged app, and go to the notification centre. They cannot sit next to the drag.
- **Activating dialogs for results:** steal focus from Explorer mid-task; ruled out by the brief.
- **WPF settings inside the host:** about 80 MB more private memory for the rest of the session, against a budget of 100 MB, and WPF's UI Automation on the resident process (the ADR-0001 stall evidence). Rejected.
- **`FileSystemWatcher` on the settings file:** fires several times per atomic replace and also on unrelated edits; a named event says exactly "saved".
- **System sounds (`MessageBeep`):** the Windows error beep for every failure is what the brief asked to replace. XAudio2 would give mixing and lower latency at the cost of COM interop that is not needed for a few short cues.
- **A UI Automation provider for the overlay now:** see Consequences.

## Consequences
- **Positive:**
  - The overlay paints faster than before. A full show → paint → move → paint → hide cycle went from p50 6.4 ms to 2.65 ms (soak).
  - Feedback is visible and actionable without opening a log.
  - The resident host stays free of WPF; a test checks its references and runtimeconfig.
- **Negative:**
  - We own the rendering of four surfaces (overlay, toast, panel, icon). Text under GDI must lie on opaque pixels, which is a rule for future drawing code.
  - On a machine with *Animation effects* off, "Follow Windows" shows no motion at all. The owner's development machine is one, and only the Animations setting shows the motion there.
- **Accessibility:**
  - **Done:** contrast is checked by a test (WCAG ratios). States are not colour only. Text is at least 11 px at 96 DPI (labels 13 px; a test checks every known label) and scales with the monitor's DPI. Motion follows *Animation effects*, and sounds can be turned off. The settings window is ordinary WPF, with keyboard focus outlines and automation names.
  - **Not done:** the overlay still has no UI Automation provider.
  - **Why not:** it exists only during a mouse drag, which has no keyboard or screen-reader path, and a provider runs on the overlay thread. ADR-0001 showed that UI Automation work on that thread can stall it for seconds. Adding one would put that risk into the latency-critical path in exchange for little benefit while the overlay can only be reached by a drag.
  - UX-007, a keyboard alternative path, stays deferred. This is recorded as a known V1 limitation.
- **Security** (`docs/security/threat-model.md`):
  - New local channels: the settings-changed event (no payload: it can only make the host re-read its own file), the settings process's `--settings` argument, and the Run value.
  - "Open logs folder" starts `%WINDIR%\explorer.exe` on the host's own log folder only, at the user's request.

## Evidence / experiments
- **Frame cost** (`tests/WinTools.Preview --bench`): p50 0.62 ms and p95 0.81 ms per frame at 120 DPI.
  - The first version used GDI+'s gamma-corrected compositing at 4.2 ms per frame. It was changed after this measurement.
- **Overlay soak**, 3000 cycles: GDI objects constant at 10, no handle or memory growth, cycle p50 2.65 ms and p95 3.89 ms.
- **Tests:**
  - `UiSurfaceTests`: opaque disc in every state, frame and DPI (mutation-checked: shrinking the base disc fails it); every known label fits without an ellipsis at 96/120/144/192 DPI; contrast; the toast's and panel's no-focus window styles; the settings signal round trip; the host never loads WPF.
  - `FeedbackTests`: navigation sound rules, sound files, toast text, info rows, label layout, animation and trigger-key change.
  - `JobFeedbackTests`: progress counts every file once, and the summary carries names and sizes, through the real worker.
- **Explorer suite and measurements:** `docs/performance/2026-10-21-ui.md`.

## Evaluator review
- **First review (2026-10-21): MERGE WITH FIXES.** No blocker. The reviewer confirmed:
  - the ADR-0014 rules, the drop path and the drop effect are unchanged;
  - the armed green never shows before the navigator arms;
  - content is set before a window is shown;
  - nothing activates outside the tray menu and the settings program;
  - sounds stay off the overlay thread;
  - the host has no WPF reference, and the screenshots contain no personal paths.
- **Major findings, fixed:**
  - Missing evidence documents: the measurement document is added and the dangling references are fixed.
  - The publish folder had no settings program: it is now copied on publish, and the publish fails if it is missing.
- **Minor findings, fixed:**
  - the menu of a session is fixed against a settings reload;
  - a failed reload no longer resets the host to defaults;
  - a failed save is retried;
  - the info panel fits the screen;
  - the toast no longer polls at rest;
  - the info panel waits for, and a toast stays below, a live ring;
  - the tray opens Settings on a left click;
  - exceptions in UI callbacks are contained;
  - a failed frame is never shown;
  - text is at least 11 px;
  - the settings-signal test uses its own event name;
  - new tests cover the job feedback mapping and the progress counts.
- **Nits, fixed:** an uncommitted size is saved on close; the saved message no longer claims a running host; a failed event name no longer stops the host; a partly failed Image info batch says what failed; sounds warm up when turned on; a middle click closes the panel.
- **Re-check of the fixes (2026-10-21): FIXES OK**, no blocker or major. Its minor findings were applied before merge:
  - a frame that fails keeps the last good frame unless the ring has just moved, and a self-hide is logged and reported;
  - the categories' label text is now tested against the drawn band;
  - a save that fails on close keeps the window open once;
  - UI errors are logged by type only (messages can hold paths).
- **Kept open:**
  - The tray icon is loaded at the system DPI and not refreshed when it changes.
  - Usage errors of the windowless host still go to standard error.
  - `FeedbackPresenter`'s timing and `ApplySettings` in the middle of a session have no automated test; they rely on code review and the Explorer runs.
