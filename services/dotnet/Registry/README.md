# Registry (port 5001, DB `registry`)

Camera CRUD (contract 7.2), publication to compacted `camera.config` (tombstone on delete, full republish at startup),
worker heartbeat + assignment/rebalancing, `GET /api/workers`.

## Environment variables
| Var | Default | Meaning |
|---|---|---|
| `KAFKA_BOOTSTRAP` | `redpanda:9092` | Kafka |
| `POSTGRES_CONNECTION` | `Host=postgres;Port=5432;Database=registry;Username=vp;Password=vp` | Npgsql connection string |
| `ASPNETCORE_URLS` | `http://+:5001` | listen address |
| `LEASE_S` | `15` | worker lease seconds |
| `SEED_CAMERAS` | `0` | if cameras table is empty at startup create `cam-001..N` (N=20 in compose) |
| `SEED_RTSP_BASE` | `rtsp://mediamtx:8554` | seed `source_url` prefix |
| `TOPIC_CAMERA_CONFIG` | `camera.config` | topic name |
| `MEDIAMTX_API_URL` | `http://mediamtx:9997` | MediaMTX API for path provisioning (contract 9.3); reconcile at startup and every 30 s |

Docker also sets `DOTNET_gcServer=0`, `DOTNET_GCHeapHardLimit=0x6000000`.
Endpoints: `/api/cameras[/{id}]`, `/api/workers`, `/api/workers/{id}/heartbeat`, `/health` (503 until seed+republish done), `/metrics`.

## Assignment design
* Pure function `Assigner.Rebalance` (sticky -> keep valid assignments; then place unassigned enabled cameras on the
  least-loaded live worker with free capacity, tie-break by worker id; dead workers = no heartbeat for `LEASE_S`;
  disabled/deleted cameras are released; lowered capacity sheds the excess).
* State lives in Postgres (`workers`, `assignments`; PK on `camera_id` => a camera can never have two owners).
  Every heartbeat / sweep / camera write runs in one transaction under `pg_advisory_xact_lock`, so heartbeats are strictly serialised
  - correct with several registry replicas, no in-memory single-writer needed. Cost: one small transaction per heartbeat
  (workers heartbeat every few seconds, so it is negligible even at 400 cameras).
* A 5 s sweeper expires dead workers even when no heartbeat arrives.
* Camera writes: DB commit then Kafka publish, ordered per replica by an in-process gate; startup republish heals any gap.
* Newly joined workers do not steal cameras from healthy workers (stickiness); they receive new/orphaned cameras only.

Migrations: `Migrations/*.sql` embedded, applied at startup under an advisory lock. Image: `Dockerfile` (context `services/dotnet`).
Tests: `dotnet test Registry.Tests`.

## Worker operations (contract 9.5)
`POST /api/workers/{id}/drain|undrain`, `PUT /api/workers/{id}/capacity {capacity|null}`, `POST /api/cameras/{id}/pin {worker_id|null}`,
`POST /api/cameras/{id}/restart` (bumps `generation`, carried in heartbeat `assigned[]`). `GET /api/workers` adds `capacity_override`,
`draining`, `state` (live|draining|dead), `managed_by` (`controller` for `fx-auto-*`); `capacity` there is the effective one.
Cameras show computed `assigned_worker` / `pin_fallback`. Dead workers stay listed for 10 min. Gauges `vp_registry_worker_capacity{worker_id}`,
`vp_registry_worker_assigned{worker_id}`. A draining worker with no other room keeps its cameras until capacity appears.

## Analysis substream + motion (contract 9.9)
Camera fields `analysis_stream_url` (rtsp/rtsps; `""` clears; omitted on PUT = keep), `motion {enabled, sensitivity low|medium|high, heartbeat_s 1..60}`
(omitted members keep the old value on PUT / defaults on POST; 400 names the field) and read-only `analysis_source_url`
(`rtsp://mediamtx:8554/{id}-analysis`, derived). Migration `004` adds nullable/defaulted columns (idempotent). `camera_id` may not end in `-analysis`.
Pulled paths (`{id}`, `{id}-analysis`) are written with `sourceOnDemand=true`, start timeout 10s, close-after 30s. Reconcile upgrades a pre-9.9
always-on path in place only while it has 0 readers (a PATCH restarts the path); busy ones upgrade on their next change or MediaMTX restart.
Duplicates: main is compared with main, analysis with analysis only. Provisioning errors have `user:pass@` redacted.
