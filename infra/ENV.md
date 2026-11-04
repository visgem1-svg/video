# Environment variables set by docker-compose.yml

Services must read exactly these names (service READMEs are checked against this list).
Dev credentials are the documented defaults of CONTRACTS section 3.

## Common
| Var | Value | Used by |
|---|---|---|
| `KAFKA_BOOTSTRAP` | `redpanda:9092` | all .NET services, planner, frame-extractor (host tools: `localhost:19092`) |
| `LOG_FORMAT` | `json` | all (informational) |
| `PORT` | service port | Python services (engines 8101-8104, planner 8010, frame-extractor 9101) |
| `ASPNETCORE_URLS` | `http://+:<port>` | .NET (5001 registry, 5002 stream-health, 5003 executor, 5004 results, 5005 realtime-gateway, 8000 api-gateway) |
| `DOTNET_gcServer=0`, `DOTNET_GCHeapHardLimit=0x6000000` (hex, 96 MB), `DOTNET_TieredPGO=0`, `DOTNET_EnableDiagnostics=0` | | .NET |

## MinIO / object store (results, executor, frame-catalog; NOT the extractors since contract v1.7)
`MINIO_ENDPOINT=minio:9000`, `MINIO_ACCESS_KEY=minio`, `MINIO_SECRET_KEY=minio12345`, `MINIO_USE_SSL=false`,
`MINIO_FRAMES_BUCKET=frames`, `MINIO_SNAPSHOTS_BUCKET=snapshots`.

## Databases
| Var | Value | Service |
|---|---|---|
| `POSTGRES_CONNECTION` (Npgsql) | `Host=postgres;Port=5432;Database=registry;Username=vp;Password=vp;Maximum Pool Size=10` | registry |
| `POSTGRES_CONNECTION` | same, `Database=results` | results |
| `DATABASE_URL` (libpq URL) | `postgresql://vp:vp@postgres:5432/planner` | planner |

## Service URLs
| Var | Value | Service |
|---|---|---|
| `REGISTRY_URL` | `http://registry:5001` | frame-extractor-1/2, stream-health, planner, api-gateway |
| `STREAM_HEALTH_URL` | `http://stream-health:5002` | api-gateway |
| `RESULTS_URL` | `http://results:5004` | api-gateway |
| `REALTIME_GATEWAY_URL` | `http://realtime-gateway:5005` | api-gateway |
| `PLANNER_URL` | `http://planner:8010` | api-gateway |
| `MEDIAMTX_WHEP_URL` | `http://mediamtx:8889` | api-gateway (`/media/**`, prefix stripped) |
| `CORS_ORIGINS` | `http://localhost:5173` | api-gateway |
| `DETECTOR_URL` / `LPR_URL` / `OCR_URL` / `FACE_URL` | `http://detector:8101` / `http://lpr:8102` / `http://ocr:8103` / `http://face:8104` | executor |

## Per service
* registry: `LEASE_S=15`, `MEDIAMTX_API_URL=http://mediamtx:9997` (path provisioning; mediamtx allows the `api` action for any compose-network host, dev only).
* executor: `EXECUTOR_ID=ex-1`.
* frame-extractor-1 / -2: `WORKER_ID=fx-1` / `fx-2`, `WORKER_CAPACITY=10`, `PORT=9101` (host ports 9101 / 9102).
  Extractors read RTSP from the `source_url` in the camera config (`rtsp://mediamtx:8554/cam-xxx`).
* engines: `MODELS_DIR=/models` (read-only mount), `XDG_CACHE_HOME=/cache` (volume `engines-cache`), `OMP_NUM_THREADS=2`;
  face also `FACE_DATA_DIR=/data` (volume `face-gallery`).
* planner: `OLLAMA_URL=http://host.docker.internal:11434`, `OLLAMA_MODEL=qwen2.5:3b`
  (`extra_hosts: host.docker.internal:host-gateway`).
* sim-cameras: `RTSP_BASE` (default `rtsp://mediamtx:8554`).
* ui: no runtime env; the API base (`http://localhost:8000`) must be built in / proxied by the UI image.

## Image requirements implied by the healthchecks
* Python images: any base (probe uses `python -c urllib`); `GET /health` must return 200.
* **.NET images must contain busybox `wget` (use `-alpine` runtime images, not chiseled/distroless)**; otherwise
  the team must ship its own probe and tell infra. Probe: `wget -qO- http://127.0.0.1:<port>/health`.
* ui image: nginx-alpine (wget) serving `/` on port 80.
* Metrics: `GET /metrics` on the service port (frame-extractor: 9101).

## Added for the scale test / v1.6-1.7
* frame-extractor-1/2 (and `fx-auto-N` copies made by worker-controller): `FFMPEG_THREADS=1`, `JPEG_MAX_WIDTH=640`; no `MINIO_*` (frames go to Kafka).
* minio-init: `FRAMES_TTL` (default 10m, 5m in compose; irrelevant once frames leave the store), `SNAPSHOT_TTL=1d`.
* results: `RECOGNITION_RETENTION_DAYS=1`.
* registry: `MEDIAMTX_API_URL=http://mediamtx:9997`.
* frame-catalog (5006): `FRAMES_PER_CAMERA=600`.
* api-gateway: `FRAME_CATALOG_URL=http://frame-catalog:5006`, `WORKER_CONTROLLER_URL=http://worker-controller:5007`.
* worker-controller (5007): `DOCKER_NETWORK=vp-net`, `TEMPLATE_CONTAINER=vision-pipeline-frame-extractor-1-1`, `FILE_SD_PATH=/sd/extractors.json`,
  `WORKER_CAPACITY=12`, `MIN_WORKERS=2`, `MAX_WORKERS=10`, `MODE=auto`; mounts `/var/run/docker.sock` and volume `fx-sd`.
* sim-cameras: `CAMERAS` (default 100), `RTSP_BASE`.
