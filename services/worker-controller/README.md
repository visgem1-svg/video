# worker-controller (autoscaler, DEV ONLY)

Starts/stops `frame-extractor` containers through the Docker API so the number of workers follows the number of
enabled cameras (CONTRACTS 9.5). Port 5007, 128 MB. **The Docker socket is root-equivalent**: the code only reads
the template container and only stops/removes containers labelled `vp.managed-by=worker-controller`
(`runtime.remove` re-checks the label). Static workers (`frame-extractor-1/2`) are never touched.
Production = Kubernetes HPA/KEDA on a Deployment (BACKLOG); this service is not deployed there.

## Control loop (every `INTERVAL_S`)
1. Read `GET /api/cameras`, `GET /api/workers`, list labelled containers (adoption = stateless: labelled containers
   are always re-discovered; a registry-side `draining` managed worker resumes its drain after a restart).
2. Dead (exited) managed containers are removed. Finished drains (0 cameras) are stopped+removed.
3. `desired = clamp(ceil(enabled / (capacity*utilization)), min, max)`, +1 if an enabled camera has no owner (capped by max).
   `current` = live workers + managed containers still starting (no duplicates before first heartbeat).
4. At most ONE action per tick, `cooldown_s` after any action: scale up = create container
   `vp-fx-auto-N` (`WORKER_ID=fx-auto-N`, env/mem_limit/healthcheck/labels copied from the template, label
   `vp.managed-by=worker-controller`, `unless-stopped`); scale down = pick the least-loaded managed worker ->
   `POST /api/workers/{id}/drain` -> wait until it owns 0 cameras -> stop + remove. `drain_timeout_s` exceeded ->
   `undrain` (cancelled). A pending drain is also cancelled if load returns. Never below `min_workers` (static included).
5. Rewrites `FILE_SD_PATH` atomically (tmp + rename) when the managed set changes.

## API (gateway `/api/autoscaler/**`)
`GET /api/autoscaler` -> `{mode, policy, current, desired, manual_target, last_error, workers:[{worker_id, managed_by,
registry_state, draining, cameras, capacity, container:{name,state,health,ip}}], events:[last 100 {ts,action,worker_id,reason}]}` ·
`PUT /api/autoscaler/policy` (partial body, 422 + messages) · `PUT /api/autoscaler/mode {"mode":"auto|manual"}` ·
`POST /api/autoscaler/scale {"workers":n}` (manual only, else 409; n within min..max; same drain-safe path) ·
`/health` · `/metrics`: `vp_controller_workers{state}`, `vp_controller_decisions_total{action}`, `vp_controller_desired_workers`.

## Environment
| Var | Default | |
|---|---|---|
| `PORT` | 5007 | |
| `REGISTRY_URL` | http://registry:5001 | |
| `INTERVAL_S` | 10 | loop period |
| `WORKER_CAPACITY` / `TARGET_UTILIZATION` | 12 / 0.8 | policy (runtime-editable) |
| `MIN_WORKERS` / `MAX_WORKERS` | 2 / 6 | policy |
| `COOLDOWN_S` / `DRAIN_TIMEOUT_S` | 60 / 120 | policy |
| `MODE` | auto | initial mode |
| `EXTRACTOR_IMAGE` | vp/frame-extractor:dev | |
| `DOCKER_NETWORK` | vp-net | **must be the real compose network name** (compose prefixes it unless `name:` is set) |
| `TEMPLATE_CONTAINER` | frame-extractor-1 | container name, or compose service name (looked up by label) |
| `FILE_SD_PATH` | /sd/extractors.json | Prometheus file_sd |
| `EXTRACTOR_METRICS_PORT` | 9101 | port in file_sd targets |
| `DOCKER_GID` | auto from socket | GID of the socket for the non-root user |
| `LOG_LEVEL` | INFO | |

Policy is in memory; a restart returns to the env defaults.

## Why root at start
A non-root user cannot be pre-baked into the socket's group (GID differs per host). `entrypoint.sh` starts as root only
to create a group with `DOCKER_GID` (or the socket's detected GID) and `setpriv`-drops to uid 10001 with that
group. If the GID is 0 (Docker Desktop sockets may be root-group) it stays root and logs a warning.

## Compose wiring (for Infra)
```yaml
  worker-controller:
    build: { context: ../services/worker-controller }
    image: vp/worker-controller:dev
    environment:
      PORT: "5007"
      REGISTRY_URL: http://registry:5001
      DOCKER_NETWORK: <actual network name of vp, e.g. vp-net via `name: vp-net`>
      TEMPLATE_CONTAINER: frame-extractor-1      # compose service name
      FILE_SD_PATH: /sd/extractors.json
      # DOCKER_GID: "999"                        # optional, auto-detected
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - fx-sd:/sd
    depends_on: { registry: { condition: service_healthy } }
    healthcheck:
      test: ["CMD", "python", "-c", "import urllib.request as u; u.urlopen('http://127.0.0.1:5007/health', timeout=3)"]
    restart: unless-stopped
    mem_limit: 128m
    networks: [vp]
  # prometheus: add volume  fx-sd:/etc/prometheus/sd:ro  and in prometheus.yml:
  #   - job_name: extractors-auto
  #     file_sd_configs: [ { files: ["/etc/prometheus/sd/extractors.json"], refresh_interval: 15s } ]
# top-level volumes: fx-sd: {}
```
Gateway: `/api/autoscaler/**` -> `http://worker-controller:5007`.

## Tests
```
python -m venv .venv && .venv/Scripts/pip install -r requirements-dev.txt
.venv/Scripts/python -m pytest      # fake Docker runtime + fake registry, no Docker needed
```
