# ADR-0008: Packaging and private distribution for the experimental stage

- **Status:** Accepted for the 0.1.0 beta (2026-10-21, owner decision): per-user Inno Setup installer, plus a portable ZIP
- **Date:** 2026-10-19, decided 2026-10-21
- **Related requirements:** DIST-001, DIST-002, PLAT-003

## Context
A handful of users need an easy private install without Administrator rights. Options: portable zip (framework-dependent or self-contained, single-file), per-user installer (Inno Setup, WiX, Velopack-style), MSIX (requires signing with a trusted cert for sideloading, developer mode or cert install on the target machine). SmartScreen will warn on unsigned downloads; code signing certificates have cost and identity requirements. Packaging also interacts with ADR-0001 (WinUI 3 historically prefers packaged apps) and with how the worker process and engines are laid out.

## Decision
The owner chose the following for the first external tester build, WinTools 0.1.0 Beta (2026-10-21).

**The installer:** a per-user Inno Setup installer, `installer/WinTools.iss`, built by `installer/Build-Installer.ps1`.
- No elevation: it installs to `%LOCALAPPDATA%\Programs\WinTools`, with an HKCU uninstall entry and a Start Menu shortcut for this user.
- **The payload:** a self-contained Release win-x64 publish of the host, the worker and the settings window. It has no debug symbols, so no local path is written into the binaries, and the worker's memory limit (ADR-0015) is kept.
- **The upgrade identity:** the fixed AppId `{6B1BF2F8-4DD0-49D7-B036-8918F771AF80}`. Running Setup again upgrades in place.
- **A running WinTools** is detected through its single-instance mutex (`Local\WinTools.Host`). Setup and the uninstaller ask the user to exit it, and they never replace files while it runs.
- **User data:** settings (`%APPDATA%\WinTools`) and logs (`%LOCALAPPDATA%\WinTools`) survive upgrades and uninstalls. "Start with Windows" is never turned on by Setup, and uninstalling removes that Run value only if it points to this installation.
- There is no service, scheduled task, driver or desktop shortcut. "Launch WinTools" is an option on the last page.

**The portable ZIP:** the same self-contained payload, with a README, for trusted testers.

**Signing:** none for the beta. The binaries and Setup are unsigned, and SmartScreen warns on download (see Consequences).

## Alternatives considered
- **MSIX sideload:** needs a certificate the target machine trusts. Packaged identity would also change how the app's settings and Run entry behave.
- **WiX per-user MSI:** a heavier toolchain, and per-user MSI has well-known pitfalls. Inno is simpler and mature for per-user installs.
- **A single-file executable:** larger memory and start-up cost, and three programs would each embed the runtime.
- **A framework-dependent build:** testers would have to install the .NET 10 desktop runtime themselves.
- **Signing:** self-signed (no SmartScreen benefit), a purchased certificate, or Azure Trusted Signing. Deferred to a later release.

## Consequences
- The payload is about 146 MB installed and Setup is 49 MB, because the .NET and WPF runtimes are bundled; no .NET installation is needed.
- **Unsigned:** browsers and SmartScreen warn about an unknown publisher until a signing certificate is used ("Windows protected your PC", then More info, then Run anyway).
- **Uninstall:** it leaves the settings and logs behind on purpose. There is no option yet to remove them (DIST-003).
- **Inno Setup** is a build-time tool (Inno Setup License, free for commercial use). Its setup runtime is embedded in Setup.exe, and nothing else of it ships.

## Evidence / experiments
2026-10-21, on Windows 11 Pro 26200 x64, without elevation (`TokenElevation = 0`; Inno logged "Administrative install mode: No").
- **Install, launch and exit:** Setup installed, the Start Menu shortcut launched WinTools from the installed folder with its tray icon, the installed settings window opened, and the close path exited cleanly.
- **Explorer harness against the installed host** (`--host`): convert, resize, compress, rotate, remove metadata, image info and a batch of 12 passed 20 of 20 cycles.
- **A running WinTools:** Setup and the uninstaller both refused while WinTools was running.
- **Upgrade, uninstall and reinstall:** an upgrade over the same version kept one entry. The uninstall removed the files, the shortcut, the entry and the Run value, and kept the settings unchanged (same hash). A reinstall followed by another upgrade worked.
- **Scans:** Microsoft Defender found no threats, and no PDB, log, source or developer path is in the payload or in Setup.exe.
- **Not tested:** a separate standard (non-admin) account and Windows 10. The installing account is an administrator using a filtered (non-elevated) token.

## Evaluator review
None yet; the owner accepted the format for the beta. Review the script before a public release.
