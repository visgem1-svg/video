# Scale validation plan — 100 live cameras (laptop), extrapolated to 400+ (server)

Owner: manager. Goal (user's top priority): prove that ingest scales horizontally — many live, different,
sometimes unstable RTSP streams, spread over many workers — and find each resource's limit on this laptop.
Analysis throughput is NOT the goal here (CPU-only, conflation keeps it live).

## Test bed
* **100 simulated cameras `cam-001..cam-100`**, all different streams:
  * content: ≥ 12 distinct base clips (existing test images + additional CC0/public-domain street, parking,
    traffic, lobby images), each camera a different clip/offset/pan speed/zoom/mirror/color shift;
  * properties mix: 70 % 960×540@15, 15 % 1280×720@25, 15 % 640×360@10; GOP 1 s; H.264 baseline/main;
  * 5 % unstable (killed/restarted every 30-90 s, some stay down 20-60 s).
* MediaMTX path regex for publishers `~^cam-[0-9]{3}$` (no per-path config).
* Cameras registered through the registry API (script, idempotent); 20 keep the active plan, 80 use the default plan.
* Workers: static fx-1/fx-2 + worker-controller (autoscaler) adds `fx-auto-N` (capacity 12) → expect ~9 workers.

## Laptop settings for the test
* extractor `FFMPEG_THREADS=1`, `JPEG_MAX_WIDTH=640`; frames TTL 5 min; OCR engine stopped (unused by plans).
* Object store: benchmark first (SeaweedFS tuned vs Garage) at 300 PUT/s × 30 KB + 30 GET/s; pick the one with
  lower CPU at p99 PUT < 100 ms.

## Ramp & measurements (each step: settle 2 min, measure 3 min)
Steps: 20 → 50 → 80 → 100 cameras.
Per step record: per-camera ingest fps (p5/p50/min), dropped frames, ffmpeg restarts, camera states, time to
assign new cameras, worker count/utilization, per-container CPU & memory (docker stats), Docker VM total memory,
host free RAM + C: free, Kafka `frames.raw` produce rate + executor consumer lag, object store PUT p50/p99 + CPU,
executor processed/superseded/stale, detection age (first/final), realtime-gateway lag, UI Live wall 4×4 check.
Failure criteria: any camera < 2.4 fps while its source is healthy, OOM kills, restarts not caused by the
unstable cameras, assignment > 30 s, consumer lag growing.

## Output
`qa/perf/SCALE-REPORT.md`: table per step, the first resource to saturate, per-camera cost (CPU, memory,
bandwidth, storage), and the extrapolated sizing for 400 cameras (workers, cores, RAM, object store, Kafka).
