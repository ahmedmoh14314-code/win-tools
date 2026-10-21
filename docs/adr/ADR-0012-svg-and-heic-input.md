# ADR-0012: SVG and HEIC/HEIF as input formats

- **Status:** Accepted (2026-10-21). Both are deferred for V1; the conditions for revisiting them are below.
- **Date:** 2026-10-21
- **Related requirements:** SCOPE-001, SEC-001, SEC-003, SAFE-003

## Context
The owner asked for SVG as an input (rendered to PNG, JPEG or WebP) "where reliable", and for HEIC/HEIF as an input "only if it can work reliably for normal Windows users". Raster → SVG vectorisation is a different, future feature and is not considered here.

## Decision
**SVG input: deferred.** Rendering SVG means parsing untrusted XML and resolving references. The two practical engines both fail a product rule today.
- **Svg.Skia 5.2.3** (MIT, on SkiaSharp 4.x) has unsafe defaults, from a source review of tag v5.2.3:
  - `<image>`, `<use href="other.svg#…">`, CSS `@font-face`/`@import`/`xml-stylesheet`, and SMIL `<animate>`/`<font-face-uri>` targets read **local files, UNC paths and http(s)**.
  - Closing all of them takes two process-wide statics plus a per-document policy and a setting. The animation and font-face-uri path ignores the per-document policy.
  - It enforces no limits on dimensions, element count, nesting depth (uncatchable stack overflow) or `<use>` fan-out.
  - Malformed elements are swallowed, so a partial render could look like a success.
  - It adds HarfBuzzSharp and Svg.Custom (MS-PL), about 15 MB per runtime identifier.
- **Direct2D's built-in SVG** (`ID2D1DeviceContext5::CreateSvgDocument`) makes no external loads, but it silently ignores `<text>`, stylesheets, filters, masks, patterns, markers and `<symbol>`. Many real-world SVGs would convert "successfully" but wrongly, which violates "a failed job must never look like a success".

**HEIC/HEIF input: deferred.**
- Windows decodes HEIF through WIC (`CLSID_WICHeifDecoder`, routed via `windowscodecs.dll`). That needs both the *HEIF Image Extensions* and the *HEVC Video Extensions* from the Store.
- The HEVC extension is a paid download unless the PC maker preinstalled it. The development PC has both (OEM), but a normal user cannot be assumed to.
- The alternative, libheif plus libde265, is LGPL-3.0 (dynamic linking and an ADR required) and carries HEVC patent exposure.
- Offering the command only on machines where the extension happens to work would make the product behave differently per PC, with no way to explain why.

## Alternatives considered
- Adopting Svg.Skia now with the hardened configuration: rejected for V1 on risk and size, as above.
- A WIC HEIF capability probed per machine in the worker: rejected for V1. It adds a startup probe and behaviour that differs between machines.

## Consequences
- `.svg`, `.heic` and `.heif` files get no commands ("No actions"). The capability matrix does not contain them.

**Conditions to revisit SVG (Svg.Skia):**
- Every external path is disabled: `ResolveExternalImages`, `ResolveExternalElements` and `ResolveExternalXmlEntites` = None; `SvgExternalResourcePolicy.Disabled` with `SecureStatic`; SVG fonts off.
- A pre-parse gate on size, depth and element count.
- Rendering on a large-stack thread, inside the worker's memory-limited job object.
- Blank-render and blocked-reference detection.
- A canary spike showing zero file or network access beyond the input, observed with Process Monitor and a local listener.

**Conditions to revisit HEIC:** either the HEVC decoder becomes part of a standard Windows installation, or a licence-clean decoder is chosen in an ADR.

## Evidence / experiments
- **SVG:** source review of Svg.Skia v5.2.3 (commit `7910666`) and Svg.Custom (`7a418e7`). Key locations:
  - `Svg.Custom/Compatibility/SvgDocumentCompatibilityLoader.cs:88-93`: XML reader settings.
  - `Svg.Model/Services/SvgService.cs:110-183, 621-662`: external references.
  - `Source/SvgElementIdManager.cs:44-81`: the animation path, which bypasses the per-document policy.
  - `Source/SvgDocument.cs:27-43`: static defaults.
  - Microsoft documentation: [Direct2D SVG support](https://learn.microsoft.com/en-us/windows/win32/direct2d/svg-support).
- **HEIC:** on the development PC, the codec CLSIDs and the installed Store packages were checked (2026-10-21):
  - `Microsoft.HEIFImageExtension` 1.2.48.0 and `Microsoft.HEVCVideoExtension` 2.5.33.0 are installed.
  - `{E9A4A80A-…}` is registered to `windowscodecs.dll`.

## Evaluator review
Covered by the evaluator of the image-formats PR.
