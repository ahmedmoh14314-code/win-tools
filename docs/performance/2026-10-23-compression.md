# Compression: before and after ADR-0017 (2026-10-23)

**Machine and build.** The machine is the development PC: Windows 11 Pro 26200 and .NET 10. The build is Release, measured in process through `ConversionJob.Run`, the same code the worker runs.

**Corpus.** `CompressionCorpus` is generated, 1600×1200, with no third-party or personal files:
- photographic scenes with gradients, texture, edges and sensor noise;
- noisy scenes;
- screenshots with flat panels, 1 px rules and glyph-like strokes.

The files are encoded the way real ones are, at the qualities listed in the tables.

**Metric for the outcome tables.** Luma SSIM, independent of the product (`Ssim` in the test project).

## Outcome per preset

### Before: main 8836149, fixed qualities 90 / 80 / 65
| File | High | Balanced | Smallest |
|---|---|---|---|
| phone.jpg (q93, 4:2:0) | **−6.6 %**, SSIM 0.995 | −54.7 %, 0.985 | −67.6 %, 0.979 |
| camera.jpg (q97, 4:4:4) | −71.2 %, **0.983** (4:4:4 lost) | −83.7 %, **0.977** | −88.7 %, 0.972 |
| moderate.jpg (q75) | nothing to do | nothing to do | **−5.4 %**, copy written |
| noisy.jpg (q92) | **−4.5 %**, copy written | −57.7 %, **0.797** | −71.2 %, **0.752** |
| screenshot.jpg (q95) | −21.2 %, 0.991 | −39.9 %, **0.977** | −53.8 %, **0.960** |
| photo-lossy.webp | −23.9 %, 0.991 | −61.7 %, 0.981 | −72.3 %, 0.975 |
| screenshot-lossless.webp | nothing to do | nothing to do | nothing to do |

### After: ADR-0017 gates
| File | High | Balanced | Smallest |
|---|---|---|---|
| phone.jpg | −14.4 %, 0.9920 (q88) | −54.7 %, 0.9854 (was −63.9 % before the 32×32 tiles) | −73.5 %, 0.9732 (q50) |
| camera.jpg | −13.6 %, 0.9908 (q95, **4:4:4 kept**) | −62.8 %, 0.9825 (was −67.9 %) | −86.9 %, 0.9676 (was −88.7 %) |
| moderate.jpg | already optimised | already optimised | −9.9 %, 0.9857 (was −18.2 %) |
| noisy.jpg | already optimised | already optimised | −9.4 %, 0.9623 (q88) |
| screenshot.jpg | already optimised | −10.8 %, 0.9962 (q93) | −27.1 %, 0.9910 (q88) |
| photo-lossy.webp | −23.9 %, 0.9914 | −61.7 %, 0.9813 | −70.2 %, 0.9759 (was −77.9 %) |
| screenshot-lossless.webp | already optimised | already optimised | already optimised |
| unoptimized.png (lossless) | | | −99.6 %, identical pixels |
| optimized.png (lossless) | | | already optimised |

Every written copy meets its preset's luma floor (0.990, 0.980 or 0.960) and saves at least 8 %. The product's own gates also hold on the other axes.

### Worst-tile gates (2026-10-24, evaluator finding on PR #12)
The 1st percentile of 8×8 windows cannot see damage in less than 1 % of the image. Probe: a 160×48 caption (0.4 % of a
1600×1200 phone-style photo). Its own SSIM against the worst 64×64 tile, per JPEG quality:

| q | saved | 1st percentile | worst tile | caption SSIM |
|---|---|---|---|---|
| 88 | 14.6 % | 0.9770 | 0.9799 | 0.9758 |
| 80 | 54.5 % | 0.9470 | 0.9580 | 0.9509 |
| 75 | 59.7 % | 0.9365 | 0.9450 | 0.9330 |
| 70 | 63.8 % | 0.9310 | 0.9394 | 0.9260 |
| 50 | 73.3 % | 0.9075 | 0.9063 | 0.8941 |

Before the tile gates, Balanced chose q70: the 1st percentile passed (0.931 ≥ 0.930) while the caption fell to 0.926. The
worst tile follows the caption closely (about 0.01 above it), so the gates are High 0.975, Balanced 0.950 and Smallest
0.900, with worst-tile mean ΔE at most 3, 5 and 7. They change no choice on the corpus files. Lossy WebP's worst tile is
not monotonic in quality (0.924 at q50, 0.890 at q55 and q60); the search below handles that.

### The search is not plain bisection (2026-10-24, found by the release matrix)
On the harness's GDI+ photograph, Balanced reported "Already optimized" although q82 passed every gate: bisection
tested q85 first, which failed the edge gate (0.9465 against 0.950), and never looked lower. A failure is now checked one
quality lower before the search turns back, and when nothing has passed, every small-enough quality that bisection
skipped is tried before the answer is "already optimized" (`CompressionSearchTests`). The sample now gets q82 at
Balanced. Lossy WebP at Smallest went from q65 (72.3 %) in the plain bisection to q50 (77.9 %).

## Why each axis is needed
These numbers come from the product's `Fidelity` (`CompressionMeasurement.Measure_fidelity_axes`).

| Case | Luma | Worst 1 % | Chroma | ΔE mean | ΔE99 | Edge | What only one axis sees |
|---|---|---|---|---|---|---|---|
| screenshot.jpg q90 | 0.9905 | **0.88** | 0.998 | 0.65 | 3.65 | 0.994 | ringing text, visible only to the worst-region axis |
| screenshot.jpg q82 | 0.9820 | **0.80** | 0.996 | 1.01 | 5.45 | 0.985 | it passes Balanced on luma alone |
| camera.jpg 4:2:0 q97 | 0.9985 | 0.997 | **0.986** | 1.12 | 4.45 | 0.998 | 4:4:4 → 4:2:0, visible only to chroma |
| camera.jpg 4:2:0 q50 | 0.9660 | 0.914 | 0.979 | 1.94 | **10.95** | 0.916 | local colour error at Smallest |
| moderate.jpg q55 | 0.9857 | 0.953 | 0.992 | 1.28 | **7.40** | 0.963 | a 9.9 % saving refused at Balanced |

The full table (13 qualities × 2 chroma modes × 7 files) is reproduced by `Measure_fidelity_axes`. Measurements run only when asked: set `WINTOOLS_MEASURE=1` and run `dotnet test tests/WinTools.Worker.Tests -c Release --filter "FullyQualifiedName~CompressionMeasurement" --logger "console;verbosity=detailed"`.

## Time per Compress, 2 MP corpus files
| File | High | Balanced | Smallest |
|---|---|---|---|
| phone.jpg | 503 ms | 371 ms | 273 ms |
| camera.jpg (two chroma modes) | 555 ms | 586 ms | 655 ms |
| moderate.jpg | 69 ms | 75 ms | 215 ms |
| noisy.jpg | 196 ms | 271 ms | 281 ms |
| screenshot.jpg | 280 ms | 270 ms | 427 ms |
| photo-lossy.webp | 724 ms | 1013 ms | 927 ms |
| screenshot-lossless.webp | 708 ms | 800 ms | 861 ms |

V1 ran one encode per file and had no fidelity check; its time was not measured in this run.

**Large images: 48 MP (8000×6000), against the host's 60 s job timeout.**

| Source | High | Balanced | Smallest |
|---|---|---|---|
| 4:2:0 q93 | 5.2 s | 6.8 s | 7.0 s |
| 4:4:4 q97 | 11.1 s | 11.5 s | 13.9 s |

Re-measured 2026-10-24 with the 32×32 tile gates and the final search, on an otherwise idle machine: 4:2:0 3.7, 7.1 and
6.2 s; 4:4:4 14.0, 16.3 and 10.0 s.
The spread between the two runs is itself a reason for the search deadline below.

**Deadline.** The search stops after 25 s and uses the best copy found so far, which has passed every gate. With none
found yet, the job fails with "It took too long and was stopped" and writes nothing; it never claims "already
optimized" (`CompressionDeadlineTests`, fake clock).

**Four 48 MP 4:4:4 files at once, through the real worker processes and the 60 s job timeout (12 logical processors):**

| Lanes | High | Smallest |
|---|---|---|
| host default | 37.0 s for 4, all written | 29.5 s for 4, all written |
| 4 | 28.8 s for 4, all written | 26.0 s for 4, all written |

(First measured with 64×64 tiles at 27.0, 39.9, 20.3 and 26.2 s; the run-to-run spread on this machine is of that order.)

No job came near its timeout on this machine. On a slower processor the deadline, not the kill, decides.

## Fidelity cost
- **2 MP:** about 35 ms per comparison, in parallel bands. The first single-threaded version took about 250 ms.
- **48 MP:** 1.06 s per comparison and 32.5 MB allocated with the 16×16 cell sums (9.4 MB before any tiles; a first version that gave every band sums for the whole image allocated 100.9 MB). Each band keeps only the cell rows it touches; the two images being compared are each a whole buffer.

## Not yet measured
- Real photographs, faces, skin tones and real screenshots. The gates await the owner's visual validation (ADR-0017 Consequences).
- Other machines: the times above come from one 12-thread desktop.
