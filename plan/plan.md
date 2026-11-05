# Vision Pipeline — Master Plan

**Live video analytics for 400+ RTSP cameras, with pipelines that an LLM builds from a plain-English query.**

This file is the single starting point for a Claude session (manager / "CEO" role) that has to design, build, verify and
scale this system. It describes what the system must do, how it is built, the numbers measured so far, the mistakes
already made (so they are not repeated), and the order in which to build it. The repository next to this file
(`../`) contains a working implementation of everything up to Phase 6; the detailed, binding message/API contracts are in
`../contracts/CONTRACTS.md` (this plan summarises them; if they disagree, **CONTRACTS.md wins**).

---

## 0. How to use this plan (read first)

**You are the manager.** Do not start by writing code. In this order:

1. Read sections 1–3 (goal, rules, architecture). Skim 4–7 (contracts, plans, extraction, UI). Read 12 (lessons) fully.
2. Confirm the environment with the user (section 13): hardware, GPU or not, real or simulated cameras, how many.
3. Write / update the **contracts first** (`contracts/`). Teams never invent private message formats.
4. Split the work into **teams with disjoint folders** (section 11), brief each team with the template in 11.3, run them in
   parallel, and verify every claim yourself with a measurement before reporting it as done.
5. Integrate, run the end-to-end check, look at the UI in a real browser, then report to the user in plain, simple words.

**Working agreements with the user (observed, keep them):**

- Explain in **simple words**, with a flow diagram when something is architectural. Lead with the answer, then the detail.
- **Report measured numbers**, not beliefs. If a target was missed, say so and say why. Never claim "done" without a test
  or a measurement. When you were wrong, say it plainly (this happened several times: see section 12).
- Org model the user asked for: **Opus = CEO/manager/approver** (harsh about scale, performance, memory, clean
  architecture), **Sonnet agents = teams** working in parallel and validating each other, **Haiku = easy tasks**, Sonnet again
  for the hard ones. A separate **bug-finding team** (drives the real UI in a browser) and a **fix team** are used after
  every integration.
- The test machine is a laptop with a small disk. Watch RAM and **disk** continuously; ask before anything destructive;
  never delete the user's other projects' images/volumes. When the user says "stop everything", stop *everything*
  (including agents that run Docker) and free caches only.
- The user wants to be told **where to look** (URLs) whenever something is ready.

---

## 1. Goal and requirements

### 1.1 Product

A user opens a web client and sees **live cameras with detection boxes**, **recognised license plates and faces with
snapshots**, **the raw extracted frames of every camera**, **the health of every stream**, **which worker reads which
camera**, and can **describe a goal in plain English** ("read the plates of cars entering gate 3") — an LLM turns it into a
validated **pipeline (a small DAG of AI tools)** that then runs live on the chosen cameras.

### 1.2 Requirements stated by the user

| # | Requirement |
|---|---|
| R1 | **400+ cameras**, analysed at **3 fps** (= 1,200 frames/s), many live streams is a MUST. Local test-prod may be slower, but the design must scale horizontally unchanged. |
| R2 | **Frame extraction with ffmpeg** by separate services; each service handles a *group* of streams (not one process for all, not one per camera); **stream monitoring** per camera. |
| R3 | A user query → **LLM builds a DAG pipeline** per camera, live. Plans are validated, activated explicitly, and **cached** (the same query repeats). |
| R4 | **Kafka** for the high-volume paths. The pipeline must be fast; slow in the local test is acceptable, bad architecture is not. |
| R5 | **Microservices divided by responsibility**; no monolith. Pick the proper language per service (.NET for the control/data plane, Python for AI engines and ffmpeg orchestration). |
| R6 | **React client**: modern, clean, nice. Pages: live (with boxes), recognitions (**with snapshots**), people library ("known people lobby"), pipelines (build + **watch a camera's pipeline running live**), sources (add cameras), health/monitoring (frame extraction, **duplicate cameras**), **extracted frames per camera with time (before the pipeline)**, **operations page: which camera/RTSP is on which worker as a flow, with auto-control of workers**. |
| R7 | **Free / open-source AI** (no paid APIs): LLM (Ollama + qwen2.5:3b), YOLOX, fast-alpr, RapidOCR, OpenCV YuNet+SFace. |
| R8 | **Prove scale**: a simulated fleet of **100 different RTSP streams**, more workers, ramp test, find the first resource that saturates, size the 400-camera system from measurements. |
| R9 | Cameras are **not controllable** (keyframe interval, codec, substream settings are whatever the camera sends): the design must not depend on camera settings (it may *benefit* from them). |

### 1.3 Non-goals (for now)

Authentication/RBAC, multi-tenant, recording/NVR, GPU deployment, multi-site, face-gallery replication. They are in the
roadmap (section 9) and backlog (section 14), not in the current build.

---

## 2. Principles and hard rules

1. **Control plane vs data plane.** The LLM runs **once per query** (control plane) and produces a validated JSON plan.
   The executor runs the plan **per frame** with plain code (data plane). The LLM is never on the hot path.
2. **One responsibility per service**, one database per service (one Postgres instance, several databases).
3. **Every service**: structured JSON logs to stdout, `GET /health` (200 when ready), Prometheus `GET /metrics`, config only
   by environment variables (documented in the service README and `infra/ENV.md`), graceful SIGTERM, multi-stage
   non-root Dockerfile, memory budget respected.
4. **Kafka consumers are idempotent**; commit offsets only after the work is done, and only the **contiguous completed
   prefix**. Message key = `camera_id` (per-camera ordering). `ts` = Unix epoch milliseconds (UTC). IDs = UUID v4.
   JSON = `snake_case`.
5. **Live system semantics:** when overloaded, *drop the oldest*, never queue unboundedly. Per-camera **conflation**: only
   the newest frame of a camera is processed.
6. **The registry is the only place that decides which worker owns which camera** (leases + heartbeats, sticky).
7. **Everything the UI needs goes through one API gateway** (`/api/**`, `/hubs/**`, `/media/**`).
8. **Contracts are owned by the manager.** Teams write only inside their own folders and propose contract changes in their
   report. Every contract change is a numbered amendment (9.x) with the measurement that motivated it.
9. **Measure before and after.** Use CPU-time metrics (not wall-clock) when the machine is saturated.
10. **No secrets in logs or API responses** (RTSP credentials are redacted).

---

## 3. Architecture

### 3.1 End-to-end flow

```
                           CONTROL PLANE (rare)                                   DATA PLANE (every frame)
 ┌──────────┐ query  ┌─────────┐ plan JSON  ┌──────────┐                ┌──────────┐ RTSP  ┌────────────────────┐
 │  Web UI  ├───────►│ Planner ├───────────►│ pipeline │                │ Cameras  ├──────►│ MediaMTX (relay)   │
 └────┬─────┘        │ (LLM +  │ validated  │  .plans  │                └──────────┘       │ + WebRTC (WHEP)    │
      │              │ cache)  │            │ (Kafka)  │                                   └─────┬──────────┬───┘
      │              └─────────┘            └────┬─────┘                              RTSP pull  │          │ live video
      │                                          │                                   (by workers) │          │ to browser
      │   ┌──────────┐ camera.config  ┌──────────▼────┐ heartbeat/assign ┌─────────▼──────────┐ │          │
      ├──►│ Registry ├───────────────►│ Executor(s)   │◄─────────────────┤ Extractor workers  │ │          │
      │   │ (cameras,│                └──┬─────────┬──┘                  │ (ffmpeg per camera,│ │          │
      │   │ workers, │                   │ HTTP    │ detections/         │ N cameras/worker)  │ │          │
      │   │ modes)   │                   ▼         ▼ recognitions        └─────────┬──────────┘ │          │
      │   └────▲─────┘         ┌───────────────┐  ┌──────────┐   frames.raw (JPEG in Kafka)     │          │
      │        │ demand        │ AI engines    │  │ Kafka    │◄───────────────────┘             │          │
      │   ┌────┴─────────┐     │ detector, lpr,│  └─┬──┬──┬──┘                                  │          │
      │   │ Realtime GW  │◄────┤ ocr, face     │    │  │  └────────► Results (Postgres + snapshots in object store)
      │   │ (SignalR)    │     └───────────────┘    │  └───────────► Stream-health (online/idle/degraded/offline)
      │   └──────▲───────┘                          └──────────────► Frame-catalog (raw-frame viewer)
      │          │ detections, health, recognitions, plans, metrics
      └──────────┴──── API gateway (YARP, :8000) ◄──── Worker-controller (autoscaler, Docker/K8s)
```

### 3.2 Service catalog

| Service | Stack | Port | Responsibility | Budget |
|---|---|---|---|---|
| `redpanda` | Redpanda (Kafka API) | 9092 / 19092 | The bus. Topics in section 4.1 | 1.25 GB |
| `redpanda-console` | Redpanda Console | 8080 | Kafka UI | 192 MB |
| `minio` | **SeaweedFS** (S3 API; MinIO images were removed from registries) | 9000, 8888 | Snapshots only (frames travel in Kafka since v1.7) | 256 MB |
| `postgres` | Postgres 16 | 5432 | DBs `registry`, `planner`, `results` | 256 MB |
| `mediamtx` | MediaMTX | 8554 RTSP, 8889 WHEP, 9997 API, 9998 metrics | Relay: pulls cameras, serves RTSP to workers and WebRTC to browsers | 128 MB |
| `sim-cameras` | ffmpeg `-c copy` publishers | – | Simulated fleet: up to 100 streams (`SIM_CAMERAS`, default 40) | 768 MB |
| `registry` | .NET 10 | 5001 | Cameras CRUD, MediaMTX path provisioning, worker leases, assignment, **extract mode (full/idle)**, weighted load, duplicate detection | 128 MB |
| `frame-extractor` ×N | Python, asyncio + **ffmpeg per camera** | 9101… | Pull RTSP, decode, sample at `analysis_fps`, motion gate, JPEG → Kafka; per-camera status | 512 MB / worker (≈12 cameras) |
| `worker-controller` | Python | 5007 | Autoscaler (Docker socket in dev, K8s/KEDA in prod); policy, manual mode; Prometheus file_sd | 128 MB |
| `stream-health` | .NET | 5002 | Fold `camera.status` into `camera.health` (online / idle / degraded / offline, error class) | 128 MB |
| `executor` | .NET | 5003 | Run plans per frame: conflation, tiers, per-tool limits, breakers, progressive detections, snapshots | 128 MB |
| `detector` | Python FastAPI + ONNX Runtime, **YOLOX** | 8101 | `detect_objects` | 512 MB |
| `lpr` | Python, **fast-alpr** | 8102 | `read_plate` | 384 MB |
| `ocr` | Python, **RapidOCR** | 8103 | `read_text` | 512 MB |
| `face` | Python, **OpenCV YuNet + SFace** | 8104 | `detect_faces`, `identify_faces`, known-people API | 384 MB |
| `planner` | Python FastAPI + **Ollama qwen2.5:3b** (host) | 8010 | Query → validated plan, plan **cache**, activation | 192 MB |
| `results` | .NET | 5004 | Store detections stats / recognitions (batched writes), serve snapshots | 128 MB |
| `realtime-gateway` | .NET SignalR | 5005 | Fan-out of live events with throttling; **publishes demand** (who watches what) | 128 MB |
| `frame-catalog` | .NET | 5006 | Raw-frame ring buffer per camera (the "Extracted frames" page) | 128 MB |
| `api-gateway` | .NET YARP | 8000 | The only backend entry for the UI; CORS; long timeout for planner | 128 MB |
| `ui` | React 19 + TS + Vite + Tailwind v4, nginx | 5173 | The client (section 7) | 64 MB |
| `prometheus`, `grafana` | – | 9090, 3000 | Metrics, dashboards (`vision-pipeline`, `frame-extraction`) | 256 / 192 MB |

Ollama runs on the **host** (`host.docker.internal:11434`, model `qwen2.5:3b`, ≈2.5 GB).

### 3.3 Repository layout

```
vision-pipeline/
  plan/                 this file + screenshots/
  contracts/            CONTRACTS.md (binding), tools.json, plan.schema.json, BACKLOG.md, SCALE-TEST.md
  infra/                docker-compose.yml, ENV.md, redpanda/, objectstore/, mediamtx.yml, prometheus/, grafana/,
                        sim-cameras/, scripts/ (e2e-check.sh, check-line-endings.sh)
  services/
    frame-extractor/    Python (ffmpeg orchestration)
    engines/            detector/ lpr/ ocr/ face/ common/   (Python FastAPI)
    planner/            Python
    worker-controller/  Python
    dotnet/             Registry StreamHealth Results Executor RealtimeGateway ApiGateway FrameCatalog (+ *.Tests)
  ui/                   React app (+ DESIGN.md, design/)
  models/               ONNX models (read-only)
  qa/                   Playwright tests, bug reports, perf harness (qa/perf/ramp.py, decode-bench/)
```

---

## 4. Contracts (summary of CONTRACTS.md, current state = v1.9)

### 4.1 Kafka topics

| Topic | Partitions | Retention | Key | Value | Producer → consumers |
|---|---|---|---|---|---|
| `camera.config` | 12 | compact | camera_id | full camera config; tombstone = deleted | registry → extractors, executor, stream-health |
| `frames.raw` | 48 | **5 min** (`segment 60 s`, max msg 2 MB, **no compression**) | camera_id | **the JPEG bytes**; header `meta` = JSON `{frame_id,camera_id,ts,seq,width,height,worker_id}` | extractor → executor, frame-catalog |
| `detections` | 24 | 6 h | camera_id | one message per analysed frame **and one after each step** (`partial`, `step_id`) | executor → realtime-gateway, results, frame-catalog |
| `recognitions` | 12 | 7 d | camera_id | plate/face events incl. `snapshot_key` | executor → results, realtime-gateway |
| `camera.status` | 12 | 1 h | camera_id | worker's view: state, fps, mode, `error_class`, `motion_skipped_pct` | extractor → stream-health |
| `camera.health` | 12 | compact | camera_id | latest merged health | stream-health → realtime-gateway |
| `camera.demand` | 12 | compact | camera_id | `{live_viewers, frame_viewers, ts}` (≥ 90 s old = zero) | realtime-gateway → registry, executor |
| `pipeline.plans` | 6 | compact | plan_id | plan; tombstone = deleted | planner → executor, realtime-gateway |
| `pipeline.metrics` | 6 | 1 h | plan_id | per step per 5 s: processed/skipped/errors/p50/p95 | executor → realtime-gateway |
| `jobs.heavy` | 12 | 6 h | camera_id | JPEG + header `job` (for `mode: queue` steps) | executor → executor (heavy workers) |

Auto topic creation is **off**; topics are created by `infra/redpanda/init-topics.sh` (idempotent, `rpk topic alter-config`
so reruns converge).

### 4.2 Camera config (registry, `camera.config`, `GET/POST/PUT /api/cameras`)

```json
{ "camera_id": "cam-001", "name": "Gate 3",
  "stream_url": "rtsp://user:pass@192.168.1.20:554/stream1 | publisher",
  "analysis_stream_url": "rtsp://…/stream2 (optional substream for analysis)",
  "source_url": "rtsp://mediamtx:8554/cam-001",              // derived: viewing path
  "analysis_source_url": "rtsp://mediamtx:8554/cam-001-analysis",   // derived, null without substream
  "analysis_fps": 3, "enabled": true, "plan_id": "uuid|null", "tags": ["gate"],
  "motion": { "enabled": false, "sensitivity": "low|medium|high", "heartbeat_s": 5 },
  "updated_at": 1761030000000 }
```
`camera_id` regex `^[a-z0-9][a-z0-9-]{2,39}$` (not ending in `-analysis`). `PUT` replaces the whole record. Duplicate cameras
(same normalised `stream_url`) are flagged with `duplicate_of`. Read-only runtime fields: `extract {mode,fps}`,
`assigned_worker`, `pinned_worker`, `generation`, `provision_error`.

### 4.3 Detections and recognitions

```json
{ "frame_id": "uuid", "camera_id": "cam-001", "ts": 1761030000123, "width": 640, "height": 360,
  "plan_id": "uuid", "plan_version": 3, "partial": false, "step_id": "s2",
  "objects": [{"class":"truck","confidence":0.91,"box":[x1,y1,x2,y2]}],
  "plates":  [{"plate":"12345678","confidence":0.95,"box":[…]}],
  "faces":   [{"box":[…],"confidence":0.9,"person_id":"p-12|null","person_name":"Dana|null","score":0.71}],
  "texts":   [{"text":"GATE 3","confidence":0.88,"box":[…]}],
  "timings_ms": {"s1":120.5,"s2":33.1}, "total_ms": 160.2, "executor_id": "ex-1" }
```
Boxes are in **pixels of the analysis frame**; the UI scales them. A **recognition** is an *event*: per (camera, value) a
cooldown applies (`RECOGNITION_COOLDOWN_S=30`, `UNKNOWN_FACE_COOLDOWN_S=10`), and only recognitions that pass it get a
snapshot (the executor PUTs the JPEG to `snapshots/{type}/{yyyyMMdd}/{recognition_id}.jpg`; `snapshot_key` in the message).

### 4.4 HTTP APIs (all through the gateway on :8000)

- **Registry:** `/api/cameras[/{id}]`, `POST /api/cameras/{id}/restart|pin`, `/api/workers[/{id}]`, `…/drain|undrain`,
  `PUT …/capacity`, heartbeat `POST /api/workers/{id}/heartbeat` → `{assigned:[camera+extract+motion+analysis_source_url], lease_s}`.
- **Stream health:** `/api/health/cameras`, `/api/health/summary` (`total, online, degraded, offline, unassigned, idle, by_error_class`).
- **Results:** `/api/recognitions?camera_id=&type=&person_id=&q=&since=&limit=`, `/api/detections/stats`, `/api/snapshots/…`.
- **Planner:** `POST /api/plans/draft` (`{query, camera_ids}` → plan + `valid` + `errors` + `cached`), `POST /api/plans`,
  `GET/DELETE /api/plans[/{id}]`, `GET /api/plans/drafts/recent`, `GET /api/tools`.
- **People (face service):** `/api/persons` (list, enroll with photos, rename, delete, photo).
- **Frame catalog:** `GET /api/frames?camera_id=&before=&limit=` → `{items,count,next_before}`, `/api/frames/{cam}/{id}`,
  `/api/frames/{cam}/{id}/image`, `/api/frames/stats`, `/api/frames/latest`.
- **Autoscaler:** `GET /api/autoscaler`, `PUT …/policy`, `PUT …/mode` (`auto|manual`), `POST …/scale`.
- **Engines:** `multipart/form-data` field `image`; `POST /detect_objects`, `/read_plate`, `/read_text`, `/detect_faces`,
  `/identify_faces`; every response has `ms`; **429 when the engine is saturated** (never queue).
- **Realtime hub** (`/hubs/live`, SignalR JSON): client → `SubscribeCameras`, `UnsubscribeCameras`, `SubscribePlans`,
  `WatchFrames`, `UnwatchFrames`; server → `detections` (≤ 3/s/camera), `cameraHealth`, `recognition`, `pipelineMetrics`, `planChanged`.
- **Live video:** WebRTC/WHEP `POST /media/{camera_id}/whep` (gateway → MediaMTX).

---

## 5. Pipelines (LLM-built DAGs)

### 5.1 Tools (closed registry, `contracts/tools.json`)

| Tool | Engine | Cost | Params |
|---|---|---|---|
| `detect_objects` | detector | low | `classes`, `min_confidence` |
| `read_plate` | lpr | medium | – |
| `read_text` | ocr | high | – |
| `detect_faces` | face | low | – |
| `identify_faces` | face | medium | `threshold` |

### 5.2 Plan

JSON (`plan.schema.json`): up to **8 steps**, executed in array order, a step may only reference earlier steps.
`when` ∈ `always | found{class,min_confidence} | count{class,gt} | failed{step}` (class lists like `"car,truck"` = any-of).
`mode`: `inline` (executor calls the engine) or `queue` (publishes `jobs.heavy`) — **set by the planner from the tool's cost**,
not by the LLM. A plan with no `plan_id` on a camera = **no analysis for that camera** (there is no default plan since v1.8).

Example: *"read plates of cars and identify people"* → `s1 detect_objects (always)` → `s2 read_plate (found car)` → `s3 identify_faces (found person)`.

### 5.3 Planner behaviour

1. LLM (Ollama `qwen2.5:3b`, **structured output**) with enums constrained to the registry's tools and COCO classes.
2. **Validator** (schema, tools, references, ≤ 8 steps) + **normalizer/repair** (drops unsatisfiable `when` on the first step, fixes class names, sets `mode`).
3. **Plan cache:** draft keyed by *normalised query + model + prompt/tools hash*; response flags `cached`, `?refresh=true` bypasses; recent drafts endpoint.
4. A draft never runs; **activation** stores the plan, publishes `pipeline.plans`, sets `plan_id` on the chosen cameras via the registry.
5. Measured on the CPU laptop: a draft takes ≈ **87 s** (valid on first attempt). The gateway allows **6 minutes** for `/api/plans/**`.
   Semantic matching of *similar* queries (embeddings) is backlog #20.

### 5.4 Executor behaviour (the hot path)

- Consumes `frames.raw` (JPEG in the message; no object-store read). Plan is resolved once per frame; immutable during the frame.
- **Per-camera conflation:** a newer frame of the same camera replaces the waiting one (reason `superseded`); frames older than
  `MAX_FRAME_AGE_MS` (1500) are dropped (`stale`). Per camera frames run **serially**; slots shared round-robin.
- **Priority tiers (v1.8):** cameras with `live_viewers > 0` (from `camera.demand`) are dispatched first; every 4th slot goes to
  background work (starvation guard). Metric `vp_executor_frames_total{result,tier}`.
- **Progressive detections:** a `detections` message after each step, so the UI shows boxes as soon as the detector answers.
- **Per-tool concurrency limits** (semaphores, aligned with each engine's `MAX_CONCURRENCY + MAX_QUEUE`), timeouts, a **circuit
  breaker** that trips on 5xx/timeouts but **not on 429** (a busy engine is back-pressure, not a failure).
- **Offsets:** committed only for the contiguous completed prefix; poison messages are counted and skipped.
- **Snapshots:** the executor uploads the frame for recognitions that pass the cooldown.

---

## 6. Frame extraction, workers and RTSP (the scale-critical part)

### 6.1 Today (implemented)

- **One ffmpeg process per camera**, supervised by a Python worker (asyncio) that holds ≈ 12 cameras. ffmpeg: RTSP over TCP,
  `-fps` filter to `analysis_fps` (3), scale to ≤ 640 px, MJPEG quality 5, **`-threads 1`** for the encoder
  (a default 16-thread encoder used 86 MB per camera and was OOM-killed; one thread uses ≈ 22 MB), output pipe → JPEG splitter
  → Kafka `frames.raw` (key = camera, `compression=none`, linger 10–20 ms).
- **Registry assignment:** workers `POST …/heartbeat` every ≈ 5 s and receive their cameras; a worker silent for `lease_s`
  (15 s) loses them. Assignment is **sticky**, least-loaded first, respects **pins**, **drain**, **capacity override**.
  `restart` bumps `generation` and the worker restarts that camera's ffmpeg.
- **Weighted load (v1.8):** full camera = 1.0, idle camera = 0.1 (`IDLE_WEIGHT`); capacity (12 per worker locally) is in load units.
- **Autoscaler:** desired = clamp(ceil(Σload / (capacity × target_utilization 0.8)), MIN 2, MAX 10) (+1 if any camera unassigned),
  60 s cooldown, drain before stop. Modes `auto` / `manual`. Starts `fx-auto-N` containers from a template container.
- **Per-camera state machine:** `starting → online → stalled → reconnecting → offline | error`. Stall = no frame for > 5 s
  (30 s in idle mode, `heartbeat_s + 3 s` with the motion gate). Backoff 1 s → 30 s with jitter.

### 6.2 Demand-driven extraction (v1.8)

A camera is decoded at full rate **only when something needs it**:

| Situation | Mode | Cost |
|---|---|---|
| Has a plan, or someone opens its frames page | **full**: `analysis_fps` (3) | 1.0 |
| No plan and nobody watching | **idle**: keyframes only (`-skip_frame nokey`), 1 frame / 5 s (keeps health + thumbnails) | ≈ 0.1 |

The registry decides the mode (single decision point): `full ⇔ enabled ∧ (plan_id ≠ null ∨ frame_viewers > 0)`;
`full → idle` only after **60 s** without demand, `idle → full` immediately. The extractor applies it (restart of that camera's
ffmpeg, at most once per 10 s). Measured on the laptop: idle ffmpeg ≈ 2–3 % of a core vs ≈ 18 % full (≈ 85–90 % cheaper).

### 6.3 Motion gate (v1.9, opt-in per camera)

ffmpeg chain `fps=3, mpdecimate=hi:lo:frac:max, scale` with `-fps_mode vfr`: frames where nothing changed are **not sent on**
(one heartbeat frame at least every `heartbeat_s`). Presets (`low|medium|high`) live **only** in
`services/frame-extractor/extractor/motion_presets.py` (calibrated: medium = hi 1536, lo 1280, frac 0.33).
Measured: on 4 static cameras frames into Kafka **12.0 → 2.6 /s (−78 %)**, events caught 100 %, noise false-triggers ≈ 0.7 %,
moving (pan/zoom) clips skipped 0 %. It saves JPEG-encode, Kafka and **detector** load; decode cost is unchanged (the frame is
decoded before the comparison). Health must **not** apply the "fps < 70 % of target" rule to gated cameras.
Known limit: no ignore-zones (needs our own code, backlog #23).

### 6.4 RTSP error classes (v1.9)

The extractor classifies ffmpeg stderr (not exit codes): `auth_failed` (401), `not_found` (404/400 path), `codec_unsupported`,
`unreachable`, `stalled`, `decode_error`, `eof`. Terminal classes (auth / not found / codec) → state `error`, retry backoff
**30 s → 5 min**, reset when the config changes; the UI shows "Wrong credentials", "Stream path not found", etc.

### 6.5 Substream and relay (v1.9)

`analysis_stream_url` = the camera's low-resolution stream; the registry provisions a second MediaMTX path
`{id}-analysis`; extractors read `analysis_source_url ?? source_url`; the main path is pulled **on demand** only
(`sourceOnDemand`, close after 30 s) when a browser watches. Cameras you cannot configure: choose the smallest sufficient stream.

### 6.6 Decode cost (measured, Debian ffmpeg 7.1, Ryzen 7 PRO 4750GE, 15 fps source, CPU-seconds per second of video)

| Resolution | H.264 | `-skip_loop_filter all` | Keyframe-only (GOP 1 s) | H.265 |
|---|---|---|---|---|
| 360p | 0.068 | 0.063 | 0.031 | – |
| 540p | 0.122 | 0.104 | 0.048 | – |
| 720p | 0.195 | 0.180 | 0.070 | 0.246 |
| 1080p | 0.404 | 0.369 | 0.116 | 0.507 |

`-skip_loop_filter all` is on by default (−8…15 % CPU, −0.3 % detections at confidence ≥ 0.5; tested on 3 images only).
**Keyframe-only is not a general solution:** cameras usually send a keyframe every 1–4 s (0.25–1 fps), far below the 3 fps
analysis rate, and we cannot reconfigure them. Use it for idle mode and for cameras whose measured keyframe rate ≥ the
needed fps. Full source: `qa/perf/decode-bench/RESULTS.md`.

---

## 7. The client (React)

Stack: React 19, TypeScript, Vite, Tailwind v4, shadcn-style primitives, React Flow (graphs), TanStack Query, SignalR client,
WHEP player. Design direction **"control room, quiet"** (dark default + light theme, dense, calm, status colours only where
they mean something) — see `ui/DESIGN.md`. Routes are lazy-loaded; the app falls back to mock data when the gateway is
unreachable (banner shown); the `index.html` is served with `no-cache` (hashed assets are immutable) so a deploy is never hidden
by a stale browser cache.

**Performance rules for the client:** subscribe to a camera's detections only while its tile is **in view**; batch
subscribe/unsubscribe calls (one hub call per 100 ms); canvas overlay (not DOM) for boxes, **hold boxes until the next
detection** (dim with age) because detections can be seconds apart under load; throttle React updates (~2 Hz) for live step
status; never mount hidden video/WebRTC sessions (close WHEP sessions on unmount); virtualise or page long lists.

### 7.1 Pages

**Live wall** — a paged grid (1 / 2×2 / 3×3 / 4×4), search by name/tag, state filter, counts of online/degraded/offline;
each tile is live WebRTC video with detection boxes and a status dot; click → focus page.

![Live wall](screenshots/01-live-wall.png)

**Camera focus** — large live video with boxes, stream card (fps, last-frame age, worker, uptime), the camera's active plan
and its **live pipeline graph** (which step ran on the latest frame, ms, 60 s counters).

![Camera focus](screenshots/02-live-focus-pipeline.png)

**Extracted frames** — the raw frames of a camera *before* any analysis: big viewer, filmstrip with capture time, frame details
(frame id, seq, size, worker), "Live tail", optional "Show analysis" overlay. Opening it switches an idle camera to full rate.

![Extracted frames — one camera](screenshots/03-extracted-frames-camera.png)

**Extracted frames — all sources** — grid with the latest frame of every camera (only tiles in view are watched).

![Extracted frames — all sources](screenshots/04-extracted-frames-all-sources.png)

**Recognitions** — plates and faces as cards with a **snapshot**, confidence ring, camera, age; search by plate/name, filter by
camera/type, Live vs History; a known person shows their name.

![Recognitions](screenshots/05-recognitions.png)

**People ("known people lobby")** — enrol people with several photos; `identify_faces` matches against them (cosine on SFace
embeddings, threshold 0.363).

![People](screenshots/06-people.png)

**Pipelines** — describe a goal in words, pick cameras, **Draft plan** (graph preview, validation errors, "cached" badge),
then activate. Example chips come from recent drafts.

![Pipelines — new](screenshots/07-pipelines.png)

**Active plans** — list of plans with camera counts and versions, and **Watch**: the plan's graph with per-step live counters.

![Pipelines — watch live](screenshots/07c-pipeline-watch-live.png)

**Sources** — the camera registry (name, id, stream, plan badge, worker, state, duplicates flagged); add/edit/delete.

![Sources](screenshots/08-sources.png)

**Add camera** — id, name, **stream URL** (rtsp/rtsps/rtmp/srt/http m3u8 or `publisher`), optional **analysis (sub)stream**, analysis fps,
plan, tags, **Motion detection** (on/off, Low/Medium/High, heartbeat 1–60 s), enabled.

![Add camera](screenshots/09-sources-add-camera-motion.png)

**Operations** — **workers as boxes with their cameras inside** (status dot, fps, publisher vs RTSP host, `idle` badge),
load bar (weighted load / capacity), Kafka `frames.raw` rate → executor → engines on the right; worker table with
Capacity / Drain / Undrain; **autoscaler** panel (Auto/Manual, policy, current/desired workers, events).

![Operations — flow](screenshots/10-operations-flow.png)

Click a camera → side drawer (non-modal, the flow stays visible): **Info** (URLs, worker, pin, plan, motion, error class,
Restart / Pin), **Live** video with boxes, **Pipeline** (the plan running), **Frames** (raw frames). Click a worker → its facts
and actions.

![Operations — camera drawer](screenshots/11-operations-camera-panel.png)

**Health** — every stream, worst first: state (online / idle / degraded / offline), fps vs target, last-frame age, reconnects,
uptime, **motion skipped %**, **error class + hint**, duplicate cameras; summary tiles that double as filters.

![Health](screenshots/12-health.png)

**Monitoring (Grafana)** — dashboards `vision-pipeline` (extractor fps per camera, executor step latency p50/p95, consumer lag,
memory per service) and `frame-extraction` (per camera/worker rates, drops, restarts, mode).

![Grafana — vision pipeline](screenshots/13-grafana-vision-pipeline.png)

*(The "Kafka consumer lag" panel is empty because Redpanda 24.2 exposes no consumer-group lag metric, it is not a bug.
Use `rpk group describe <group>` or Redpanda Console until a lag exporter exists — backlog #13.)*

![Grafana — frame extraction](screenshots/14-grafana-frame-extraction.png)

---

## 8. Observability and operations

- **Metrics** `vp_<service>_<thing>_<unit>`: extractor (`frames_total`, `frames_dropped_total`, `camera_mode`,
  `mode_switches_total`, `frames_motion_skipped_total`), executor (`frames_total{result,tier}`, `frames_skipped_total{reason}`,
  `step_duration_seconds`, `detection_age_seconds`, `inflight`, `snapshot_failures_total`), registry (`cameras_by_mode`,
  `worker_load`, `duplicate_cameras`), realtime (`messages_in_total`, `sent_total`, `dropped_total{reason}`, `consumer_lag_ms`,
  `demand_published_total`), results, frame-catalog, stream-health, planner (`planner_cache_total`).
- **Kafka lag** per consumer group (Redpanda Console, `rpk group describe`). Lag vs the 5-minute `frames.raw` retention is the
  most important health number: a consumer more than a few seconds behind is losing frames.
- **SLOs to alert on (proposed):** detection age p95 (watched cameras ≤ 3 s), consumer lag < 5 s, ingest ≥ 90 % of
  `analysis_fps` per full camera, camera uptime, error-class counts, disk/memory headroom.
- **End-to-end check:** `bash infra/scripts/e2e-check.sh` (line endings, unhealthy services, plans, recognitions with
  snapshots, snapshot fetch, ingest fps per camera).
- **Troubleshooting** (in `infra/README.md`): Docker "unable to start" → quit, `wsl --shutdown`, restart; VM clock drift
  (`docker run --rm --privileged alpine date -u -s "@$(date -u +%s)"`); CRLF in scripts (guard script); port forwarding broken
  after restart → clean Docker restart.

---

## 9. Scalability

### 9.1 What 400 cameras at 3 fps means (arithmetic)

| Load | Per-unit measurement | At 400 cameras (1,200 frames/s) |
|---|---|---|
| Frames into Kafka | JPEG ≈ 25 KB at 640 px | ≈ **30 MB/s**, 5-minute retention ≈ 9 GB ring; ≈ 2.6 TB/day if it were kept |
| Decode (CPU, 15 fps source) | 0.068 (360p) / 0.122 (540p) / 0.404 (1080p) core per camera | **≈ 27 / 49 / 162 cores**; 25 fps sources cost ≈ 1.5× more; H.265 ≈ 1.25× |
| Object detection | YOLOX-Tiny 0.23 s/frame on an idle CPU core (YOLOX-S 0.92 s) | **≈ 280–550 CPU cores** → needs **GPUs** (≈ 2–3 with batching/TensorRT) |
| Detections rows | one row/class per frame | 100 M+ rows/day if every frame is stored → time-series store + rollups |
| Snapshots | cooldown-limited events | small; a durable object store is fine |
| Workers | ≈ 12 full cameras per 0.5 GB worker locally | ≈ 35 workers locally-sized; far fewer with GPU decode |

### 9.2 What is built for scale already

Horizontal workers with leases/rebalancing and an autoscaler; per-camera isolation; Kafka partitioning by camera
(48 partitions for frames); stateless executors scaled by consumer group; per-camera conflation and drop-oldest back-pressure;
demand-driven modes (no demand → ≈ 0 decode); motion gate; weighted capacity; progressive results; throttled realtime fan-out
(≤ 3/s/camera, nothing computed for cameras nobody watches); batched DB writes with offset-after-commit.

### 9.3 Measured limits so far (this laptop: 8 cores / 16 threads, 15 GB RAM with an 8 GB Docker cap, no GPU)

- Object store (SeaweedFS) saturated at ≈ **50 PUT/s** (p99 5.5 s) → frames moved into Kafka (v1.7); the store holds only snapshots.
- 100 simulated streams cost ≈ 1.7 cores / 476 MB in the simulator alone; with 31 registered cameras and ~25 full the host sits at
  ≈ 90 % CPU and the detector (≈ 4.5 cores) is the bottleneck: ≈ **3–6 analysed frames/s in total**. A *watched* camera got
  ≈ 0.4 analysed frames/s under that load (target 1.5; see backlog #24) before demand-driven modes were deployed together.
- **The 100-camera ramp (20 → 50 → 80 → 100, settle 2 min, measure 3 min) has a harness (`qa/perf/ramp.py`, plan in
  `contracts/SCALE-TEST.md`) but has not been run to completion** — do it after the next integration and write
  `qa/perf/SCALE-REPORT.md` (first saturating resource, 400-camera sizing).

### 9.4 Target architecture for 400+ (what to change, in order)

```
Cameras ─substream─► MediaMTX shards (pull on demand, optional recording)
                         │
        ┌────────────────▼─────────────────────────────┐   GPU node
        │ Edge worker: NVDEC decode → frame select →     │   fps set by the plan (0 = none), keyframe-only when the
        │ resize on GPU → motion gate → [detector +      │   camera allows, ring buffer 10–30 s, per-stream cost report,
        │ tracker in-process]                            │   RTSP error classes, make-before-break handover
        └──┬─────────────────────┬──────────────────────┘
   detections / frames.meta      │ crops / JPEG only when a later step (plate, face, OCR) needs them or a viewer asks
           ▼                     ▼
        Kafka            Executor (DAG step 2+, tiers: watched > SLA > background) ─gRPC, batched─► Triton: LPR · face · OCR
   recognitions → Postgres (partitioned) · detections → ClickHouse/Timescale (TTL + rollups) · snapshots → S3
   faces → pgvector/Qdrant · realtime gateway with Redis backplane · control plane unchanged (LLM planner → cache → registry HA → KEDA)
```

1. **Now (laptop, small):** plan-driven fps at the source (done), no default plan (done), watched-camera priority (done, needs the
   pipelining follow-up), idle mode (done), motion gate (done), `frames.meta` topic split from `frames.raw`, SLO alert on detection age and lag.
2. **First GPU server:** Triton + TensorRT FP16 with **dynamic batching** and gRPC; LPR/face/OCR take **crops**; **tracker (ByteTrack)**
   so plates/faces run once per tracked object (10–50× fewer heavy calls); NVDEC decode with GPU resize.
3. **Production design:** edge worker with detector in-process (no JPEG, no Kafka for step 1), ClickHouse for detections,
   vector DB for faces (the JSON-file gallery breaks with 2 replicas), Kubernetes + KEDA instead of the Docker socket,
   Redis SignalR backplane / sharded frame-catalog / registry HA with leader election, MediaMTX sharding, substreams everywhere,
   **authentication and secret storage** (RTSP credentials), cost-based placement (not camera counts), topology awareness.

---

## 10. Testing and quality

- **Unit tests per service** (no Docker): .NET xUnit (Registry 109, StreamHealth 42, Executor 43, RealtimeGateway 13, Results 18,
  FrameCatalog 14, ApiGateway…), Python pytest (extractor 112, worker-controller 19, planner, engines), UI vitest (107) + `tsc -b` + eslint.
- **Contract tests**: JSON schema validation of every message in tests; plan validator golden tests; classifier tests with real ffmpeg error strings.
- **Integration**: `infra/scripts/e2e-check.sh`; `docker compose ps` all healthy; Kafka lag; snapshot fetch.
- **Browser QA (Playwright, `qa/.venv`)**: drive the real UI, collect console errors and 5xx responses, check each page, heap growth
  over 40 s on the all-sources grid; bug reports in `qa/bugs/BUG-NNN.md`; **never** wait for `networkidle` (live apps never reach it) — use
  `domcontentloaded` + a timeout.
- **Performance**: `qa/perf/ramp.py` (ramp), `qa/perf/decode-bench/` (decode matrix, motion calibration), object-store bench. Measure
  **CPU-time** (rusage), repeat, report median and spread.
- **Definition of done for a feature:** unit tests, contract updated, deployed, **verified live with a number**, screenshot checked, backlog updated.

---

## 11. Delivery plan (how to build it from scratch)

### 11.1 Phases, owners and acceptance

| Phase | Content | Teams | Acceptance |
|---|---|---|---|
| 0 Foundations | contracts, compose (Redpanda, Postgres, object store, MediaMTX, Prometheus, Grafana), topic/bucket init, **sim cameras**, line-ending guard | manager, infra | all infra healthy; 20 sim RTSP streams visible in MediaMTX |
| 1 Ingest | extractor (ffmpeg per camera, supervision, backoff, status), registry (cameras, MediaMTX provisioning, leases), stream-health | ingest, .NET-A | 20 cameras at ≈ 3 fps each; killed worker → cameras move within lease; unstable cameras recover |
| 2 AI + executor | 4 engines (limits, 429), executor (plan runner, breakers, conflation), results (batched, snapshots) | AI, .NET-B | plan runs per frame; recognitions stored **with snapshots**; no breaker storms |
| 3 Planner | LLM structured output, validator, normaliser, cache, activation | planner | valid plan from a plain query, cache hit < 100 ms |
| 4 Client | live wall/focus (WHEP + canvas boxes), recognitions, people, sources, health, pipelines | UI, UI/UX | all pages work against the live backend, no console errors |
| 5 Ops | frame-catalog + Extracted frames, worker ops (drain/capacity/pin/restart), worker-controller, Operations page | .NET-A/B, ingest, UI | autoscaler adds/removes workers; flow page readable at 30 cameras |
| 6 Scale hardening | frames in Kafka, demand-driven modes, motion gate, substream, error classes, priority tiers, 100-camera simulator, **ramp test** | all + perf | ramp report with first saturating resource and 400-camera sizing |
| 7 Production path | GPU/Triton, edge worker + tracker, ClickHouse, vector DB, K8s/KEDA, auth, secrets, backplane, sharding | platform | load test at 400 cameras on a server |

### 11.2 Review checklist the manager applies to every report (be harsh)

Scale (what breaks at 400?), memory (budget kept? bounded queues?), CPU/latency (measured?), back-pressure (what drops, how
is it counted?), failure (restart of any single service: data loss? reshuffle?), idempotence, security (credentials, ports),
clean architecture (one responsibility, no shared DB), operability (metrics, health, logs), and **did they run it**.

### 11.3 Team brief template

```
You are the <team> lead. I am the CEO/approver: harsh on performance, memory, clean code. Report ≤ 200 words with measured numbers.
Project root … Binding spec: contracts/CONTRACTS.md section <x> (read fully). Use Bash.
Scope: <numbered tasks, only your folders>.
Tests: <what must pass>. Deploy: <exact compose commands>. Verify live: <curl/measurement>.
Constraints: disk/RAM limits, no new images, no other teams' code. If the contract is ambiguous pick the simplest reading and list it.
```

### 11.4 After each integration

Run the **bug-finding team** (Playwright: every page, console errors, 5xx, screenshots reviewed) → write bug reports → **fix team**
per bug → re-run the tests → update contracts/backlog → tell the user the URLs.

---

## 12. Lessons learned (do not repeat)

1. **Check that images exist.** MinIO images disappeared from Docker Hub/Quay → SeaweedFS S3 (single-container, `weed shell` init).
2. **Line endings:** CRLF in `.sh`, compose, YAML broke containers (`set: illegal option -`). Guard script + `.gitattributes`; fix with byte replacement, not `sed` loops in Git Bash.
3. **Redpanda:** use `--mode=dev-container`; cluster config goes through the admin API (`RPK_ADMIN_HOSTS`); do not mask init failures with `|| true`; auto-topic-creation off.
4. **librdkafka `queued.max.messages.kbytes` is for the whole consumer queue, not per partition.** A 512 KB / 1 MB setting starved the executor
   and the frame-catalog (23,000 frames behind, 97 % of frames dropped as stale). 8–16 MB is right for ~25 KB frames. Always compare produce rate with consume rate.
5. **Object stores are not a message bus:** SeaweedFS saturates at ≈ 50 PUT/s; frames live minutes and are read once or twice → keep them in Kafka (5-minute retention) and store only snapshots.
6. **Snapshots flood the disk** (≈ 20 GB/day at 20 cameras): recognition *cooldown* + snapshot TTL + retention. A recognition is an event, not a per-frame row.
7. **Circuit breakers must ignore 429.** A saturated engine answering 429 tripped the breaker and caused an outage; align executor concurrency with `MAX_CONCURRENCY + MAX_QUEUE`.
8. **Per-camera conflation + stale-drop** beat queues for live video. Commit offsets for the contiguous prefix only.
9. **ffmpeg:** `-rw_timeout` is invalid for RTSP (`-timeout` is the right one); the MJPEG encoder needs `-threads 1` (86 MB → 22 MB per camera, OOM rc = −9 otherwise); classify errors from **stderr text**, exit codes are useless.
10. **CPU detectors are slow and noisy under load:** YOLOX-Tiny 231 ms vs YOLOX-S 919 ms; the INT8 variant was incompatible with ONNX Runtime. Under a saturated host, give inference CPU priority (`cpu_shares`) and measure **CPU-time**, not wall-clock.
11. **LLM plans:** constrain with enums, validate, repair; `mode` comes from tool cost, not from the LLM; `when` on the first step is unsatisfiable. A 3B model on CPU needs ≈ 90 s; raise gateway (YARP) timeouts (default 100 s killed it → 502).
12. **Windows + Docker Desktop (WSL2):** the VM caches file data; Windows grew `pagefile.sys` to 14–18 GB until the disk filled and Docker crashed → `.wslconfig` (`memory=8GB`, `autoMemoryReclaim=dropCache`, `sparseVhd=true`); the VHDX does not shrink by itself (`diskpart compact vdisk`); clock drift after sleep; port forwarding breaks after a restart (clean restart); reboot to apply `.wslconfig`.
13. **Playwright + live apps:** never `networkidle`.
14. **Caching:** `index.html` must be `no-cache` or users keep seeing the old app after a deploy.
15. **UI realism:** overlay boxes must outlive the detection interval (otherwise nothing is visible when detections are seconds apart); do not show a fake "default plan" graph for cameras without a plan.
16. **Health under pressure:** CPU starvation makes ffmpeg stall for > 5 s and flaps health; add hysteresis, and do not apply the fps-vs-target rule to motion-gated cameras.
17. **A registry restart reshuffles cameras** if downtime > lease (15 s) → add a startup grace period (backlog #21).
18. **`PUT /api/cameras` replaces the whole record** (a partial body clears `plan_id`) → add `PATCH` (backlog #22).
19. **Leftover state from tests** (autoscaler left in manual mode, workers left draining) silently degraded the system → after QA/perf runs, always restore `auto` mode and undrain workers, and re-check `/api/workers`.
20. **Do not trust claims — including your own.** Several confident statements were corrected by measurement: "in-process decoding lets you skip frames" (only keyframes are skippable), "8× less CPU with keyframe-only" (needs camera settings you cannot control), "boxes missing = broken" (detections were seconds apart). Verify, then report.
21. **Agents can fight over Docker.** Parallel `docker compose build/up` from several teams saturates a laptop and can restart the whole stack; when the user says stop, message every agent first, then bring the stack down.

---

## 13. Environment and how to run

- **Current test machine:** Windows 10, Ryzen 7 PRO 4750GE (8 cores / 16 threads), 15 GB RAM, **no GPU**, Docker Desktop (WSL2) capped at 8 GB.
  Ollama on the host with `qwen2.5:3b` (`ollama pull qwen2.5:3b`).
- **Start:** `cd infra && docker compose up -d` (first time `--build`; use `COMPOSE_PARALLEL_LIMIT=1` on a laptop); wait until all services are healthy and the init jobs `Exited (0)`.
  Simulated cameras: `SIM_CAMERAS=100 docker compose up -d sim-cameras` (default 40). Stop everything: `docker compose down` (keeps data; `-v` wipes volumes).
- **URLs:** client http://localhost:5173 · gateway http://localhost:8000 · Grafana http://localhost:3000 (anonymous viewer) ·
  Prometheus http://localhost:9090 · Redpanda Console http://localhost:8080 · SeaweedFS browser http://localhost:8888 ·
  MediaMTX API http://localhost:9997/v3/paths/list · Kafka from the host `localhost:19092`.
- **Models that are NOT in the git repo:** three large ONNX files are missing from `models/` after a clone (GitHub uploads kept failing on this network).
  Download them into `models/` **before** `docker compose up` (the engines mount `models/` read-only; the detector and face engines load their models from there):
  `object_detection_yolox_2022nov.onnx` (YOLOX-S, 36 MB), `face_recognition_sface_2021dec.onnx` (SFace, 39 MB), `yolox_tiny.onnx` (20 MB).
  Sources and the check command are in `SETUP.md` section 3b. The three small models (`yolox_nano`, `object_detection_yolox_2022nov_int8bq`, `face_detection_yunet_2023mar`) are in the repo.
  `test-images/` and `qa/screenshots/` are also not in git (local test data only).
- **Questions to ask the user before building for real:** number and type of cameras (codec, resolution, fps, GOP, substream available?),
  GPU availability, retention requirements (snapshots, detections), single site or several, authentication needs, where credentials live.

---

## 14. Known issues and backlog (priority order)

| # | Item |
|---|---|
| 1 | Run the **100-camera ramp** and write `qa/perf/SCALE-REPORT.md`; size the 400-camera system |
| 2 | Watched camera analyses < 1 frame/s under load: allow **2 concurrent runs for watched cameras** (pipelining) and re-tune `MAX_INFLIGHT_FRAMES` (backlog #24) |
| 3 | Registry restart reshuffles cameras: startup grace period (#21); add `PATCH /api/cameras/{id}` (#22) |
| 4 | Split `frames.meta` from `frames.raw` (frame-catalog reads every JPEG just to index metadata); frame-catalog image fetch returns 503 for some older frames (serialised single-message fetch) |
| 5 | Recognition cards: show a placeholder while the snapshot loads |
| 6 | Motion gate: ignore zones / noise-floor learning; re-check `skip_loop_filter` on more images and bitrates (#23) |
| 7 | Retention jobs, Kafka lag exporter, SignalR backplane, StreamHealth HA, auth, face-gallery shared store, semantic plan cache (#20), GPU/Triton engines, detector model per deployment (YOLOX-S on GPU), late-binding executor (#18), plate search `q=` |
| 8 | Production path of section 9.4 (edge worker, ClickHouse, vector DB, K8s/KEDA, secrets) |

---

## 15. First message to the user, when you start

> I read the plan. I will (1) confirm your cameras and hardware, (2) update the contracts, (3) start the teams in parallel and report
> measured results, with URLs. Before I touch disk-heavy things I will check free space, and I will not delete anything that is not part of this system.
