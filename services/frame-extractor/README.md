# frame-extractor

RTSP -> ffmpeg (1 process/camera) -> JPEG -> Kafka `frames.raw` (contract 9.7: value = raw JPEG, header `meta` = JSON, `content-type: image/jpeg`, key = camera_id); publishes `camera.status`. No object store.
Control loop: every 5 s `POST {REGISTRY_URL}/api/workers/{WORKER_ID}/heartbeat {capacity, running}`; the
`assigned` list is the desired state (start new, stop removed, restart on `source_url`/`analysis_fps` change,
disabled = stopped). Registry unreachable: running cameras are kept, heartbeat retried every 5 s.

## Run / test
```
python -m venv .venv && .venv/Scripts/pip install -r requirements-dev.txt
.venv/Scripts/python -m pytest        # no Docker, network or ffmpeg needed
python -m extractor.main              # env below
```

## Environment (names per infra/ENV.md, binding)
| Var | Default | |
|---|---|---|
| `WORKER_ID` | hostname | registry identity, `worker_id` in messages |
| `WORKER_CAPACITY` | 10 | sent in heartbeat (25 on a real server) |
| `REGISTRY_URL` | http://registry:5001 | |
| `KAFKA_BOOTSTRAP` | redpanda:9092 | |
| `FFMPEG_PATH` | ffmpeg | |
| `FFMPEG_HWACCEL` | (empty) | e.g. `cuda`, `vaapi`, `qsv`, `d3d11va` -> `-hwaccel X` |
| `JPEG_MAX_WIDTH` | 960 | `scale='min(W,iw)':-2` (never upscales) |
| `LOG_LEVEL` | INFO | JSON logs on stdout |
| `PORT` | 9101 | `/health`, `/metrics` |
| `FFMPEG_SKIP_LOOP_FILTER` | off | `-skip_loop_filter all` before `-i` (contract 9.9; cheaper decode, quality unproven) |
| optional: `FFMPEG_THREADS` 2, `HEARTBEAT_INTERVAL_S` 5, `STALL_TIMEOUT_S` 5, `STARTUP_TIMEOUT_S` 15, `STATUS_INTERVAL_S` 2 | | |

## Design
* **ffmpeg subprocess per camera** (vs PyAV/OpenCV): decoding runs in a separate process, so the GIL and the
  asyncio loop never see pixels; one Python process supervises 25 cameras with ~1 core of Python work total.
  A crashed/hung decoder (common with bad RTSP) is killed and restarted without risking the worker; PyAV/OpenCV
  decode inside the process, hangs in native `read()` cannot be cancelled, leaks/segfaults take down all
  cameras, and frame conversion to numpy + JPEG encode in Python costs extra copies. ffmpeg does fps-drop,
  scale and MJPEG encode natively and hands us ready JPEG bytes (no re-encode in Python). Cost: one process
  (~25-45 MB RSS) per camera and a pipe copy of ~60-100 KB/frame, negligible.
* JPEG framing: SOI/EOI split on a single `bytearray` with a persistent scan offset (no rescans, one copy per frame).
  Dimensions read from the SOF segment. `-q:v 5` ~ JPEG quality 80-85 (contract says 80).
* Frames go straight from the reader to `produce()` (non-blocking enqueue, no per-camera queue or upload threads).
  Producer: `compression=none` (JPEG), linger 10 ms, `acks=1`, `message.max.bytes` ~2 MB, queue bounded to ~2 s of
  frames (`WORKER_CAPACITY x 3 fps x 2 s x 200 KB`, min 8 MB). When the queue is full the new frame is dropped and
  counted in `vp_extractor_frames_dropped_total` (librdkafka cannot evict the oldest queued message; with a ~2 s
  bound the staleness is capped either way). Delivery failures also count as drops.
* Kafka via `confluent-kafka`: batching/IO in native threads, far less CPU than aiokafka at ~1,200 msg/s.
* State machine (contract 5.5): `starting -> online -> stalled -> reconnecting -> online`; `error` after 5
  consecutive sessions without a frame (or ffmpeg cannot be spawned; `error` text = ffmpeg stderr tail, retries
  continue at max backoff); `offline` = camera stopped/unassigned (final status published). Watchdog: no frame
  >5 s (15 s before the first frame of a session) = `stalled` -> kill -> backoff 1,2,4..30 s +-20 % jitter (capped
  at 30 s); backoff resets after 30 s of stable online. `fps_actual` = frames in last 10 s / 10.
* Shutdown: SIGTERM/SIGINT -> stop control loop, kill all ffmpeg, final `offline` statuses, flush Kafka.
  Windows: the process joins a Job Object with KILL_ON_JOB_CLOSE, so ffmpeg children die even if we crash.
  Container: `tini` as PID 1.

## Metrics (`:9101/metrics`)
`vp_extractor_frames_total{camera_id}`, `vp_extractor_frames_dropped_total{camera_id}`,
`vp_extractor_ffmpeg_restarts_total{camera_id}`, `vp_extractor_camera_state{camera_id,state}`,
`vp_extractor_frames_motion_skipped_total{camera_id}`, `vp_extractor_cameras_running`, plus
`vp_extractor_publish_errors_total{stage}`, `vp_extractor_bad_frames_total`. Series of removed cameras are dropped.

## Motion gate and error classes (contract 9.9)
* Heartbeat item `motion` `{enabled, sensitivity, heartbeat_s}` (missing/disabled = unchanged chain) and
  `analysis_source_url` (read instead of `source_url`). Full mode only:
  `fps=F,mpdecimate=hi:lo:frac:max=M,scale=...` + `-fps_mode vfr`, `M = ceil(heartbeat_s*F)-1`. Numbers per
  sensitivity: `extractor/motion_presets.py` only. A change restarts ffmpeg in place (coalesced, 1 per 10 s).
* Gate counter: `-progress pipe:2 -stats_period 1`; dropped = round(dt_out_time * F) - (frame delta) since the first
  report. Stream-time based, so immune to CPU starvation and to the live stream's non-zero first pts; costs ~12 short
  stderr lines per second per gated camera and nothing otherwise. `camera.status.motion_skipped_pct` = rolling 60 s.
* `extractor/errors.py` classifies ffmpeg stderr (exit codes are useless: 8 for 400/401/404/unknown protocol).
  Terminal classes (`auth_failed`, `not_found`, `codec_unsupported`) -> state `error`, retry 30 s -> 5 min, reset on
  motion/analysis-URL change or first frame. MediaMTX answers 400 for an unconfigured path, 404 for a configured one
  without a publisher; both are `not_found`.
* Trying the gate without the registry field: run a throwaway extractor with a stub registry that returns
  `assigned:[{..., "motion": {"enabled": true}}]` (see `tests/test_motion.py` for the config shapes).

## Capacity (ESTIMATES, not measured: no ffmpeg available locally)
| per camera | 640x360 / 720p sim | 1080p25 H.264 software |
|---|---|---|
| CPU (ffmpeg, 2 threads) | ~0.03-0.05 core | ~0.10-0.15 core |
| RSS (ffmpeg) | ~25 MB | ~45 MB |
| Python side | ~1-2 MB + queue <1 MB | same; parent baseline ~60 MB |

* Budget check: 10 cameras = ~60 + 10x25 = ~310 MB at 360p, ~510 MB at 1080p, i.e. **the 256 MB contract budget
  is tight/insufficient** even for small sim streams; propose 384 MB (360p) / 640 MB (1080p) per replica.
* 400 cameras at 25/worker = 16 workers (+2 spare = 18); each ~1.5 GB RAM and 2.5-4 cores at 1080p software
  (total ~50-60 cores, ~25 GB), ~1/3 of that with substreams.
* Server-scale levers: **camera substreams** (use 640x360/720p stream URLs in `source_url`: the biggest win);
  **`-skip_frame nokey`** (decode only I-frames; with 1-2 s GOP gives ~0.5-1 fps, so only for low-fps analysis -
  not enabled since 3 fps needs more); `FFMPEG_HWACCEL=cuda|qsv|vaapi` (NVDEC decode offload, scale/encode stay on CPU);
  `fps` filter after decode already drops frames cheaply. Network: ~1,200 msg/s, ~100 MB/s total into Kafka (spread over workers).
