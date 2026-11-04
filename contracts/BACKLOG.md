# Backlog (post test-prod, required before production)

| # | Item | Owner | Why |
|---|---|---|---|
| 1 | Retention purge for `results.detection_counts` and `results.recognitions` (and `snapshots` bucket lifecycle) | dotnet-a | Tables grow forever |
| 2 | StreamHealth HA (state rebuild from `camera.status` replay or partitioned instances) | dotnet-a | Single replica, in-memory state |
| 3 | Snapshot copy retry queue (currently gives up after 3 retries) | dotnet-a | Lost snapshots |
| 4 | SignalR sticky routing / backplane when realtime-gateway > 1 replica | dotnet-b | Multi-instance UI connections |
| 5 | Engines on GPU: Triton/ONNX Runtime CUDA with dynamic batching | ai | 1,200 frames/s needs GPUs |
| 6 | Camera substreams / hw decode (`FFMPEG_HWACCEL`) for 400 cameras | ingest | ~50-60 cores at 1080p software decode |
| 7 | Planner: one camera = one plan; consider multi-plan per camera | planner | Product decision |
| 8 | Auth (UI, API gateway, MediaMTX) | dotnet-b / infra | No auth in test-prod |
| 10 | `GET /api/recognitions?q=` server-side plate search | dotnet-a | UI searches client-side only |
| 11 | Persons (face enrollment) admin page in UI | ui | Enrollment only via engine API now |
| 12 | `snapshot_url` in the live `recognition` event (realtime-gateway) | dotnet-b | UI derives it today |
| 13 | Kafka consumer-lag exporter (e.g. kminion) for the Grafana lag panel | infra | Redpanda 24.2 has no lag metric |
| 14 | Face gallery shared store (Postgres/pgvector) so `face` can run >1 replica | ai | Gallery is a local JSON file |
| 15 | Trim images: .NET publish ships all native runtimes (~100 MB: arm64/win/osx); Python images 530-900 MB (slim deps, ffmpeg static) | dotnet-a/b, ai, ingest | Pull time / disk at 400-camera scale |
| 16 | Object store CPU: SeaweedFS measured ~2.5 cores for ~65 PUT/s + ~13 GET/s of ~50 KB → benchmark/tune (or Ceph RGW / MinIO AIStor) before 1,200 frames/s | infra | Would need dozens of cores at 400 cameras |
| 17 | Detector model per deployment: local CPU test runs YOLOX-Tiny (231 ms vs 919 ms YOLOX-S under load); production YOLOX-S on GPU | ai | Accuracy 32.8 vs 40.5 mAP |
| 18 | Executor: acquire the first-step engine slot *before* binding a camera's newest frame (late binding), instead of relying on `MAX_INFLIGHT_FRAMES` ≈ engine capacity. Measured locally: in-flight 32 → p50 detection age 3.9 s; 6 → 1.18 s (p90 1.46 s) | dotnet-b | Latency under saturation |
| 19 | Host sizing: Windows pagefile grew to 17 GB and Docker's VHDX to 39 GB (not sparse) on the laptop; set `.wslconfig` memory cap and compact the VHDX | infra / user | Disk filled to ~2 GB free twice |
| 20 | Semantic plan cache: match *similar* queries via embeddings (e.g. nomic-embed-text in Ollama + pgvector) with a confirm step in the UI; exact-normalized cache exists | planner / ui | Paraphrased queries still call the LLM |
| 9 | Load test at 400 simulated cameras on a server | manager | Validate sizing |
| 21 | Registry restart reshuffles cameras (lease 15 s < restart time): add a startup grace period (workers keep their cameras until they re-register; no rebalance for ~60 s after start) | dotnet | Measured v1.9 deploy: ~29 of 31 cameras moved and briefly stopped |
| 22 | `PUT /api/cameras/{id}` replaces the whole record (a partial body nulls `plan_id`): add `PATCH` with merge semantics; UI already sends the full record | dotnet | Found by Perf-bench (v1.9) |
| 23 | Motion gate: per-camera ignore zones / learned noise floor; in-process motion detector (OpenCV) once the worker owns raw frames; re-check `skip_loop_filter` quality on more images and bitrates | ingest | Benchmark covered 3 images, 1 bitrate; mpdecimate has no regions |
| 24 | Executor admits only one in-flight frame per camera; a watched camera analyzes 0.4-1 frames/s under load: allow 2 concurrent runs for watched cameras (pipelining) and raise `MAX_INFLIGHT_FRAMES` after measuring | dotnet | v1.8 measurement: 0.37/s watched |
