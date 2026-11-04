# planner (port 8010)

Turns a natural-language query into a validated pipeline plan (contract 7.5), stores it in Postgres,
publishes it to compacted Kafka topic `pipeline.plans` (key = plan_id, tombstone on delete) and sets
`plan_id` on the cameras through the registry API.

## Environment

| Var | Default | Meaning |
|---|---|---|
| `DATABASE_URL` | `postgresql://vp:vp@postgres:5432/planner` | asyncpg DSN (table `plans`, migrated at startup) |
| `KAFKA_BOOTSTRAP` | `redpanda:9092` | Kafka bootstrap |
| `REGISTRY_URL` | `http://registry:5001` | `GET/PUT /api/cameras/{id}` |
| `OLLAMA_URL` | `http://host.docker.internal:11434` | Ollama base URL |
| `OLLAMA_MODEL` | `qwen2.5:3b` | model |
| `CONTRACTS_DIR` | `<app>/contracts` (`/app/contracts` in image) | folder with `tools.json`, `plan.schema.json` |
| `LLM_TIMEOUT_S` | `60` | per LLM call |
| `LLM_MAX_RETRIES` | `2` | retries with validator errors fed back |
| `REGISTRY_TIMEOUT_S` | `5` | |
| `PORT` | `8010` | HTTP port (`python -m app`) |
| `LOG_LEVEL` | `INFO` | JSON logs to stdout |

`/health` returns 503 until Postgres and Kafka are connected (retries for ~3 min in background).

## Contracts

`contracts/` is a **copy** (Docker build context is this folder). Source of truth is the manager's
`vision-pipeline/contracts/`. Refresh with `./sync_contracts.ps1` before `docker build`. Never edit the copies.

## Behaviour notes

* Draft: not stored, not published. Activation (`POST /api/plans`) forces `status=active`; re-posting the same
  `plan_id` with changed name/query/camera_ids/steps bumps `version` (server-side: previous+1, client value ignored);
  an identical re-post keeps the version and just republishes. Invalid plans -> 422 `{valid:false, errors}`.
* Cameras unknown to the registry (or registry errors) do not fail activation: reported in the
  `X-Planner-Warnings` response header (JSON list) and logged. Cameras dropped from a plan, or all cameras on
  delete, get `plan_id` cleared only if it still equals this plan.
* Validator rules (labels of `vp_planner_validation_errors_total{rule}`): `schema`, `step_ids`, `failed_ref`,
  `coco_class`, `plate_gating`, `face_gating`, `duplicate_tool`, `queue_chain`, `condition_source`, `params`.

## Dev

```
python -m venv .venv && .venv\Scripts\pip install -r requirements-dev.txt
.venv\Scripts\python -m pytest -q
.venv\Scripts\python scripts\eval_queries.py          # needs live Ollama
.venv\Scripts\python -m app
```
