# ADR-0006: Image conversion engine for V1 (JPG/PNG/WebP; convert, compress, resize)

- **Status:** Proposed
- **Date:** 2026-10-19
- **Related requirements:** SCOPE-001, ARCH-002, DEP-001, SEC-001

## Context
V1 needs decode/encode for JPEG, PNG and WebP plus resize and quality control. Candidates differ in license, binary size, WebP support, quality, metadata handling, security history and whether they are managed or native. The engine sits behind an adapter and runs in the worker (ADR-0005), so a later swap is acceptable, but the license decision is not reversible once shipped.

## Decision
Not yet decided.

## Alternatives considered (to be completed in the dependency matrix)
- Windows Imaging Component (WIC): in-box, no redistribution; WebP codec availability depends on the OS and the "WebP Image Extensions" store package, which cannot be assumed.
- ImageSharp (Six Labors): managed, WebP supported; license is Six Labors Split License (commercial terms apply above a revenue threshold), needs explicit review.
- SkiaSharp: BSD-3, native binaries, WebP encode/decode; size and native surface to consider.
- Magick.NET / ImageMagick: broad format support; license is ImageMagick (Apache-2.0-like) but bundled delegates vary; large; security history significant.
- libwebp + separate PNG/JPEG decoders via P/Invoke: smallest, most work.

## Consequences
To be written with the decision.

## Evidence / experiments
Dependency matrix rows for each candidate; golden-file quality comparison; encode/decode benchmarks on representative images; size of the shipped binaries.

## Evaluator review
Pending.
