# Worker memory per operation and image size (2026-10-21)

Machine: the development PC (Windows 11 Pro 26200, 15.7 GB RAM, 12 logical processors). Release worker, one job per process, driven over its stdin protocol by a local script. The peak commit is `PeakPagefileUsage` of the worker process after the job.

Inputs were generated (a smooth noise pattern), not personal files: JPEG quality 90 and PNG at compression level 1, with an aspect ratio of 4:3.

## 25 MP (100 MB of BGRA pixels)
| Operation | From JPEG: peak commit | From PNG: peak commit | Time from JPEG / PNG |
|---|---|---|---|
| To PNG | 302 MB | — | 1.3 s |
| To JPEG | — | 272 MB | 0.8 s |
| To WebP | 241 MB | 273 MB | 2.2 / 2.5 s |
| To TIFF | 352 MB | 383 MB | 2.3 / 2.5 s |
| To BMP | 504 MB | **536 MB** | 0.6 / 0.9 s |
| Resize 50 % | 325 MB | 358 MB | 0.5 / 0.9 s |
| Rotate right | 343 MB | 425 MB | 0.7 / 1.9 s |
| Compress (smallest) | 224 MB | — | 0.4 s |
| Remove metadata (nothing to remove) | 113 MB | 160 MB | 0.2 / 0.5 s |
| Image info | 113 MB | 160 MB | 0.2 / 0.5 s |

## Scaling, from PNG
| Operation | 50 MP | 64 MP |
|---|---|---|
| To BMP | 1,056 MB | 1,345 MB |
| Rotate right | 838 MB | 1,114 MB |
| To TIFF | 774 MB | 960 MB |
| Resize 50 % | 708 MB | 902 MB |
| To WebP | 539 MB (12.5 s) | 685 MB (18.0 s) |

The peak grows linearly with the pixel count: about 21 MB per megapixel in the worst case (BMP output, about 5.3 times the BGRA buffer). The rotated and validated copies of the image, and the encoded output read back for validation, all exist at the same time.

## Consequences (ADR-0015)
- At the old limit of 100 MP a single worker would need about 2.1 GB, and four parallel workers about 8.6 GB. During this measurement only 4.3 GB of physical memory was available on a 16 GB machine.
- The pixel limit is now 50 MP: 1.06 GB at most per worker.
- The per-worker cap is 1.5 GB (job object, with the same GC heap limit).
- The number of parallel workers is bounded by the available commit, less 1 GB, divided by the cap.
- `ResourceLimitTests.An_image_at_the_pixel_limit_is_processed_under_the_worker_memory_cap` runs the costliest case at exactly 50 MP (BMP → BMP rotation) under the cap. `A_worker_cannot_commit_past_the_memory_cap` shows that the cap stops native allocations. Without the cap, that test fails.
- The slowest case at the limit, WebP at 50 MP, took 12.5 s, within the 60 s job timeout.
