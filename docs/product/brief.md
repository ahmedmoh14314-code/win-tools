# Product brief (verbatim from the product owner, 2026-10-19)

This is the original brief that defines the product and the planning-phase deliverables. It is the source for `requirements.json`. Do not edit it to reflect later decisions; record those in ADRs and the requirement ledger.

---

We are starting a new Windows desktop software project.

Do NOT begin implementation yet.

Your first responsibility is to behave as the senior software architect and technical lead for this product. Research the problem, challenge assumptions, identify risks, propose an architecture, and produce an evidence-based implementation plan.

This project must be treated as production software, not as a quick AI-generated prototype.

Do not agree with requirements merely because I proposed them. If a requirement, technology, architecture, shortcut, dependency, UX decision, or conversion feature is technically unsound, risky, unnecessarily complex, poorly scoped, difficult to maintain, insecure, or likely to create performance problems, explicitly reject or challenge it and explain why.

Engineering correctness takes priority over pleasing me.

## Product concept

We want to build a lightweight Windows desktop file utility inspired by the interaction concept of applications such as Tangerine on macOS, but this must become our own Windows-native product, architecture, UX, branding, feature set, and implementation.

The core product idea is extremely fast file operations through a radial menu.

The intended interaction is approximately:

User selects a file in Windows Explorer/Desktop. User begins dragging the file. While still dragging, the user activates our application using a keyboard trigger. A radial menu appears immediately around or near the mouse cursor. The user moves the dragged file toward an operation/category. Nested radial menus may appear when appropriate. The user drops the file onto the desired action. The operation begins immediately.

Example: photo.png → drag → activation trigger → radial menu → Convert → WebP → drop → photo.webp is created safely.

The user should not normally have to open a conventional application window. The primary experience must feel fast, lightweight, native, predictable, and almost invisible.

## Important UX requirement

Do NOT assume that Ctrl alone should be the activation key. Windows uses keyboard modifiers such as Ctrl, Shift, and Alt during drag-and-drop operations and they may affect Copy/Move/Link behavior. Research this properly.

The activation mechanism must initially be treated as an open engineering question. Evaluate options such as: Space while dragging; Ctrl+Space; another keyboard combination; mouse buttons; configurable activation triggers; other Windows-native approaches.

The final choice must be based on compatibility, reliability, discoverability, latency, and interference with Windows Explorer behavior. The trigger must eventually be user configurable.

## Context-aware radial menu

The radial menu must not display every operation for every file. Operations should be derived from file capabilities.

For example, an image might expose: Convert, Compress, Resize, Crop, Metadata, Create PDF. A video might expose: Convert, Compress, Trim, Extract Audio, Extract Frames. A PDF might eventually expose: Compress, Images, Merge, Split, Rotate, Document conversion.

The UI must not contain hardcoded format logic scattered throughout view code. Design a central capability/command system that answers questions such as "What operations are valid for this input?" The radial UI should render the resulting capabilities. Adding a new file format or operation later should not require redesigning the radial menu architecture.

## Scope discipline

Do not attempt to implement every conceivable conversion in version one. The initial experimental version is for us and a small group of friends.

There is currently no requirement for: backend, server, database server, user accounts, cloud synchronization, Microsoft Store, payments, subscriptions, analytics, remote processing, AI functionality.

V1 should be local-first and offline. Files must remain on the user's computer. Network connectivity should not be necessary.

## Prototype scope

Recommend an intentionally small prototype. Our current preferred starting point is image operations such as JPG, PNG, WebP and a very small set of operations such as Convert, Compress, Resize.

The real purpose of the first prototype is not format coverage. Its purpose is to prove that this interaction works extremely well: drag file → activation → wheel appears instantly → select operation → drop → reliable processing. If this interaction is not excellent, we should improve or redesign it before adding additional formats.

## Document conversion warning

Do not treat all document conversions as equivalent. Conversions such as PNG → WebP are fundamentally different from semantic conversions such as Word → PowerPoint or layout reconstruction such as PDF → Word.

For every requested conversion path, classify it: lossless or near-lossless format conversion; rendering/export; content extraction; layout reconstruction; semantic transformation; unsupported/unreliable.

Do not advertise arbitrary "anything to anything" conversion. If a conversion cannot be performed reliably, say so and exclude it. A failed or unsupported conversion is preferable to silently generating a corrupted or misleading document.

Office/document conversion should NOT automatically be included in the first implementation. Research it separately. Evaluate possible approaches including Microsoft Office integration, Open XML based approaches, LibreOffice/headless conversion, dedicated document libraries, and other realistic alternatives. For every approach evaluate: quality, external dependencies, licensing, installation size, performance, whether Microsoft Office or LibreOffice must already be installed, commercial redistribution implications, format fidelity, failure modes.

## Technology selection

Do not blindly commit to a UI framework. C# and modern .NET are currently preferred. WPF + Win32 interoperability is currently a strong candidate because this application depends heavily on: cross-application drag and drop, transparent overlay windows, precise cursor-relative positioning, topmost windows, global input, system tray integration, Windows messages, native desktop behavior.

However, compare WPF, WinUI 3, direct Win32 where relevant, Windows App SDK integration, or a hybrid approach. Do not choose technology because it is newer. Choose it because it best satisfies the product requirements with the least risk. Create a small technical spike plan where necessary before committing to architecture.

Avoid Electron unless there is overwhelming evidence that it is the correct choice. Performance, native behavior, installer size, memory consumption, and integration quality matter significantly.

## Architecture principles

The program must be modular. Do not build a giant MainWindow or single service containing the application.

Investigate an architecture approximately separating: UI / Radial Overlay; Windows Integration; Input / Activation; Drag and Drop; Core Commands; File Type Detection; Capability Resolution; Job Management; Conversion Adapters; Worker Processes; Settings; Logging; Notifications; Infrastructure.

The UI must not know implementation details of FFmpeg, image libraries, PDF libraries, or document engines. Conversion engines should sit behind stable interfaces/adapters. We must be able to replace an engine later without rewriting the application.

## Process isolation

Investigate executing expensive or unsafe conversions outside the main UI process. Preferred conceptual model: Main Application → Job → Worker Process → Conversion Engine.

If a converter crashes, hangs, receives a malformed file, or runs out of memory, the main application should survive whenever reasonably possible. The UI must never freeze during conversion. Jobs should support: progress, cancellation, timeouts, clear failure states.

## Performance philosophy

This must be a lightweight utility. When idle, the application should perform almost no CPU work. Do not continuously scan the filesystem. Do not continuously poll Windows unless absolutely necessary. Do not preload heavy conversion engines simply because they may eventually be needed. Load expensive components on demand.

Establish measurable performance budgets for: idle CPU, idle memory, startup, radial-menu latency, job startup latency, UI responsiveness, installer size. Propose realistic targets and explain how they will be measured. Do not guess performance from intuition after implementation. Instrument and benchmark it.

## File safety

Original files must be protected. Default behavior should never destructively overwrite an original file.

Prefer: input.ext → temporary output → validate result → atomic/final move where practical → output.ext.

Handle filename collisions safely (photo.webp, photo (1).webp). A failed conversion must not leave a result that looks successfully completed. Temporary files must be cleaned safely.

## Security model

All files are untrusted input. The application must not assume that a file is safe simply because its extension looks valid. Research relevant attack surfaces.

Do not run the application as Administrator unless an unavoidable requirement is demonstrated. Avoid shell command construction such as `cmd.exe /c "<user-controlled string>"`. Use safe process/argument APIs.

File paths may contain spaces, quotes, Unicode, very long paths, special characters. Design for them.

Converters and third-party parsers should be considered potentially vulnerable components. Investigate isolation boundaries where appropriate. Do not execute macros or embedded executable content from Office documents. Do not automatically launch produced files. Review archive traversal issues if archive support is later added.

## Dependency policy

Do not add a dependency merely because it makes implementation easier. For every substantial third-party dependency document: purpose, license, redistribution rights, commercial-use implications, binary size, maintenance status, security history where relevant, alternatives, why we chose it.

Pay special attention to: FFmpeg, ImageMagick, PDF engines, archive engines, LibreOffice, Office SDKs, commercial libraries. Do not accidentally turn the application into GPL software or create redistribution obligations we do not understand. Create a dependency/license matrix before adopting major engines.

## Reliability requirements

"Works 100%" must not be interpreted as "every arbitrary file conversion succeeds." That guarantee is impossible. Instead design for deterministic and safe behavior: supported input succeeds reliably; unsupported input is rejected clearly; corrupted input does not crash the main app; converter crash does not corrupt the original; timeout is handled; user cancellation is handled; partial output is not presented as success; errors are actionable; the program remains usable after a failed job.

## Testing strategy

Create a serious testing strategy before implementation. Include: unit tests for capability resolution and command logic; integration tests for conversion adapters; golden/sample-file tests; malformed-file tests; large-file tests; Unicode/path edge cases; filename collision tests; cancellation tests; timeout tests; worker crash tests; drag/drop interaction tests; multi-monitor tests; different DPI/scaling tests; Windows 10/11 compatibility decisions if applicable; performance benchmarks; memory-leak checks.

The visual wheel alone is not enough. Reliability of the entire file lifecycle must be tested.

## Radial menu engineering

Investigate: cursor positioning; screen boundaries; multi-monitor environments; different DPI scaling levels; wheel placement near screen edges; drag visual feedback; nested wheel transitions; hit testing; accidental activation; cancellation; keyboard accessibility; mouse accessibility; animation performance; latency.

The wheel must remain usable when the cursor is near any corner of any monitor. Do not solve edge placement with brittle hardcoded pixel values.

## Installation and distribution

For the experimental stage, optimize for easy private installation. We do not need Microsoft Store integration yet. Research appropriate options for: portable/private builds; installer; MSIX; traditional installer if relevant; code signing implications; Windows SmartScreen behavior.

Do not spend significant engineering effort on public distribution until the core interaction is proven.

## Product growth

The architecture should allow future modules without requiring them today. Possible later categories may include: images, video, audio, PDF, documents, spreadsheets, presentations, archives, metadata, batch operations, file actions. But do not build speculative abstractions for years of hypothetical functionality. Apply YAGNI while ensuring obvious extension points are clean.

## Deliverables for the planning phase

1. Executive technical assessment.
2. Feasibility analysis of the drag-file → trigger → radial drop interaction on Windows.
3. Major technical risks ranked by severity and likelihood.
4. Technology comparison and recommended stack with justification.
5. Proposed architecture and module boundaries.
6. Proposed process/worker model.
7. File-operation safety design.
8. Threat model and security concerns.
9. Dependency and licensing strategy.
10. Performance strategy and measurable budgets.
11. Conversion capability taxonomy.
12. Explicit list of conversions that should NOT be included initially.
13. V1 experimental scope.
14. Milestone plan from technical spike through usable prototype.
15. Testing strategy.
16. Unknowns that require proof-of-concept experiments.
17. Decisions we should postpone.
18. Clear go/no-go judgment.
19. Suggested repository structure.
20. A short Architecture Decision Record list identifying the important decisions we will need to document.

Be skeptical. Do not hide complexity. Do not inflate complexity unnecessarily either. If something is straightforward, say so. If something is risky, say so. If one of my assumptions is wrong, correct it. Prefer simple, boring, proven engineering over cleverness.

The goal is not to impress us with architecture. The goal is to create a fast, stable, maintainable Windows utility that users can trust with their files.
