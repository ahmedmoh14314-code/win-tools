# Architecture rules

- **Module boundaries** (target shape, refined via ADRs): UI/Radial Overlay · Windows Integration · Input/Activation · Drag & Drop · Core Commands · File Type Detection · Capability Resolution · Job Management · Conversion Adapters · Worker Process · Settings · Logging · Notifications · Infrastructure.
- **Dependency direction**: UI → application/core → adapters → engines. Core never references UI or a concrete engine. Engines are reached only through adapter interfaces.
- **Capability system is central**: "what operations are valid for this input?" is answered by one resolver fed by declarative capability descriptors. The radial UI renders the result; format logic never lives in views.
- **Adding a format or operation** must not require changing the radial menu architecture.
- **Process model**: Main app → Job → Worker process → Engine. Jobs expose progress, cancellation, timeout, and an explicit failure state. The main app survives worker crash, hang, or out-of-memory.
- **Result model**: operations return explicit results (success, or failure with an actionable reason). Exceptions do not cross process or module boundaries unstructured.
- **No god classes**: no MainWindow/App/Service that "contains the application". Prefer small sealed types, records for data, and interfaces at boundaries only where a second implementation or a test seam is real.
- **YAGNI**: no speculative abstractions for future categories (video, PDF, Office). Keep the obvious extension points (capability descriptors, adapters, job pipeline) clean and stop there.
- **Decisions with material uncertainty** (UI framework, activation trigger, overlay technique, worker IPC, image engine) are made by spike + ADR, not by preference.
- C# style: modern C# conventions (records, nullable reference types, pattern matching, `CancellationToken` on every async API, Result-style errors at boundaries). Where an external style guide recommends actor frameworks or server patterns, project rules win (no actor frameworks).
