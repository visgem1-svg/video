# Executor (port 5003)

Consumes `frames.raw` (group `executor`), keeps `camera.config` + `pipeline.plans` (compacted, read from the beginning
at start; `/health` is 503 until loaded) in memory, runs each frame through its plan and publishes `detections`,
`recognitions`, `jobs.heavy` and `pipeline.metrics`. A second loop consumes `jobs.heavy` (group `executor-heavy`).
Endpoints: `/health`, `/metrics` only.

## Design
* **Parallelism**: at most `MAX_INFLIGHT_FRAMES` frames in flight (semaphore; when full the consume loop stops polling =
  back-pressure to Kafka). Per-tool `SemaphoreSlim` (`TOOL_CONCURRENCY_<TOOL>`) protects the engines.
  Engine calls: pooled `SocketsHttpHandler` per tool, Polly pipeline (attempt timeout, 1 retry w/ jitter, circuit breaker per tool).
* **Image (contract 9.7)**: the JPEG is the `frames.raw` message value (meta in header `meta`), copied once into an ArrayPool buffer and sent to engines via `ReadOnlyMemoryContent`. No S3 GET. A recognition that passes the cooldown is PUT to `snapshots/{type}/{yyyyMMdd}/{id}.jpg` (`SNAPSHOT_CONCURRENCY`=8, one retry; failure -> `snapshot_key:null`, `vp_executor_snapshot_failures_total`). `jobs.heavy`: value = JPEG, header `job` = job JSON (topic needs `max.message.bytes` >= 2 MB).
  `ReadOnlyMemoryContent` (no copies), returned to the pool after the last step.
* **Conflation (live latency)**: the consume loop never blocks; each camera has one mailbox slot, a newer frame replaces the waiting one (`reason="superseded"`, completed at once so offsets stay contiguous), cameras are served round-robin as slots free up, one frame in flight per camera.
* **Live back-pressure**: when a frame is picked up (after getting an in-flight slot) and `now - ts > MAX_FRAME_AGE_MS`
  it is skipped (`vp_executor_frames_skipped_total{reason="stale"}`); catching up after downtime is therefore cheap.
* **Offsets**: auto-offset-store is OFF. Frames finish out of order, so `OffsetTracker` keeps a per-partition queue and
  stores `highest contiguous completed offset + 1` (librdkafka auto-commits stored offsets every 1 s). A slow early frame
  holds the commit back, so a crash can only cause re-processing (consumers are idempotent), never loss. Skipped,
  unparsable and failed frames count as completed. Revoked partitions drop their tracker.
* **Plans**: immutable records; swap = one dictionary write; a frame resolves its plan once and finishes on it. Only
  `active` plans run; missing/disabled/draft plan or `plan_id: null` -> default plan; `enabled:false` camera -> skipped.
* **queue mode**: step emits a `jobs.heavy` message (key camera_id, context = earlier step results); the heavy loop runs
  it (own limit `HEAVY_CONCURRENCY`) and publishes a *partial* `detections` message (only that step's output, same
  `frame_id`) plus its recognitions.

## Environment (names per infra/ENV.md)
| Var | Default | |
|---|---|---|
| `KAFKA_BOOTSTRAP` | `redpanda:9092` | |
| `ASPNETCORE_URLS` | `http://+:5003` | |
| `EXECUTOR_ID` | `ex-<hostname>` | |
| `MINIO_ENDPOINT` `MINIO_ACCESS_KEY` `MINIO_SECRET_KEY` `MINIO_USE_SSL` `MINIO_SNAPSHOTS_BUCKET` | `minio:9000` `minio` `minio12345` `false` `frames` | `MINIO_REGION` (`us-east-1`) |
| `DETECTOR_URL` `LPR_URL` `OCR_URL` `FACE_URL` | tools.json hosts | base URL; path = tool name |
| `TOOL_URL_<TOOL>` | derived | full URL override, e.g. `TOOL_URL_DETECT_OBJECTS` |
| `MAX_INFLIGHT_FRAMES` | 32 | |
| `MAX_FRAME_AGE_MS` | 1500 | |
| `TOOL_CONCURRENCY_<TOOL>` | detect_objects 8, read_plate 4, read_text 2, detect_faces 4, identify_faces 4 | |
| `TOOL_TIMEOUT_MS_<TOOL>` | 4000/5000/10000/4000/5000 | per attempt |
| `TOOL_RETRIES` | 1 | |
| `FRAME_TIMEOUT_MS` | 15000 | whole-frame deadline |
| `HEAVY_CONCURRENCY` / `HEAVY_MAX_AGE_MS` | 4 / 60000 | |
| `RECOGNITION_COOLDOWN_S` / `UNKNOWN_FACE_COOLDOWN_S` | 30 / 10 | one recognition per (camera,type,value) per window; unknown faces per camera |
| `METRICS_WINDOW_S` | 5 | pipeline.metrics period |
| `DRAIN_TIMEOUT_MS` | 10000 | graceful shutdown drain |

Metrics: `vp_executor_step_duration_seconds{tool}`, `vp_executor_tool_wait_seconds{tool}`,
`vp_executor_frames_total{result}`, `vp_executor_frames_skipped_total{reason}`, `vp_executor_inflight`,
`vp_executor_heavy_jobs_total{result}`, `vp_executor_heavy_inflight`.

## Build / test
```
dotnet build Executor/Executor.csproj
dotnet test Executor.Tests
docker build -f Executor/Dockerfile services/dotnet
```
