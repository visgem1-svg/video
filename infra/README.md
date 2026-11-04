# Vision Pipeline - infra

Everything runs from `infra/docker-compose.yml` (project `vision-pipeline`, network `vp-net`).
Env vars: [ENV.md](ENV.md). Contracts: `../contracts/CONTRACTS.md`.

## Start
```
cd infra
docker compose up -d --build          # whole system
docker compose ps -a                  # wait: all healthy, init jobs "Exited (0)"
docker compose down                   # stop (keeps data);  down -v  wipes volumes (re-runs Postgres init)
```
**Docker Desktop / WSL2 memory:** create `%USERPROFILE%\.wslconfig` (then `wsl --shutdown`, restart Docker Desktop):
```
[wsl2]
memory=8GB
processors=8
swap=2GB
[experimental]
autoMemoryReclaim=gradual
```
**Line endings:** all scripts/configs must be LF (CRLF broke `minio-init`: `set: illegal option -`).
Run `bash scripts/check-line-endings.sh` (also called by `scripts/e2e-check.sh`); `.gitattributes` forces LF.

Ollama runs on the **host**: `ollama serve` and `ollama pull qwen2.5:3b` (planner uses host.docker.internal:11434).

### Infra only (no application code needed)
```
docker compose up -d redpanda-init minio-init postgres mediamtx sim-cameras redpanda-console prometheus grafana
```
(`redpanda` and `minio` are pulled in via `depends_on`.) Add applications one at a time, e.g.
`docker compose up -d --build registry`; their dependencies start automatically.

### Start order (enforced by `depends_on` conditions)
1. `redpanda`, `minio`, `postgres`, `mediamtx` (healthy)
2. `redpanda-init` (topics), `minio-init` (buckets) - one-shot, must exit 0; `sim-cameras` after mediamtx
3. `registry`, `stream-health`, `results`, `realtime-gateway`, `detector` / `lpr` / `ocr` / `face`
4. `executor` (needs detector healthy), `planner` (needs registry), `frame-extractor-1/2` (need registry + mediamtx)
5. `api-gateway` (needs all backends healthy), then `ui`

## URLs
| What | URL |
|---|---|
| UI | http://localhost:5173 |
| API gateway | http://localhost:8000 |
| Redpanda Console | http://localhost:8080 |
| MinIO console | http://localhost:9001 (minio / minio12345) |
| Grafana, dashboard "Vision Pipeline" (anonymous viewer; admin/admin) | http://localhost:3000 |
| Prometheus | http://localhost:9090 |
| MediaMTX API / metrics | http://localhost:9997/v3/paths/list , http://localhost:9998/metrics |
| WHEP direct | `POST http://localhost:8889/cam-001/whep` |
| RTSP | `rtsp://localhost:8554/cam-001` |
| Kafka from host | `localhost:19092` |
| Postgres | `localhost:5432` (vp/vp; DBs registry, planner, results) |
| Extractor metrics | http://localhost:9101/metrics (fx-1), http://localhost:9102/metrics (fx-2) |

## Services added since v1
* `frame-catalog` (5006): frame ring + frame images (v1.7: image fetched from the `frames.raw` message by partition/offset).
* `worker-controller` (5007): autoscaler, starts/stops `fx-auto-N` extractors through the Docker socket (dev only); Prometheus finds them via the
  `fx-sd` volume (file_sd job `extractors-auto`). `MIN_WORKERS=2`, `MAX_WORKERS=10`, capacity 12.
* **Contract v1.7 - frames travel in Kafka:** `frames.raw` carries the JPEG bytes (headers: `meta`, `content-type`); topic config
  `retention.ms=300000, segment.ms=60000, segment.bytes=64MB, max.message.bytes=2MB` (applied by `init-topics.sh` with `rpk topic alter-config`,
  so re-running converges). Extractors have no MinIO env any more; the `frames` bucket is kept but unused; `snapshots` is written by the executor.

## 100-camera scale test
`sim-cameras` publishes `cam-001..cam-100` (72 pre-baked clips: 70 % 960x540@15, 15 % 1280x720@25, 15 % 640x360@10; GOP 1 s; `-c copy`).
Unstable: cam-019, 020, 047, 073, 096. Measured at 100 streams: sim-cameras ~0.6 core / 476 MB (limit 768), mediamtx ~0.3 core / 28 MB.
Extractors use `FFMPEG_THREADS=1`, `JPEG_MAX_WIDTH=640`. MediaMTX has one regex path `~^cam-[0-9]{3}$` plus API-added paths. Plan: `contracts/SCALE-TEST.md`.

## Pieces
* **Topics** (`redpanda/init-topics.sh`): the 9 topics of CONTRACTS 5, exact partitions, `compact` or `delete` + retention (1h/6h/7d).
  Also enables per-group consumer-lag metrics. Auto topic creation is off.
* **Buckets** (`minio/init-buckets.sh`): `frames` (1-day lifecycle), `snapshots`.
* **Postgres** (`postgres/init/01-databases.sql`): runs only on an empty volume; after editing use `down -v`.
* **MediaMTX** (`mediamtx.yml`): `cam-001..cam-020`, RTSP 8554, WebRTC/WHEP 8889, API 9997, metrics 9998.
  *Browser WebRTC*: signalling goes browser -> api-gateway `/media` -> mediamtx:8889, but media flows browser -> host:8189 (UDP, TCP fallback)
  -> container. The SDP must advertise an address the browser can reach: `webrtcIPsFromInterfaces: no` hides the unreachable
  172.x docker IPs and `webrtcAdditionalHosts: [127.0.0.1, localhost]` advertises the published port on the host.
  For viewing from another PC add the server's LAN IP there.
* **sim-cameras**: built from `jrottenberg/ffmpeg:7.0-alpine`. At build time 5 looping 8 s clips (960x540, 15 fps, H.264 baseline, 500 kbps;
  panning still + moving red box from car/street/messi/lena/text images). At run time `supervisor.sh` publishes 20 streams with
  `-re -stream_loop -1 -c copy` (clip = (N mod 5)+1). A dead publisher is restarted after 2 s. **cam-019 and cam-020 are unstable**: killed and
  restarted every random 30-90 s; cam-020 also stays down 20-40 s in ~50 % of cycles. Restarts are logged as JSON lines.
* **Prometheus / Grafana**: scrapes every service, mediamtx:9998, redpanda `:9644/public_metrics`, minio. Dashboard panels: extractor frames/s per camera,
  ffmpeg restarts, executor step latency p50/p95 per tool, Kafka consumer lag, process memory per service.

## Memory (sum of `mem_limit`)
Infra: redpanda 1280, minio 256, postgres 256, mediamtx 128, sim-cameras 512, prometheus 256, grafana 192, console 192 = 3072 MB.
Apps: 6 x .NET 128 = 768; detector 512, lpr 384, ocr 512, face 384; planner 192; 2 x extractor 256; ui 64 = 3520 MB.
Base total **6592 MB** (before frame-catalog 128 + worker-controller 128 + up to 8 more auto extractors x 512 MB) (init jobs +192 MB briefly) plus ~2.5 GB Ollama on the host.
Redpanda is 1280 MB instead of the 1 GB contract figure on purpose: Seastar `--memory 1G` is the heap only, a 1 GB cgroup limit risks an OOM kill.

## Troubleshooting
* `docker compose ps -a`; init jobs must be `Exited (0)`; `docker compose logs redpanda-init`.
* A service stuck `unhealthy`: `docker compose logs <svc>`; healthchecks need `GET /health` = 200 (see image requirements in ENV.md).
* Laptop out of memory: start infra only, add services one by one, watch `docker stats`.
* Black video in browser: `docker compose logs sim-cameras`, `http://localhost:9997/v3/paths/list` (`ready: true`), port 8189 udp/tcp not blocked by VPN/firewall.
* cam-019/020 flapping is intentional (exercises reconnect and health logic).
* Re-create topics/buckets: `docker compose run --rm redpanda-init` / `minio-init` (idempotent).
* Stale data from an old run: `docker compose down -v`.
* **Docker Desktop "unable to start"** (WSL VM `docker-desktop` shows `Stopped` in `wsl -l -v`): quit Docker
  Desktop, `wsl --shutdown`, start Docker Desktop again, then `docker compose up -d`. Data volumes survive.
* **Docker VM clock drift** (common after laptop sleep; UI shows wrong "last frame age", `logs --since` misses
  lines): compare `date -u` with `docker run --rm alpine date -u`; fix with
  `docker run --rm --privileged alpine date -u -s "@$(date -u +%s)"` (Git Bash).
* End-to-end check of the running stack: `bash infra/scripts/e2e-check.sh` (Git Bash).
* Object store is **SeaweedFS** (MinIO images were removed from Docker Hub); browse buckets at http://localhost:8888.
