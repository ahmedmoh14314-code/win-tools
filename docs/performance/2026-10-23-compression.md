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
| phone.jpg | −14.4 %, 0.9920 (q88) | −63.9 %, 0.9811 (q70) | −73.5 %, 0.9732 (q50) |
| camera.jpg | −13.6 %, 0.9908 (q95, **4:4:4 kept**) | −67.9 %, 0.9812 (q88, 4:4:4) | −88.7 %, 0.9715 (q65, 4:2:0) |
| moderate.jpg | already optimised | already optimised | −18.2 %, 0.9797 (q50) |
| noisy.jpg | already optimised | already optimised | −9.4 %, 0.9623 (q88) |
| screenshot.jpg | already optimised | −10.8 %, 0.9962 (q93) | −27.1 %, 0.9910 (q88) |
| photo-lossy.webp | −23.9 %, 0.9914 | −61.7 %, 0.9813 | −77.9 %, 0.9702 |
| screenshot-lossless.webp | already optimised | already optimised | already optimised |
| unoptimized.png (lossless) | | | −99.6 %, identical pixels |
| optimized.png (lossless) | | | already optimised |

Every written copy meets its preset's luma floor (0.990, 0.980 or 0.960) and saves at least 8 %. The product's own gates also hold on the other five axes.

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

## Fidelity cost
- **2 MP:** about 35 ms per comparison, in parallel bands. The first single-threaded version took about 250 ms.
- **48 MP:** 0.93 s per comparison and 9.4 MB allocated. Memory is a few rows and a histogram per band, never a plane.

## Not yet measured
- Real photographs, faces, skin tones and real screenshots. The gates await the owner's visual validation (ADR-0017 Consequences).
- Batch throughput with 4 workers, each running the parallel fidelity computation.
