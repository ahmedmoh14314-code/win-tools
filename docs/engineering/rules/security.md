# Security rules

- **All files are untrusted input.** Extension is not type. Detect by content (magic bytes) and never select an engine from the extension alone.
- **Parsers and converters are vulnerable components.** Run them in a worker process with the least privilege practical. A malformed file may crash the worker but must not crash the main app or corrupt the original.
- **Process launching**: `ProcessStartInfo` with `ArgumentList` only. Never build a command line string from user-controlled data. Never go through `cmd.exe`. Set a working directory you own. Capture stdout/stderr with bounded buffers.
- **Paths**: handle long paths (`\\?\` prefix or `LongPathsEnabled`), Unicode, quotes and spaces; never write outside the chosen output directory; treat reparse points on the output path as a reason to stop.
- **Privilege**: never require Administrator or UAC elevation. If an experiment seems to need it, stop and document why in an ADR first.
- **No execution of content**: no macros, scripts, embedded executables, or auto-launching produced files. No shell "open" of outputs without an explicit user action.
- **Archives (if ever added)**: path traversal (zip-slip), symlinks, size bombs and nested-archive limits are required before support ships.
- **Secrets**: no signing keys, tokens or personal credentials in the repo, in any development tool's context, or in logs. `*.pfx`, `.env*`, `secrets.json` are gitignored by design.
- **Logging**: never log file contents; log paths with care (user privacy); logs stay local.
- **Dependencies are an attack surface**: see `dependencies.md`; check security history before adoption.
- The threat model lives in `docs/security/`; update it when a new input type, engine or IPC channel is introduced.
