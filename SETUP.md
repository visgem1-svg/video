# SETUP - install and run the Vision Pipeline

Full guide: everything to install, how to start the system, how to check it, how to stop it.
Architecture and feature plan: [plan/plan.md](plan/plan.md). Contracts: [contracts/CONTRACTS.md](contracts/CONTRACTS.md).
Infra details and troubleshooting: [infra/README.md](infra/README.md), env vars: [infra/ENV.md](infra/ENV.md).

---------------------------------------------------------------------------------------------------

## 1. What you need

### 1.1 Hardware (minimum for ~40 simulated cameras)
| Resource | Minimum | Comfortable |
|---|---|---|
| RAM | 16 GB (8 GB given to Docker) | 32 GB |
| CPU | 8 cores | 12+ cores |
| Free disk | **40 GB** (Docker images + volumes + Docker VM disk) | 80 GB |
| GPU | not required (CPU inference) | NVIDIA GPU needed for 400+ real cameras (see plan.md section 9.4) |

The Docker disk file grows over time and does not shrink by itself; keep free space on the drive that holds Docker.

### 1.2 Software to install
| # | Software | Version | Needed for | Download |
|---|---|---|---|---|
| 1 | **Docker Desktop** (with WSL2 backend on Windows) | 4.30+ | runs the whole system (28 containers) | https://www.docker.com/products/docker-desktop |
| 2 | **WSL2** (Windows only) | latest | Docker backend | `wsl --install` in an admin PowerShell |
| 3 | **Git** (Git Bash on Windows) | 2.40+ | clone repo, run `.sh` scripts | https://git-scm.com |
| 4 | **Ollama** | latest | the LLM that turns plain English into pipelines | https://ollama.com/download |
| 5 | Node.js | 20 or 22 LTS | only to develop/build the UI outside Docker | https://nodejs.org |
| 6 | .NET SDK | 10.0 | only to build/test the .NET services outside Docker | https://dotnet.microsoft.com/download |
| 7 | Python | 3.12 or 3.13 | only to run Python services/tests outside Docker | https://www.python.org |
| 8 | ffmpeg | 7.x | only for local experiments (the containers bring their own) | https://ffmpeg.org/download.html |
| 9 | Playwright (optional) | latest | UI screenshots / UI bug-finding runs (`pip install playwright && playwright install chromium`) | https://playwright.dev |

**To only RUN the system you need items 1-4.** Items 5-9 are for development.

### 1.3 Everything else is pulled by Docker (nothing to install by hand)
Redpanda (Kafka), SeaweedFS (S3 object store, service name `minio`), PostgreSQL, MediaMTX (RTSP/WebRTC),
Prometheus, Grafana, Redpanda Console, nginx, ffmpeg inside the extractor and sim-cameras images,
Python 3.13 and .NET 10 base images, ONNX Runtime, OpenCV, FastAPI, fast-alpr, RapidOCR.

AI models:
* `models/*.onnx`: small ones are in this repo; three large ones must be downloaded (section 3b): YOLOX-S, SFace, YOLOX-tiny.
* LPR (license plate) models download once into the `engines-cache` volume on the first start of the `lpr` container (needs internet once).
* The LLM model is pulled by Ollama (section 3).

---------------------------------------------------------------------------------------------------

## 2. Windows one-time preparation

1. Enable virtualization in BIOS, then in an admin PowerShell: `wsl --install` and reboot.
2. Install Docker Desktop, tick "Use WSL 2 based engine".
3. Limit the Docker VM memory. Create `%USERPROFILE%\.wslconfig`:
   ```
   [wsl2]
   memory=8GB
   processors=8
   swap=2GB

   [experimental]
   autoMemoryReclaim=dropCache
   sparseVhd=true
   ```
   Apply with: `wsl --shutdown`, then start Docker Desktop again.
4. Git: keep **LF** line endings for scripts. The repo has a `.gitattributes`; do not run `git config core.autocrlf true` for this repo.
   (CRLF in a `.sh` file breaks the `minio-init` container.)
5. Free disk check: `docker system df` (Docker shows how much it uses).

Linux / macOS: install Docker Engine (or Docker Desktop) and Ollama; steps 1-3 are not needed.

---------------------------------------------------------------------------------------------------

## 3. Install the LLM (runs on the host, not in Docker)

```bash
ollama serve                 # starts automatically after install on Windows/macOS; run it manually on Linux
ollama pull qwen2.5:3b       # ~2 GB, one time
ollama list                  # must show qwen2.5:3b
```
The planner container reaches it at `http://host.docker.internal:11434` (set in docker-compose).
Without Ollama everything else works, but "build a pipeline from a text query" returns an error.

### 3b. Download the large models (not stored in this repo)
Three model files are too large for the GitHub upload and are NOT in the repo. Download them into `models/` **before** `docker compose up`
(the engines mount `models/` read-only). The small ones (`yolox_nano.onnx`, `object_detection_yolox_2022nov_int8bq.onnx`,
`face_detection_yunet_2023mar.onnx`) are already in the repo.

| File (put in `models/`) | Size | Source |
|---|---|---|
| `object_detection_yolox_2022nov.onnx` (YOLOX-S) | 36 MB | OpenCV Model Zoo: https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_yolox (or https://huggingface.co/opencv/object_detection_yolox) |
| `face_recognition_sface_2021dec.onnx` (SFace) | 39 MB | OpenCV Model Zoo: https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface (or https://huggingface.co/opencv/face_recognition_sface) |
| `yolox_tiny.onnx` | 20 MB | YOLOX releases: https://github.com/Megvii-BaseDetection/YOLOX/releases (ONNX assets) |

Check afterwards: `ls -la models/` shows all six `.onnx` files.

---------------------------------------------------------------------------------------------------

## 4. Get the code and start the system

```bash
git clone https://github.com/visgem1-svg/video.git
cd video/infra            # (the folder name is the repo name)
docker compose up -d --build
docker compose ps -a      # wait until every service is "healthy"; init jobs must be "Exited (0)"
```
First build takes 10-25 minutes (downloads images, builds 12 .NET/Python images, builds the sim-camera clips).
Later starts take about 1-2 minutes.

### Start order (done by `depends_on`, for understanding)
1. `redpanda`, `minio` (SeaweedFS), `postgres`, `mediamtx`
2. `redpanda-init` (creates the Kafka topics), `minio-init` (creates buckets), `sim-cameras`
3. `registry`, `stream-health`, `results`, `realtime-gateway`, `frame-catalog`, engines `detector` / `lpr` / `ocr` / `face`
4. `executor`, `planner`, `frame-extractor-1/2`, `worker-controller` (autoscaler)
5. `api-gateway`, then `ui`

### Start only part of it (low memory)
```bash
docker compose up -d redpanda-init minio-init postgres mediamtx sim-cameras redpanda-console prometheus grafana   # infra only
docker compose up -d --build registry                                                                           # then one service at a time
```

### Number of simulated cameras
Default is 40. Change with an env var, then recreate sim-cameras:
```bash
SIM_CAMERAS=100 docker compose up -d sim-cameras        # PowerShell: $env:SIM_CAMERAS=100; docker compose up -d sim-cameras
```
Cameras `cam-019`, `cam-020` and others flap on purpose (they test reconnect and health logic).

---------------------------------------------------------------------------------------------------

## 5. Where to look (URLs)

| What | URL |
|---|---|
| **UI** (press Ctrl+Shift+R once after an update) | http://localhost:5173 |
| UI: Operations (workers, load, autoscaler) | http://localhost:5173/operations |
| UI: Extracted frames | http://localhost:5173/frames |
| API gateway | http://localhost:8000 |
| Grafana (dashboard "Vision Pipeline"; admin / admin) | http://localhost:3000 |
| Prometheus | http://localhost:9090 |
| Redpanda Console (Kafka topics, messages, consumer groups) | http://localhost:8080 |
| Object store browser (SeaweedFS) | http://localhost:8888 |
| MediaMTX paths API | http://localhost:9997/v3/paths/list |
| RTSP from the host | `rtsp://localhost:8554/cam-001` |
| Extractor metrics | http://localhost:9101/metrics , http://localhost:9102/metrics |
| Postgres | `localhost:5432` (user `vp`, password `vp`) |
| Kafka from the host | `localhost:19092` |

Dev-only default credentials (documented in contracts section 3) are used everywhere. **Change them and add auth before any real deployment.**

---------------------------------------------------------------------------------------------------

## 6. First steps in the UI

1. Open http://localhost:5173. The live wall shows the simulated cameras.
2. Open a camera: you see live video (WebRTC) and the detections drawn on it.
3. Type a plain-English query in the query box, e.g. "find red cars and read the license plate".
   The LLM builds a pipeline (a DAG of tools); the UI shows it as a graph and validates it before it runs.
4. Add your own camera: Sources -> Add camera -> paste the RTSP URL (optionally a lower-resolution "analysis" stream URL).
   Motion gating and its sensitivity (low / medium / high) are per camera.
5. Known people (faces): add photos in the people library; recognitions appear with a snapshot.
6. Operations page: which extractor worker handles which camera, load per worker, the autoscaler (auto / manual).

---------------------------------------------------------------------------------------------------

## 7. Check that it works

```bash
bash infra/scripts/e2e-check.sh          # Git Bash; end-to-end check of the running stack
bash infra/scripts/check-line-endings.sh # scripts must be LF
docker compose ps -a                      # (from infra/)
curl -s http://localhost:8000/health
curl -s http://localhost:9997/v3/paths/list | head
```
Expected: all services healthy, recognitions arrive with snapshots, frames ingest at roughly 1 fps x active cameras (3 fps for watched / planned cameras).

---------------------------------------------------------------------------------------------------

## 8. Stop, restart, clean

```bash
docker compose stop                 # stop, keep containers
docker compose down                 # remove containers, KEEP data (volumes)
docker compose down -v              # remove containers AND all data (Kafka, DB, snapshots)
docker compose restart <service>    # e.g. frame-catalog results
docker compose logs -f <service>    # follow logs
docker builder prune                # free build cache
```
After a Docker/laptop restart some services can start before their dependencies and stay `unhealthy`:
`docker compose restart frame-catalog results` (or the unhealthy service) fixes it.

---------------------------------------------------------------------------------------------------

## 9. Develop and test outside Docker (optional)

| Part | Folder | Commands |
|---|---|---|
| .NET services (7 + tests) | `services/dotnet/` | `dotnet test services/dotnet/TeamA.slnx` (also TeamB, TeamC) |
| Frame extractor | `services/frame-extractor/` | `python -m venv .venv`, `pip install -r requirements.txt -r requirements-dev.txt`, `pytest` |
| Planner (LLM) | `services/planner/` | same pattern; needs Ollama running |
| AI engines | `services/engines/` | see [services/engines/README.md](services/engines/README.md) (one service per pytest process) |
| Worker controller | `services/worker-controller/` | `pip install -r requirements.txt -r requirements-dev.txt`, `pytest` |
| UI | `ui/` | `npm ci`, `npm run dev` (dev server), `npm run build`, `npm run lint` |
| Load / ramp test | `qa/perf/` | `python qa/perf/ramp.py` (plan: [contracts/SCALE-TEST.md](contracts/SCALE-TEST.md)), decode benchmark in `qa/perf/decode-bench/` |

---------------------------------------------------------------------------------------------------

## 10. Troubleshooting (most common)

| Symptom | Fix |
|---|---|
| Docker Desktop "unable to start" | quit Docker Desktop, `wsl --shutdown`, start Docker Desktop again, `docker compose up -d` (volumes survive) |
| A service is `unhealthy` | `docker compose logs <service>`; then `docker compose restart <service>` |
| `minio-init` fails with `set: illegal option -` | CRLF line endings; run `bash infra/scripts/check-line-endings.sh`, re-checkout with LF |
| Out of memory / laptop freezes | start infra only (section 4), lower `SIM_CAMERAS`, check `.wslconfig` memory cap, `docker stats` |
| Black video in the browser | `docker compose logs sim-cameras`; `http://localhost:9997/v3/paths/list` must show `ready: true`; UDP/TCP port 8189 must not be blocked by VPN/firewall |
| Query returns an error | Ollama not running or model missing: `ollama serve`, `ollama pull qwen2.5:3b` |
| UI shows old version | Ctrl+Shift+R (index.html is served with no-cache) |
| Wrong "last frame age", `logs --since` misses lines | Docker VM clock drift after sleep: `docker run --rm --privileged alpine date -u -s "@$(date -u +%s)"` (Git Bash) |
| Disk full / `docker_data.vhdx` huge | `docker system prune`, `docker builder prune`; compact the VHDX with Docker stopped (`Optimize-VHD -Mode Full`, admin) |
| Stale data from an old run | `docker compose down -v` |

---------------------------------------------------------------------------------------------------

## 11. Scaling beyond the laptop (400+ cameras)

The laptop setup is a functional and design reference. For 400+ real RTSP cameras at 3 fps you need GPU decode and
inference, more Kafka brokers, a real database for detections and a Kubernetes-style scheduler. The concrete numbers
(CPU cores for decode, Kafka throughput, GPU sizing) and the target architecture are in [plan/plan.md](plan/plan.md) section 9.
