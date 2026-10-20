# Windows integration rules

- **Research before assuming.** OLE drag-and-drop (`DoDragDrop`, `IDropTarget`, `DROPEFFECT`, modifier-key semantics), low-level hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`), `RegisterHotKey`, raw input, layered/topmost windows, DPI awareness (`PerMonitorV2`) and multi-monitor APIs behave in specific documented ways. Cite the documentation in `docs/research/` when a design depends on it.
- **The activation trigger is undecided** (ADR-0003). Ctrl, Shift and Alt during a drag change Copy/Move/Link in Explorer; do not assume any modifier is free.
- **Overlay windows**: topmost, layered, non-activating (`WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`), DPI-aware, positioned from the physical cursor position and clamped to the monitor work area. No hardcoded pixel offsets.
- **Hooks are dangerous**: a slow low-level hook is silently removed by Windows and degrades every app. Hook procedures post a message and return immediately.
- **Drag sources differ**: Explorer, Desktop and common apps are separate test cases; `CF_HDROP`, `CFSTR_SHELLIDLIST` and virtual files are not interchangeable.
- **Windows versions**: the support baseline is pending (ADR-0002); do not use APIs newer than the agreed baseline without an ADR.
- **Interop code** is isolated in a Windows-integration module with thin, tested wrappers. Prefer `CsWin32` source generation over hand-written signatures where it fits.
- **No Administrator, no `UIAccess`, no driver-level tricks.**
- Every interop hypothesis gets a spike with a recorded result before it becomes architecture.
