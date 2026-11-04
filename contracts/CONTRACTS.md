# Vision Pipeline — System Contracts (v1)

Owner: manager. **Teams must not change this file.** If a contract is wrong or missing,
say so in your final report and propose the change; do not invent a private variant.

Goal: live video analytics for **400+ cameras at 3 fps analysis** (1,200 frames/s), with
LLM-built pipelines (query → DAG). Local test-prod runs on one laptop (8 cores, 15 GB RAM,
no GPU) with ~20 simulated cameras; the design must scale horizontally to 400+ unchanged.

## 1. Hard rules (all teams)

1. One responsibility per service. No shared databases between services (one Postgres
   instance, **one database per service**).
2. Images never travel in Kafka. Frames go to MinIO; Kafka carries a reference (`object_key`).
3. Every service: JSON structured logs to stdout, `GET /health` (200 when ready), Prometheus
   `GET /metrics`, config only via environment variables (document them in the service README),
   graceful shutdown on SIGTERM, and a `Dockerfile` (multi-stage, slim/alpine/chiseled base,
   non-root user).
4. Memory budgets are part of the contract (section 9). Stay inside them.
5. Every Kafka consumer is idempotent (frames can be redelivered). Commit offsets only after
   the work for that message is done.
6. Kafka message keys = `camera_id` (keeps per-camera ordering) unless stated otherwise.
7. Timestamps: `ts` = Unix epoch **milliseconds** (int64), UTC. IDs: UUID v4 strings.
   JSON field names: `snake_case`.
8. Do not start Docker or `docker compose up` during development; integration is done by the
   manager. Unit/integration tests must run without Docker (mock Kafka/MinIO/HTTP).

## 2. Repository layout

```
vision-pipeline/
  contracts/            CONTRACTS.md, tools.json, plan.schema.json        (manager)
  infra/                docker-compose.yml, configs, sim-cameras          (team infra)
  services/
    frame-extractor/    Python                                            (team ingest)
    engines/
      detector/ lpr/ ocr/ face/   Python FastAPI                          (team ai)
    planner/            Python FastAPI                                    (team planner)
    dotnet/
      Registry/ StreamHealth/ Results/                                    (team dotnet-a)
      Executor/ RealtimeGateway/ ApiGateway/                              (team dotnet-b)
  ui/                   React + TypeScript                                (team ui)
  models/               shared ONNX model files (read-only)
  test-images/          sample images (read-only)
```

Each team writes **only inside its own folders**.

## 3. Network: compose service names and ports

| Service (compose name) | Port | Notes |
|---|---|---|
| `redpanda` | 9092 (internal), 19092 (host) | Kafka API |
| `redpanda-console` | 8080 | Kafka UI |
| `minio` | 9000 API, 9001 console | user `minio` / `minio12345` |
| `postgres` | 5432 | user `vp` / `vp`; databases `registry`, `planner`, `results` |
| `mediamtx` | 8554 RTSP, 8889 WebRTC (WHEP), 9997 API, 9998 metrics | |
| `sim-cameras` | — | publishes `rtsp://mediamtx:8554/cam-001` … `cam-020` |
| `prometheus` | 9090 | |
| `grafana` | 3000 | anonymous viewer enabled |
| `ollama` | 11434 | runs on the **host** in local test (`host.docker.internal:11434`) |
| `registry` | 5001 | .NET |
| `stream-health` | 5002 | .NET |
| `executor` | 5003 | .NET (only /health, /metrics) |
| `results` | 5004 | .NET |
| `realtime-gateway` | 5005 | .NET SignalR |
| `api-gateway` | 8000 | .NET YARP — the **only** backend entry point for the UI |
| `planner` | 8010 | Python |
| `detector` | 8101 | Python |
| `lpr` | 8102 | Python |
| `ocr` | 8103 | Python |
| `face` | 8104 | Python |
| `frame-extractor` | 9101 (metrics/health) | Python, N replicas |
| `ui` | 5173 (dev), 80 (container) | |

Inside compose, services address each other by compose name (e.g. `http://detector:8101`).

## 4. MinIO

| Bucket | Content | Key format | Retention |
|---|---|---|---|
| `frames` | analysis JPEGs (max 960 px wide, quality 80) | `{camera_id}/{yyyyMMddHH}/{frame_id}.jpg` | 1 day (lifecycle) |
| `snapshots` | recognition frames (copied from `frames`) | `{type}/{yyyyMMdd}/{recognition_id}.jpg` | kept |

## 5. Kafka topics

| Topic | Partitions | Cleanup | Key | Producer → Consumers |
|---|---|---|---|---|
| `camera.config` | 12 | compact | camera_id | registry → frame-extractor, executor, stream-health |
| `frames.raw` | 48 | delete, 1h | camera_id | frame-extractor → executor |
| `detections` | 24 | delete, 6h | camera_id | executor → realtime-gateway, results |
| `recognitions` | 12 | delete, 7d | camera_id | executor → results, realtime-gateway |
| `camera.status` | 12 | delete, 1h | camera_id | frame-extractor → stream-health |
| `camera.health` | 12 | compact | camera_id | stream-health → realtime-gateway |
| `pipeline.plans` | 6 | compact | plan_id | planner → executor, realtime-gateway |
| `pipeline.metrics` | 6 | delete, 1h | plan_id | executor → realtime-gateway |
| `jobs.heavy` | 12 | delete, 6h | camera_id | executor → executor (heavy-step workers) |

Consumer group ids = the service name (`executor`, `results`, `realtime-gateway`, ...).
Exception: `realtime-gateway` instances each use a unique group id (every instance needs all events).

### 5.1 `camera.config` (value = full camera; tombstone = deleted)
```json
{ "camera_id": "cam-001", "name": "Gate 3", "source_url": "rtsp://mediamtx:8554/cam-001",
  "analysis_fps": 3, "enabled": true, "plan_id": "a6f...", "tags": ["gate"], "updated_at": 1761030000000 }
```
`plan_id` null → executor uses the built-in default plan (`detect_objects` only).

### 5.2 `frames.raw`
```json
{ "frame_id": "uuid", "camera_id": "cam-001", "ts": 1761030000123, "seq": 1042,
  "width": 960, "height": 540, "object_key": "cam-001/2026102109/uuid.jpg", "worker_id": "fx-1" }
```

### 5.3 `detections` (one per analyzed frame)
```json
{ "frame_id": "uuid", "camera_id": "cam-001", "ts": 1761030000123, "width": 960, "height": 540,
  "plan_id": "uuid|default", "plan_version": 3,
  "objects": [ { "class": "truck", "confidence": 0.91, "box": [x1, y1, x2, y2] } ],
  "plates":  [ { "plate": "12345678", "confidence": 0.95, "box": [x1, y1, x2, y2] } ],
  "faces":   [ { "box": [x1, y1, x2, y2], "confidence": 0.9, "person_id": "p-12|null", "person_name": "Dana|null", "score": 0.71 } ],
  "texts":   [ { "text": "GATE 3", "confidence": 0.88, "box": [x1, y1, x2, y2] } ],
  "timings_ms": { "s1": 120.5, "s2": 33.1 }, "total_ms": 160.2, "executor_id": "ex-1" }
```
Boxes are in **pixels of the analysis frame** (`width` × `height`); the UI scales them.

### 5.4 `recognitions` (emitted for every plate read and every face identified/detected by a plan step)
```json
{ "recognition_id": "uuid", "type": "plate|face", "camera_id": "cam-001", "frame_id": "uuid",
  "ts": 1761030000123, "value": "12345678 | person name or 'unknown'", "person_id": "p-12|null",
  "confidence": 0.95, "box": [x1, y1, x2, y2], "frame_key": "cam-001/2026102109/uuid.jpg",
  "plan_id": "uuid" }
```
`results` copies `frame_key` into the `snapshots` bucket and stores `snapshot_key`.

### 5.5 `camera.status` (frame-extractor, every 5 s per camera and on every state change)
```json
{ "camera_id": "cam-001", "worker_id": "fx-1", "state": "starting|online|stalled|reconnecting|offline|error",
  "fps_actual": 2.9, "last_frame_ts": 1761030000123, "reconnects": 2, "error": null, "ts": 1761030000500 }
```
`stalled` = process alive but no frame for > 5 s. `reconnecting` = restarting with backoff
(1 s, 2 s, 4 s … max 30 s, with jitter).

### 5.6 `camera.health` (stream-health, compacted latest view per camera)
```json
{ "camera_id": "cam-001", "name": "Gate 3", "state": "online|degraded|offline|unassigned",
  "worker_id": "fx-1", "fps_actual": 2.9, "fps_target": 3, "last_frame_ts": 1761030000123,
  "reconnects_1h": 2, "uptime_pct_1h": 99.2, "last_error": null, "ts": 1761030000600 }
```
`degraded` = online but fps_actual < 70 % of target, or ≥ 3 reconnects in 10 min.
`offline` = no status for > 15 s, or state offline/error.

### 5.7 `pipeline.plans` — value follows `contracts/plan.schema.json`; tombstone = deleted.

### 5.8 `pipeline.metrics` (executor, every 5 s per plan step)
```json
{ "plan_id": "uuid", "step_id": "s2", "tool": "read_plate", "window_s": 5, "processed": 41,
  "skipped": 120, "errors": 0, "p50_ms": 31.0, "p95_ms": 55.2, "executor_id": "ex-1", "ts": 1761030005000 }
```

### 5.9 `jobs.heavy` — `{ "job_id", "frame": <frames.raw message>, "plan_id", "plan_version", "step_id", "context": {<results of earlier steps>} }`

## 6. Pipeline plans (DAG)

Schema: `contracts/plan.schema.json`. Tools: `contracts/tools.json`.

* Steps run in array order. A step may only reference earlier steps.
* `when` condition types (closed list): `always`, `found {class, min_confidence?}`,
  `count {class, gt}`, `failed {step}`. Missing `when` = `always`.
* `mode`: `inline` (executor calls the engine synchronously) or `queue` (executor publishes
  `jobs.heavy`; a heavy-step consumer runs it and emits detections/recognitions).
* `params` are passed to the tool as form fields (e.g. `classes`, `min_confidence`, `zone`).
* Max 8 steps. Only tools in `tools.json`. The validator (planner) rejects anything else.
* Default plan (`plan_id: "default"`): `[ {id:"s1", tool:"detect_objects", mode:"inline"} ]`.

## 7. HTTP APIs

### 7.1 AI engines (all: `multipart/form-data`, image field `image`, JSON response with `ms`)
| Service | Route | Extra fields | Response |
|---|---|---|---|
| detector | `POST /detect_objects` | `classes` (comma list, optional), `min_confidence` | `{objects:[{class,confidence,box}], count, ms}` |
| lpr | `POST /read_plate` | — | `{plates:[{plate,confidence,detection_confidence,box}], count, ms}` |
| ocr | `POST /read_text` | — | `{lines:[{text,confidence,box}], count, ms}` (box = `[x1,y1,x2,y2]`) |
| face | `POST /detect_faces` | — | `{faces:[{box,confidence}], count, ms}` |
| face | `POST /identify_faces` | `threshold` (default 0.363) | `{faces:[{box,confidence,person_id,person_name,score}], count, ms}` |
| face | `POST /persons` | `name`, `image` | `{person_id, name}` (enroll; stored in a local volume) |
| face | `GET /persons`, `DELETE /persons/{id}` | | |

Engines may also accept `image_key` (MinIO key) instead of `image` — optional, later.

### 7.2 Registry (`/api/cameras`, `/api/workers`)
* `GET /api/cameras` · `GET /api/cameras/{id}` · `POST /api/cameras` · `PUT /api/cameras/{id}` · `DELETE /api/cameras/{id}`
  (body = camera from 5.1 without `updated_at`). Every change is published to `camera.config`.
* `POST /api/workers/{worker_id}/heartbeat` body `{ "capacity": 25, "running": ["cam-001", ...] }`
  → `200 { "assigned": [ <camera config>, ... ], "lease_s": 15 }`.
  The registry is the **only** place that decides which worker owns which camera. Workers that
  miss heartbeats for `lease_s` lose their cameras, which are rebalanced to live workers
  (least-loaded first, respecting `capacity`). Assignment must be sticky (don't move cameras
  between healthy workers).
* `GET /api/workers` → `[ { worker_id, capacity, assigned: [...], last_seen } ]`

### 7.3 Stream health — `GET /api/health/cameras` → `[camera.health]` · `GET /api/health/summary` → `{ total, online, degraded, offline, unassigned }`

### 7.4 Results
* `GET /api/recognitions?camera_id=&type=&since=&limit=50` → newest first, with `snapshot_url`
* `GET /api/detections/stats?camera_id=&minutes=60` → per-class counts per minute
* `GET /api/snapshots/{type}/{yyyyMMdd}/{id}.jpg` → image bytes (streamed from MinIO)

### 7.5 Planner
* `POST /api/plans/draft` `{ "query": "...", "camera_ids": ["cam-001"] }` →
  `{ "plan": <plan>, "valid": true, "errors": [], "explanation": "..." }` (does not activate)
* `POST /api/plans` `<plan>` → validates, stores (status `active`), publishes `pipeline.plans`,
  then sets `plan_id` on the cameras through the registry API.
* `GET /api/plans` · `GET /api/plans/{id}` · `DELETE /api/plans/{id}` (tombstone + clear cameras)
* `GET /api/tools` → contents of `tools.json`

### 7.6 API gateway (YARP, port 8000) — routes, CORS for `http://localhost:5173`
`/api/cameras/**`, `/api/workers/**` → registry · `/api/health/**` → stream-health ·
`/api/recognitions/**`, `/api/detections/**`, `/api/snapshots/**` → results ·
`/api/plans/**`, `/api/tools` → planner · `/hubs/**` (WebSockets) → realtime-gateway ·
`/media/**` → `mediamtx:8889` (WebRTC WHEP, prefix stripped).

### 7.7 Realtime gateway (SignalR hub `/hubs/live`, JSON protocol)
Client → server: `SubscribeCameras(string[] cameraIds)`, `UnsubscribeCameras(string[])`,
`SubscribePlans(string[] planIds)`.
Server → client:
* `detections(detection)` — only to subscribers of that camera; **throttle to ≤ 3/s per camera**, drop older.
* `cameraHealth(health)` — to everyone.
* `recognition(recognition)` — to everyone.
* `pipelineMetrics(metrics)` — to subscribers of that plan.
* `planChanged(plan)` — to everyone.

## 8. Live video in the UI
Browser plays WebRTC via WHEP: `POST http://localhost:8000/media/{camera_id}/whep`
(MediaMTX WHEP endpoint `http://mediamtx:8889/{camera_id}/whep`). Boxes from `detections`
are drawn on a `<canvas>` over the `<video>`, scaled from `width/height`; boxes fade after 1 s.

## 9. Memory budgets (local test, per container)
redpanda 1 GB (`--memory 1G --smp 1`), minio 256 MB, postgres 256 MB, mediamtx 128 MB,
sim-cameras 512 MB, prometheus 256 MB, grafana 192 MB, redpanda-console 192 MB,
each .NET service 128 MB (Server GC off: `DOTNET_gcServer=0`, `DOTNET_GCHeapHardLimit` set),
frame-extractor 256 MB per replica (~10 cameras), detector 512 MB, lpr 384 MB, ocr 512 MB,
face 384 MB, planner 192 MB, ui 64 MB. Ollama (host) qwen2.5:3b ≈ 2.5 GB.

## 9.1 Amendments v1.1 (manager decisions, binding)
* `redpanda` container limit is **1280 MB** (`--memory 1G` is the Seastar heap only).
* .NET runtime images are **`mcr.microsoft.com/dotnet/aspnet:10.0-alpine`** (not chiseled): compose
  healthchecks run `wget -qO- http://127.0.0.1:<port>/health`. `DOTNET_GCHeapHardLimit=0x6000000` (96 MB).
* `/metrics` is served on each service's own port (frame-extractor: 9101).
* **Partial detections**: a `queue`-mode step's result is a second `detections` message for the same
  `frame_id` containing only that step's output plus `"partial": true` and `"step_id": "sN"` (main
  messages have `"partial": false`, `"step_id": null`). Idempotency key for consumers =
  (`frame_id`, `step_id ?? "main"`). The UI merges partials into the frame it already shows.
* `planChanged` for a deleted plan is `{ "plan_id": "...", "deleted": true }`.
* `faces[].score`, `person_id`, `person_name` are `null` for `detect_faces` (no identification).
* `GET /api/workers` → `assigned` is a list of camera ids. Registry has no PATCH: update = GET + full PUT.
* Health mapping: extractor `starting|stalled|reconnecting|error` → `degraded`; disabled camera or no status yet → `unassigned`.
* `GET /api/detections/stats` → `[ { camera_id, class, minute_ts, count } ]` (objects only).
* Snapshot URL for a live `recognition` = `/api/snapshots/{type}/{yyyyMMdd UTC of ts}/{recognition_id}.jpg`;
  it can 404 for ~1 s until Results copies it — clients retry.
* frame-extractor budget is **384 MB per replica** for ~10 cameras at ≤540p (≈640 MB at 1080p).
* `camera.status` `state: "offline"` from an extractor is a **final release** (camera unassigned from
  that worker or worker shutdown) — not a failure. stream-health must not count it as a reconnect or
  downtime if another worker reports the camera online within the lease (15 s); after that, the camera
  is `unassigned` (no owner in registry) or `offline`.
* **Environment variable names are defined by `infra/ENV.md` and are binding.** Services read exactly
  those names (e.g. `MINIO_USE_SSL`, `MINIO_FRAMES_BUCKET`, `MINIO_SNAPSHOTS_BUCKET`, `PORT` for Python
  services, `POSTGRES_CONNECTION` for .NET, `DATABASE_URL` for planner, `DETECTOR_URL`/`LPR_URL`/`OCR_URL`/
  `FACE_URL` base URLs for the executor, `REALTIME_GATEWAY_URL`, `MEDIAMTX_WHEP_URL`, `CORS_ORIGINS`,
  `FACE_DATA_DIR`, `LEASE_S`, `EXECUTOR_ID`). Extra service-specific tuning vars are allowed with defaults.

## 9.2 Amendments v1.2 (from integration, binding)
* `when.class` in `found` and `count` may be a **comma-separated any-of list** of COCO names, e.g.
  `"car,truck,bus"` (no spaces around commas; multi-word names keep their space: `"cell phone"`).
  `found` = any listed class detected (≥ min_confidence); `count` = number of detections of any listed class > gt.
  `plan.schema.json` is unchanged (class stays a string).
* `mode` is assigned by the planner from the tool's `cost` in `tools.json` (`high` → `queue`, else `inline`)
  unless the user explicitly asks; the LLM does not choose it.
* API gateway: planner routes (`/api/plans/**`) allow **6 minutes** per request (local CPU LLM drafts
  take 2-3 min); all other routes keep the default.

## 9.3 Amendments v1.3 (from user acceptance testing, binding)
* **Adding sources.** Every camera is ingested **through MediaMTX** (one connection per camera, also for the
  UI's live video). The camera config gains `stream_url` (the real camera URL the user enters: `rtsp://`,
  `rtsps://`, `rtmp://`, `http(s)://…m3u8`, or `srt://`). Special value `"publisher"` = a stream pushed into
  MediaMTX by someone else (the sim cameras), no pull is configured.
  * `camera_id` must match `^[a-z0-9][a-z0-9-]{2,39}$` (it is also the MediaMTX path name).
  * The **registry** owns MediaMTX path provisioning via the MediaMTX API (`MEDIAMTX_API_URL`, default
    `http://mediamtx:9997`): create → `POST /v3/config/paths/add/{camera_id}` `{"source": stream_url,
    "sourceOnDemand": false, "rtspTransport": "tcp"}`; update of `stream_url` → `PATCH
    /v3/config/paths/patch/{camera_id}`; delete → `DELETE /v3/config/paths/delete/{camera_id}`; on startup
    reconcile all cameras (MediaMTX config is not persistent). `"publisher"` cameras are not provisioned.
  * `source_url` (what extractors read) is **always set by the registry** to `rtsp://mediamtx:8554/{camera_id}`;
    clients send `stream_url`, never `source_url`.
  * A provisioning failure does not block the camera write; the camera is stored and the error is returned in
    `provision_error` (string|null) on the camera, and retried by the startup/periodic reconcile (30 s).
* **Live latency (conflation).** The executor processes **only the newest frame per camera**: a frame is
  skipped (`vp_executor_frames_skipped_total{reason="superseded"}`) when a newer frame of the same camera has
  already been consumed. `MAX_FRAME_AGE_MS` default **1500**. Goal: detection boxes ≤ ~1 s behind live video.
* **Sim cameras** contain no synthetic shapes: motion comes only from pan/zoom over the real test images.
* **`frames` bucket retention is 10 minutes** (was 1 day: ~12 GB/h locally, ~5 TB/day at 400 cameras, and it
  filled the laptop disk). Consumers read a frame within seconds; anything kept longer goes to `snapshots`.

## 9.4 Amendments v1.4 (user requests, binding)
* **Known people library.** The face gallery is exposed through the gateway: `/api/persons/**` → `face:8104/persons/**`.
  * `GET /api/persons` → `{ persons: [ { person_id, name, photos: n, created_at, photo_url } ], count }`
  * `POST /api/persons` (multipart `name`, `image`) → enroll a new person (400 if no / several faces in the image)
  * `POST /api/persons/{id}/photos` (multipart `image`) → add another photo (embedding averaged / max-matched)
  * `PUT /api/persons/{id}` `{ name }` → rename · `DELETE /api/persons/{id}`
  * `GET /api/persons/{id}/photo` → face thumbnail JPEG (`photo_url` points here)
  * `GET /api/recognitions?person_id=…` filters sightings of one person (Results).
* **Duplicate cameras.** The registry flags cameras that pull the same stream: normalized `stream_url`
  (lowercase scheme/host, default port removed, credentials and trailing `/` ignored). Each camera carries
  `duplicate_of: [camera_id…]` (empty when unique) in the API and `camera.config`. Duplicates are allowed but
  flagged; the UI warns before saving a duplicate. Metric `vp_registry_duplicate_cameras` (gauge, cameras that
  have ≥1 duplicate). `"publisher"` cameras are never duplicates.

* **Plan draft cache.** `POST /api/plans/draft` first looks up a cache keyed by
  `sha256(normalized_query + "|" + OLLAMA_MODEL + "|" + sha256(system_prompt + tools.json + llm_output_schema))`,
  normalized_query = lowercase, trimmed, internal whitespace collapsed, trailing punctuation removed.
  Only VALID drafts are stored (Postgres table in database `planner`, with `hits`, `created_at`, `last_hit_at`);
  a cached draft is re-validated before being returned (if invalid now, it is evicted and the LLM is called).
  `camera_ids` are not part of the key (the steps don't depend on them; the response uses the request's ids).
  Response adds `cached` (bool) and `cache_hits` (int). `POST /api/plans/draft?refresh=true` bypasses and replaces
  the entry. `GET /api/plans/drafts/recent?limit=10` → recent distinct queries (for suggestions).
  Metrics: `vp_planner_cache_total{result="hit|miss|refresh|evicted"}`.

## 9.5 Amendments v1.5 (user requests + QA, binding)
* **Progressive detections (latency).** The executor publishes a `detections` message after EACH inline step:
  the first completed step → `partial:false, step_id:null` (boxes reach the UI after detect_objects only);
  every later inline step → `partial:true, step_id:"sN"` with only that step's output and `timings_ms`.
  Queue-mode steps unchanged. Consumers already key on (`frame_id`, `step_id ?? "main"`).
* **Frame catalog** — new service `frame-catalog` (.NET, port 5006, 128 MB). Consumes `frames.raw` and
  `detections` (own group), keeps per camera an in-memory ring of the last `FRAMES_PER_CAMERA` (default 600)
  frames: `{frame_id, camera_id, ts, seq, width, height, worker_id, object_key, analyzed, objects:{class:count},
  plates:[...], faces:[...], texts:[...], total_ms}` (detections merged by frame_id incl. partials).
  API (gateway `/api/frames/**` → frame-catalog):
  * `GET /api/frames?camera_id=&before=<ts>&limit=60` → newest first
  * `GET /api/frames/{camera_id}/{frame_id}` → one frame record incl. boxes (objects/plates/faces/texts with boxes)
  * `GET /api/frames/{camera_id}/{frame_id}/image` → JPEG streamed from the `frames` bucket (404 once expired, 10 min)
  * `GET /api/frames/stats` → per camera `{camera_id, frames, analyzed, oldest_ts, newest_ts, fps}`
* **Worker operations (registry).**
  * `POST /api/workers/{id}/drain` / `POST /api/workers/{id}/undrain` (draining: no new cameras, existing ones moved away)
  * `PUT /api/workers/{id}/capacity` `{capacity}` (override; null clears)
  * `POST /api/cameras/{id}/pin` `{worker_id|null}` (sticky placement; rebalancer respects pins if the worker is live)
  * `POST /api/cameras/{id}/restart` → bumps `generation`; heartbeat `assigned[]` items carry `generation`; the
    extractor restarts that camera's ffmpeg when generation changes.
  * `GET /api/workers` items add `capacity_override`, `draining`, `state: live|draining|dead`, `managed_by`
    (`static|controller`).
  * extractor `WORKER_ID` defaults to the container hostname; `camera.status` interval 2 s (was 5 s).
* **Worker controller (autoscaler)** — new service `worker-controller` (Python, port 5007, 128 MB, Docker API via
  the Docker socket — dev only; production = Kubernetes HPA/KEDA, BACKLOG). Every 10 s: desired workers =
  clamp(ceil(enabled_cameras / (WORKER_CAPACITY × TARGET_UTILIZATION)), MIN_WORKERS, MAX_WORKERS) (+1 if any camera
  is unassigned); scale up = start a `vp/frame-extractor:dev` container (`fx-auto-N`, same env/limits/network,
  label `vp.managed-by=worker-controller`); scale down = pick the least-loaded controller-managed worker → registry
  drain → wait until it owns 0 cameras → stop/remove. Never touches static workers. Cooldown `COOLDOWN_S` (60).
  API (gateway `/api/autoscaler/**` → worker-controller): `GET /api/autoscaler` → `{mode: auto|manual, policy,
  current, desired, workers:[...], events:[{ts, action, worker_id, reason}]}`; `PUT /api/autoscaler/policy`;
  `PUT /api/autoscaler/mode`; `POST /api/autoscaler/scale` `{workers}` (manual mode). Metrics
  `vp_controller_workers{state}`, `vp_controller_decisions_total{action}`. Prometheus discovers controller-managed
  workers through a file_sd JSON the controller writes to a shared volume.

## 9.6 Amendments v1.6 (from an overnight run: disk filled by snapshots, binding)
* **A recognition is an EVENT (a sighting), not a per-frame result.** The executor keeps per camera a cooldown
  map: the same (`camera_id`, `type`, `value`) produces at most one `recognitions` message per
  `RECOGNITION_COOLDOWN_S` (default 30). For faces with `value: "unknown"` the cooldown is per camera
  (`UNKNOWN_FACE_COOLDOWN_S`, default 10). Suppressed sightings are counted
  (`vp_executor_recognitions_suppressed_total{type}`); `detections` messages are unaffected (boxes still show).
  Measured before: ~3 recognitions/s for one parked car → ~20 GB/day of snapshots on the laptop.
* **Snapshot retention:** `snapshots` bucket TTL `SNAPSHOT_TTL` (production 7d, local test 1d); Results deletes
  recognition rows older than `RECOGNITION_RETENTION_DAYS` (same horizon) hourly. Prometheus keeps
  `--storage.tsdb.retention.size=1GB`.

## 9.7 Amendments v1.7 — FRAMES TRAVEL IN KAFKA (replaces hard rule 1.2 for frames; binding)
Measured: SeaweedFS saturates at ~50 PUT/s (p99 5.5 s, 1.3-2.4 cores); 100 cameras need ~300 PUT/s and 400
need ~1,200/s. Frames live minutes, are written once and read 1-2 times in order → Kafka, not an object store.
* `frames.raw` message: **key** = camera_id, **value = the raw JPEG bytes**, **headers**: `meta` = UTF-8 JSON of
  5.2 **without** `object_key` (`{frame_id, camera_id, ts, seq, width, height, worker_id}`), `content-type: image/jpeg`.
  Producer: lz4 is useless on JPEG — use `compression.type=none`; `message.max.bytes` ≥ 2 MB; linger 10-20 ms.
* Topic `frames.raw`: retention **5 min** (`retention.ms=300000`), `segment.ms=60000`, `segment.bytes=64MB`,
  `max.message.bytes=2097152`, 48 partitions.
* Executor reads the JPEG from the message (no S3 GET in the hot path). On a recognition that passes the
  cooldown (9.6) the **executor** PUTs the frame to `snapshots/{type}/{yyyyMMdd UTC}/{recognition_id}.jpg` and
  puts `snapshot_key` in the `recognitions` message (field added). `frame_key` is removed from recognitions.
* Results stores `snapshot_key` as given (no copy anymore) and keeps serving `/api/snapshots/...`.
* frame-catalog stores (`partition`, `offset`) per frame and serves `/api/frames/{cam}/{id}/image` by fetching
  that single message (assign + seek) with a small per-camera LRU of the newest JPEGs (default 3 per camera);
  404 once the offset is below the log start (retention).
* The `frames` bucket is no longer written; extractors need no S3 config.

## 9.8 Amendments v1.8 — DEMAND-DRIVEN EXTRACTION + WATCHED-CAMERA PRIORITY (binding)
Measured (31 cameras, laptop): every camera was decoded + JPEG-encoded at 3 fps while 11 had no plan and nobody
looked at their frames; the executor then discarded ~95 % of frames (stale/superseded) and a watched camera got
one analyzed frame every 7-25 s. Rule: **no demand → no full decode**; **watched cameras are analyzed first**.

**1. Demand signal — new topic `camera.demand`** (12 partitions, compact, key = camera_id), producer =
realtime-gateway, consumers = registry, executor.
```json
{ "camera_id": "cam-001", "live_viewers": 2, "frame_viewers": 0, "ts": 1761030000123 }
```
* `live_viewers` = SignalR connections subscribed via `SubscribeCameras` (live wall tiles, focus page, ops live tab).
* `frame_viewers` = connections subscribed via the NEW hub methods `WatchFrames(string[] cameraIds)` /
  `UnwatchFrames(string[] cameraIds)` (Extracted-frames page, ops Frames tab). Same limits/cleaning as SubscribeCameras;
  removed on disconnect.
* Published on change (debounced 1 s per camera) and re-published for every camera with a non-zero count every 30 s.
  Consumers treat an entry older than **90 s as zero** (gateway crash safety). A count dropping to 0 is published as 0
  (not a tombstone).
* Several realtime-gateway instances: out of scope (single instance; BACKLOG #4 backplane).

**2. Extraction mode — decided by the registry** (single decision point), per camera:
* `full` at `analysis_fps` when: enabled AND (`plan_id` != null OR `frame_viewers` > 0 OR `live_viewers` > 0 with a plan).
  Simplified: `full` ⇔ enabled ∧ (plan_id ≠ null ∨ frame_viewers > 0).
* otherwise `idle`: keyframes only (`-skip_frame nokey`), output **0.2 fps** (one frame / 5 s) — keeps health,
  thumbnails and the frames grid alive at ~zero decode cost.
* Hysteresis: `full → idle` only after **60 s** without demand; `idle → full` immediately.
* Heartbeat `assigned[]` items and `GET /api/cameras` items add `"extract": {"mode": "full|idle", "fps": 3}`.
* Capacity accounting: a worker's load = full cameras × 1.0 + idle cameras × **0.1** (`IDLE_WEIGHT`, env). Placement,
  `GET /api/workers` (`load` field added, number) and the autoscaler use load instead of camera count:
  desired = clamp(ceil(Σload / (WORKER_CAPACITY × TARGET_UTILIZATION)), MIN, MAX) (+1 if any camera unassigned).

**3. Extractor**: applies `extract` from the heartbeat. A change of (mode, fps) restarts that camera's ffmpeg with the
new arguments, at most once per **10 s** per camera (coalesce changes). `camera.status` adds `"mode": "full|idle"` and
`"fps_target"` (number). In idle mode the stall timeout is **30 s** (keyframes only, GOP up to 10 s) instead of 5 s.

**4. Executor**:
* **The built-in default plan is removed**: `plan_id` null (or plan not active) → frame skipped, reason `no_plan`
  (contract 5.1 sentence about the default plan is void). Idle frames of cameras without a plan cost nothing.
* **Priority scheduling**: the conflation ready-queue has two tiers — tier 1 = cameras with `live_viewers` > 0,
  tier 2 = everything else; free slots always take tier 1 first (round-robin within a tier). Starvation guard:
  every 4th slot goes to tier 2 if it has waiting work. Metric `vp_executor_frames_total{tier="watched|background"}`.

**5. Stream health**: state `idle` (owned, mode idle, frames arriving within 30 s) counts as healthy. Degraded/online
compare `fps_actual` with the status's `fps_target`. Summary adds `idle` count; `/api/health/cameras` items add `mode`.

**6. UI**: Extracted-frames page and the ops Frames tab call `WatchFrames`/`UnwatchFrames` for the camera(s) shown
(grid: visible tiles only, batched like SubscribeCameras). Show the mode (`idle` badge, grey) on health, ops chips and
frames page ("idle: 1 frame / 5 s — opening this page switches it to full rate"). Remove the "running the default plan"
text; cameras without a plan show "No pipeline" with a link to /pipelines.

**Acceptance (measured on the laptop, 31 cameras, plan on 20):** extractor CPU for the 11 plan-less cameras drops
≥ 80 %; a watched plan camera gets ≥ 1.5 analyzed frames/s; opening Extracted frames on an idle camera reaches 3 fps
within 5 s; no regression in detection age p95 for watched cameras.

## 9.9 Amendments v1.9 — CHEAPER EXTRACTION: MOTION GATING, SUBSTREAM, RTSP ERROR CLASSES (binding)
Design facts (decided with the user): cameras are NOT controllable (keyframe interval is whatever they send, often 1-4 s,
so keyframe-only decoding cannot deliver 3 fps) → decode cost is paid per SOURCE frame; levers are fewer pixels (substream),
cheaper decode flags, not sending unchanged frames downstream (motion gating), and per-class RTSP error handling.

**1. Camera fields** (registry; stored in `camera.config` and `GET/POST/PUT /api/cameras`, all optional):
* `analysis_stream_url` — real RTSP URL of the camera's substream used for analysis. The registry provisions a SECOND MediaMTX
  path `{camera_id}-analysis` (same provisioning rules/credentials handling as 9.3) and exposes read-only
  `analysis_source_url` = `rtsp://mediamtx:8554/{camera_id}-analysis` (null when no `analysis_stream_url`).
  Heartbeat `assigned[]` items add `analysis_source_url`; the extractor reads `analysis_source_url ?? source_url`.
  The main path `{camera_id}` (`source_url`) is for live viewing (WebRTC) and is pulled by MediaMTX only on demand.
* `motion` — `{ "enabled": false, "sensitivity": "medium", "heartbeat_s": 5 }`; `sensitivity` ∈ `low|medium|high`,
  `heartbeat_s` ∈ [1, 60]; missing = `{enabled:false, sensitivity:"medium", heartbeat_s:5}` (opt-in until measured).
  Validation errors → 400 with field names. Changing `motion` or `analysis_stream_url` is a config change (published to camera.config).

**2. MediaMTX provisioning**: paths for pulled cameras (`stream_url`/`analysis_stream_url` set) use `sourceOnDemand: true`,
`sourceOnDemandStartTimeout: 10s`, `sourceOnDemandCloseAfter: 30s`. Publisher-type (sim) paths unchanged.

**3. Extractor ffmpeg chain** (full mode only; idle mode unchanged, never gated):
`-vf fps={analysis_fps},mpdecimate=hi=H:lo=L:frac=F:max=M,scale=...` with `-fps_mode vfr` (no duplicate refill).
`fps` BEFORE `mpdecimate` (compare consecutive kept frames). `M = ceil(heartbeat_s * analysis_fps) - 1` consecutive
drops at most (= heartbeat frame). `(H, L, F)` per sensitivity live ONLY in `extractor/motion_presets.py`
(calibrated by the benchmark; initial = ffmpeg defaults for medium, stricter for low, looser for high).
Optional decoder flag `-skip_loop_filter all` behind env `FFMPEG_SKIP_LOOP_FILTER` (default off until the benchmark shows
no detection-quality loss; the benchmark decides the default).
Changing motion/analysis URL restarts that camera's ffmpeg (≤ 1 per 10 s, existing coalescing).

**4. Observability**: `vp_extractor_frames_motion_skipped_total{camera_id}` (frames dropped by the gate; derive from ffmpeg
`-progress` drop counters or expected-vs-emitted, team's choice, document it); `camera.status` adds `motion_skipped_pct`
(rolling 60 s, 0-100, null when motion disabled/idle) and `error_class`; health items add `motion_skipped_pct`, `error_class`.

**5. RTSP error classes** (extractor classifies ffmpeg stderr + exit; `camera.status.error_class`):
`auth_failed` (401/Unauthorized) · `not_found` (404 / path not found) · `codec_unsupported` · `unreachable`
(refused/timeout/no route/DNS) · `stalled` (no frames) · `decode_error` · `eof`. Terminal-until-changed classes
(`auth_failed`, `not_found`, `codec_unsupported`): state `error`, retry backoff 30 s → 5 min (not the hot 1-10 s loop),
reset when the camera config changes. Transient classes keep the existing exponential backoff + jitter.
Stream-health: state `offline` with `error_class` (UI shows the class, e.g. "Wrong credentials"), never "degraded".

**6. Deliverables / acceptance**
* `qa/perf/decode-bench/` (script + `RESULTS.md`): CPU-seconds per source-second (rusage user+sys, NOT wall clock; ≥ 60 s of
  media, 3 repeats, container `--cpus=1`) for {360p, 540p, 720p, 1080p} × {h264 baseline, `-skip_loop_filter all`,
  keyframe-only GOP 1 s} + h265 at 720p/1080p if the encoder exists; plus motion gating on static vs moving clips.
* 10 new static-scene simulated cameras (profile S, `cam-031..040`): fixed scene + sensor noise + a moving object crossing for
  ~3 s about every 20 s (known timestamps in `infra/sim-cameras/assets/S-events.json`).
* Acceptance on static cameras (medium sensitivity): ≥ 80 % of frames skipped overall; every injected event produces ≥ 2
  emitted frames within 1 s of its start (recall 100 %); on the moving (pan/zoom) clips skipped ≤ 5 %.
  Then detector load before/after on 4 static cameras with a plan attached.

## 10. Observability
Prometheus metric names: `vp_<service>_<thing>_<unit>` (e.g. `vp_extractor_frames_total{camera_id}`,
`vp_executor_step_duration_seconds{tool}` histogram, `vp_extractor_ffmpeg_restarts_total`).
