# Project progress (cross-session state)

Keep this a snapshot, not a diary. Update at the end of every session (workflow step 13).

## Current phase
**Experimental framework phase closed (2026-10-21).** ADR-0001 is Accepted: native Win32 radial/drop overlay. ADR-0003 is Accepted with amendments: Space trigger, low-level hooks on a dedicated thread, asynchronous hand-off. Next comes the first product slice on branch `feat/first-image-conversion-slice`: PNG→JPEG and JPEG→PNG, end to end.

## Last known-good commit
`main`: the Spike 002C documentation commit (2026-10-21). Earlier checkpoints are kept in the private archive.

## Work completed (this phase)
- Spike 001 (interaction feasibility) and Spike 002 (dead zone, Win32 vs WPF, hooks under GC pressure, Raw Input, memory plateau). See `docs/research/`.
- Spike 002C ran a stall probe and found the WPF overlay's 5 s blocks are UI Automation / UI-thread stalls, not the watchdog:
  - a watchdog of 2/5/8 s did not change the stall;
  - with UIA disabled, the run had 0 stalls and 200/200 drops.
- Spike 002C fast release showed no host-specific loss once the overlay existed. Explorer fast release was not obtained because of harness defects; those are fixed but not re-run.
- Reference reviews and the licensing ledger are in `docs/research/references/`. The requirement ledger holds 34 entries.

## Decisions made
- Drag modifiers, the middle button and the right button are not triggers. Space is the default trigger.
- Post-release rule: after `DragEnter`, wait for OLE Drop/DragLeave. A short grace applies only without DragEnter. Esc is ignored while a drop is pending.
- The overlay is a native Win32 window. WPF is not in the resident interaction path. Future settings UI is deferred.
- Engines run only in an on-demand worker. Output goes temp → validate → exclusive create. No polling in the idle path.
- Raw Input and hook-health recovery are deferred future reliability work.

## Open questions (each with the experiment or decision that closes it)
- Explorer fast-release behaviour: product acceptance test in the first slice.
- Exact cause of the WPF ≈ 5 s CLR wait inside UIA: recorded as a rejected path, not pursued.
- Overlay accessibility (own MSAA/UIA provider that cannot block the overlay thread): before V1 ships.
- Not testable here: multi-monitor/mixed DPI, Windows 10, Desktop source (do it in Windows Sandbox), elevated source, the human feel pass.

## Current blockers
None.

## Next recommended task
First product slice (PNG↔JPEG) on `feat/first-image-conversion-slice`, reviewed through a pull request.
