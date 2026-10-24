# ADR-0017: Compress presets keep a measured fidelity, not a fixed encoder quality

- **Status:** Accepted (implementation); the gate values are calibrated on a generated corpus and await the owner's visual validation
- **Date:** 2026-10-23
- **Deciders:** product owner (V2 plan approval, 2026-10-23, with amendments)
- **Related requirements:** OPS-005

## Context
**Owner field report (0.1.0 Beta):** Compress often saved only a few percent on real photographs, and it wrote copies nobody needed.

**Cause.** In V1 each preset was one fixed encoder quality: High 90, Balanced 80, Smallest 65. That had four consequences:
- **No fidelity measure.** Nothing measured how the copy looked, so a noisy JPEG at Balanced dropped to luma SSIM 0.80.
- **Insignificant copies.** Any output one byte smaller was written, such as phone.jpg at High, which saved 6.6 %.
- **Lost colour resolution.** A 4:4:4 source always became 4:2:0.
- **Tests that could not catch it.** The tests used uniform random noise and only checked that the sizes were ordered.

The Stage 0 reproduction is in commit 611ff10, and the plan is `docs/plans/2026-10-23-v2-technical-plan.md`.

**Owner amendments:**
- SSIM must not be the sole gate.
- Chroma preservation, colour difference and edges must be investigated too.
- The 8 % minimum saving and the floors 0.990 / 0.980 / 0.960 are starting points to validate.

## Decision
**1. A preset is a promise about appearance.** For JPEG and WebP, Compress writes the smallest encoding on a bounded quality ladder whose fidelity passes every gate of the preset. It writes the copy only if that copy saves at least **8 %**. Otherwise it reports "Already optimized" and writes nothing.

PNG stays lossless and is verified pixel for pixel, with the same 8 % minimum.

**2. Fidelity has six axes** (`Fidelity`, in the worker). Each one catches a loss the others miss:

| Axis | What it catches |
|---|---|
| Luma SSIM (mean) | overall structural loss |
| Worst-region luma SSIM (1st percentile of 8×8 windows) | damage concentrated in text, edges or one area, which a clean background averages away |
| Chroma SSIM (Cb, Cr) | lost colour resolution (4:4:4 → 4:2:0), colour blocking |
| Mean ΔE (CIE76, CIELAB, D65) | overall colour shift |
| ΔE 99th percentile | local colour errors (fringes, bleeding) |
| Edge SSIM (Sobel magnitude) | ringing and blur at edges |

**3. Gates:**

| Preset | Meaning | Luma ≥ | Worst ≥ | Chroma ≥ | ΔE mean ≤ | ΔE99 ≤ | Edge ≥ | Lowest quality |
|---|---|---|---|---|---|---|---|---|
| High | no visible difference | 0.990 | 0.970 | 0.993 | 1.0 | 4.0 | 0.975 | 85 |
| Balanced | differences only on close comparison | 0.980 | 0.930 | 0.985 | 1.5 | 6.0 | 0.950 | 65 |
| Smallest | visible on close inspection, never damaged | 0.960 | 0.880 | 0.970 | 2.5 | 10.0 | 0.900 | 50 |

**4. Search.**
- **Ladder:** 95, 93, 90, 88, 85, 82, 80, 75, 70, 65, 60, 55, 50, cut at the preset's lowest quality. It is searched by bisection, with at most 4 encodings per chroma mode.
- **Chosen candidates always pass.** Pass and fail are only close to monotonic in quality (measured: noisy.jpg scores lower at q95 than at q92), so a non-monotonic step can cost some saving, never quality.
- **Skipping useless candidates:** a candidate that would not save 8 % is not measured, because a higher quality could not save more.
- **Chroma modes:** a JPEG whose frame header says 4:4:4 is tried at both 4:4:4 and 4:2:0, and the gates decide. A subsampled source stays subsampled. Lossy WebP is always 4:2:0 (a libwebp property).

**5. Feedback.** Success reads "2.37 MB → 597.7 KB, saved 75%". When nothing is written, it reads "Already optimized: a smaller copy would not save enough or would look different".

**6. Cost and memory.** The fidelity computation streams over rows of 4×4 blocks in parallel bands. It keeps a few rows and a histogram per band, never a plane. At the 48-megapixel limit it allocates about 9 MB and takes about 0.9 s.

## Alternatives considered
- **Keep fixed qualities (V1), only add the 8 % minimum.** Rejected:
  - It removes the insignificant copies but none of the quality failures: noisy at 0.80 and the screenshot's text damage.
  - It still discards 4:4:4.
- **Luma SSIM as the only gate (the plan's first proposal).** Rejected after measurement:
  - **Screenshot at q90:** mean SSIM 0.9905, but the worst 1 % of windows is at 0.88, which means ringing glyphs.
  - **Camera 4:4:4 → 4:2:0 at q97:** luma 0.9985 hides a chroma SSIM of 0.986.
  - **Camera 4:2:0 at q50:** passes luma for Smallest (0.966) with ΔE99 10.95.
- **A perceptual metric library (Butteraugli, SSIMULACRA2, VMAF).** Not adopted for now:
  - Each is a new native dependency, needing a licence and security review (dependencies.md) and a worker-side binary.
  - The six axes are short, auditable code with no dependency.
  - Revisit if the owner's visual validation shows the gates are wrong in practice.
- **Target a file size or a percentage.** Rejected: it promises nothing about appearance, and on noisy images it reproduces the V1 failure.
- **Encoder-side tricks (mozjpeg trellis, jpegli, WebP sharp YUV).** Out of scope: they are new dependencies. The design keeps the encoder behind `Encode`, so a better encoder can be swapped in later.

## Consequences
- **Typical photographs get real reductions.** Measured on the corpus:

  | File | High | Balanced | Smallest |
  |---|---|---|---|
  | phone | 14 % | 64 % | 74 % |
  | camera | 14 % (4:4:4 kept) | 68 % | 89 % |
  | lossy WebP | 24 % | 62 % | 78 % |

- **Files that cannot shrink without visible loss are reported as already optimised**, not degraded:
  - noisy.jpg at High and Balanced;
  - moderate.jpg (q75) at High and Balanced;
  - the lossless screenshot WebP at every preset.
- **Screenshots compress less than plain luma SSIM would allow** (Balanced 10.8 %, Smallest 27.1 %), because the worst-region gate protects text. The Stage 0 test's 30 % expectation for Balanced came from the luma-only plan. It was changed with this rationale, in the commit that changed the semantics.
- **Lossless sources are compressed conservatively.** Lossy WebP is always 4:2:0, so a lossless photograph with fine colour detail and the corpus's per-channel noise can fail the High and Balanced chroma and ΔE gates and be reported as already optimised. Smallest's chroma and ΔE99 gates (0.970, 10.0) were set from that measurement: a 400×300 lossless photo then compresses at q85 and saves 97 %. Owner validation is owed on real lossless photos.
- **Cost.** Per image it is now 0.07–1.0 s at 2 MP and up to 14 s at 48 MP for a 4:4:4 source, against the 60 s job timeout. V1 ran a single encode per file, but its time was not measured, so there is no like-for-like comparison yet. Measurements: `docs/performance/2026-10-23-compression.md`.
- **The gates are numbers from a generated corpus, not from viewers.** The corpus has no faces, skin tones, real sensor noise or real screenshots. The owner's visual check on real files is the validation still owed, and the thresholds are recorded here so they can move with evidence.

## Evidence / experiments
- **Stage 0 (611ff10):** `CompressionQualityTests` failed 14 of 38 on main 8836149. The failures were broken floors, insignificant copies, 4:4:4 not kept, and no "saved X%" wording.
- **Measurements:** `docs/performance/2026-10-23-compression.md`. Every axis was measured across the quality ladder for every corpus file (`CompressionMeasurement.Measure_fidelity_axes`), plus the times and the 48 MP case.
- **Release gate:** `CompressionQualityTests`, 39 cases, checked with a luma SSIM written independently of the product (`Ssim` in the test project). `FidelityMetricTests` (7) check that each axis sees the damage it exists for. All pass.
- **Older tests:** three compress tests used fixtures that the new semantics correctly refuse, so their fixtures were replaced in a separate commit and their assertions kept:
  - uniform noise;
  - a 64×48 image that is mostly ICC profile;
  - x·y aliasing art.

## Amendment: worst-tile gates, search deadline, transparency (2026-10-24, evaluator review of PR #12)
- **Worst tile.** The worst-region gate (1st percentile of windows) cannot see damage in less than 1 % of the image. Two axes are added: the mean luma SSIM and the mean ΔE of the worst 64×64 tile, with tiles every 32 px, so damage 32 px or more across lies almost wholly in one tile. Gates: tile SSIM ≥ 0.975, 0.950 and 0.900; tile ΔE ≤ 3, 5 and 7 (High, Balanced, Smallest). The probe and the measurements are in `docs/performance/2026-10-23-compression.md`. Before the gates, Balanced left a caption covering 0.4 % of a photo at SSIM 0.926, under its floor.
- **Thin images.** Images under 8 px on a side were compared as one window, so a damaged stretch of a long strip averaged away. They are compared in windows of the short side × 8 px along the long side.
- **Search.** Pass and fail are not monotonic enough for plain bisection: on a GDI+ photo, q85 failed the edge gate and q82 passed, and Balanced reported "already optimized". A failure is checked one quality lower before the search turns back, and when nothing has passed, every small-enough quality not yet tried is tried. "Already optimized" is then true of the whole ladder (`CompressionSearchTests`).
- **Deadline.** The search stops after 25 s and uses the best passing copy found so far. With none, the job fails with the timeout reason and writes nothing; "already optimized" is said only when the search finished. There is no cancellation token inside the worker: the host's cancel and its 60 s timeout kill the worker process, and the deadline keeps a slow search inside that limit.
- **Transparency.** Fidelity is measured on white, so a change in alpha alone would be invisible to it. A candidate whose alpha differs from the source's is rejected. libwebp keeps lossy WebP's alpha lossless today; `Lossy_webp_keeps_transparency_exactly` guards it.
- **Wording.** A mixed Compress batch says what was saved and how many files were already optimized. The "less than 8%" message no longer depends on the worker's culture.
- **Test independence, stated precisely.** The release tests' SSIM (`Ssim` in the test project) is separate code, but it uses the same window size, grid and constants as the product's luma axis. It guards against a bug in the product's code, not against a blind spot of the method; the caption probe measures the region on its own for that reason.
- **Thin margins.** Some corpus results sit close to their floors (camera High at 0.9908 against 0.990). A change in the encoder or the corpus can move them; the tests then fail and the numbers here are revisited, never the floors lowered silently.

## Evaluator review
PR #12, 2026-10-24: no blocker. Two major findings (damage in less than 1 % of the image passed; the time was measured on one machine only) and the minor ones are answered by the amendment above.
