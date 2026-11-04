# Scale report

Generated 2026-10-22 08:54 from 1 step(s). Raw JSON: `qa/perf/results/`.

## 1. Per-step results

| metric | 21 cams |
|---|---|
| ingest fps p5 / p50 / min (online cams) | 0.86 / 0.93 / 0.8 |
| online cameras < 2.4 fps | 19 |
| frames.raw produce rate (fps) | 24.39 |
| dropped frames (window) | 889.0 |
| ffmpeg restarts (window) | 3.0 |
| camera states (extractor) | {'online': 19, 'reconnecting': 1, 'error': 1} |
| health summary | {'total': 21, 'online': 16, 'degraded': 5, 'offline': 0, 'unassigned': 0} |
| time to assign new cams p50 / max (s) | None / None |
| workers / utilization | 0 / [] |
| docker total CPU avg (% of 1 core) / mem (MiB) | 1089.62 / 3718.0 |
| Docker VM mem total (MiB) | 7942 |
| host free RAM min (MiB) / C: free min (MiB) | 2380 / 4245 |
| executor lag first -> last (max) | 83 -> 83 (83) |
| frames.raw produce msgs/s / MB/s / avg KB | 24.34 / 0.01 / 0.42 |
| redpanda CPU avg/max % ; mem max MiB | 57.18 / 57.18 ; 491.0 |
| redpanda data dir (MiB) first -> last | 4810 -> 4810 |
| catalog lag last (max); records; img req | 58 (58); 12600.0; {'ok': 0, 'expired': 0, 'unknown': 0, 'error': 0} |
| detection age first p50/p95 (s) | 3.69 / 8.75 |
| detection age final p50/p95 (s) | 4.25 / 9.65 |
| autoscaler current/desired | 3.0 / 3.0 |
| executor ok / superseded / stale (per s) | 1.13 / 13.97 / 11.07 |
| executor step mean (ms) | 2055.7 |
| realtime-gateway lag detections (ms) | 265.0 |
| OOM kills / container restarts | [] / {} |
| failure criteria hit | ['19 online cameras < 2.4 fps'] |
| UI Live wall 4x4 | manual check |

## 2. Top containers by CPU at 21 cameras

| container | CPU avg % | CPU max % | mem max MiB | limit MiB | mem % of limit |
|---|---|---|---|---|---|
| vision-pipeline-frame-extractor-1-1 | 206.26 | 206.26 | 429.4 | 512.0 | 84 |
| vision-pipeline-detector-1 | 153.18 | 153.18 | 207.0 | 512.0 | 40 |
| vision-pipeline-lpr-1 | 143.85 | 143.85 | 192.6 | 384.0 | 50 |
| vision-pipeline-sim-cameras-1 | 131.32 | 131.32 | 509.6 | 768.0 | 66 |
| vision-pipeline-frame-extractor-2-1 | 122.57 | 122.57 | 337.0 | 512.0 | 66 |
| vision-pipeline-minio-1 | 101.41 | 101.41 | 152.5 | 256.0 | 60 |
| vision-pipeline-mediamtx-1 | 64.15 | 64.15 | 46.4 | 128.0 | 36 |
| vision-pipeline-redpanda-1 | 57.18 | 57.18 | 491.0 | 1280.0 | 38 |
| vision-pipeline-face-1 | 52.4 | 52.4 | 299.6 | 384.0 | 78 |
| vision-pipeline-executor-1 | 15.51 | 15.51 | 55.5 | 128.0 | 43 |
| vision-pipeline-api-gateway-1 | 8.29 | 8.29 | 44.6 | 128.0 | 35 |
| vision-pipeline-frame-catalog-1 | 6.44 | 6.44 | 40.5 | 128.0 | 32 |

## 3. First resource to saturate (share of capacity, last step)

* 206% - CPU of `vision-pipeline-frame-extractor-1-1` pinned at 206.26% (single-threaded/limit bound)
* 153% - CPU of `vision-pipeline-detector-1` pinned at 153.18% (single-threaded/limit bound)
* 144% - CPU of `vision-pipeline-lpr-1` pinned at 143.85% (single-threaded/limit bound)
* 131% - CPU of `vision-pipeline-sim-cameras-1` pinned at 131.32% (single-threaded/limit bound)
* 123% - CPU of `vision-pipeline-frame-extractor-2-1` pinned at 122.57% (single-threaded/limit bound)
* 101% - CPU of `vision-pipeline-minio-1` pinned at 101.41% (single-threaded/limit bound)

## 4. Per-camera cost (slope first -> last step) and 400-camera extrapolation

Need at least two steps with different camera counts for slopes.

## 5. Notes

* CPU = `docker stats` (100% = one core), averaged over samples in the measure window; fps/rates from Prometheus over the same window.
* Detection age is not exported as a metric; proxies are executor stale/superseded rates and realtime-gateway consumer lag.
* UI Live wall 4x4 must be checked by eye.
