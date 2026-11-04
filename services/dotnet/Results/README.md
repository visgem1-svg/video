# Results (port 5004, DB `results`)

Consumes `detections` + `recognitions` (group `results`), stores recognitions (one row each), aggregated detection counts
(`detection_counts`: camera/class/minute, **no per-frame rows**), stores the `snapshot_key` the executor sends (it uploads the frame to `snapshots` itself, contract 9.7).
API: `GET /api/recognitions?camera_id=&type=&since=&limit=50`, `GET /api/detections/stats?camera_id=&minutes=60`
(rows `{camera_id, class, minute_ts, count}`), `GET /api/snapshots/{type}/{yyyyMMdd}/{id}.jpg`, `/health`, `/metrics`.

## Environment variables
| Var | Default | Meaning |
|---|---|---|
| `KAFKA_BOOTSTRAP` | `redpanda:9092` | Kafka |
| `POSTGRES_CONNECTION` | `Host=postgres;Port=5432;Database=results;Username=vp;Password=vp;Maximum Pool Size=10` | Npgsql |
| `MINIO_ENDPOINT` | `minio:9000` | |
| `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY` | `minio` / `minio12345` | |
| `MINIO_USE_SSL` | `false` | |
| `MINIO_SNAPSHOTS_BUCKET` | `snapshots` | bucket served at `/api/snapshots/...` |
| `ASPNETCORE_URLS` | `http://+:5004` | |
| `BATCH_MAX` / `BATCH_FLUSH_MS` | `500` / `500` | flush on whichever comes first |
| `RECOGNITION_RETENTION_DAYS` | `7` | hourly delete of older recognition rows, 1000 per batch (<=0 disables); metric `vp_results_recognitions_deleted_total` |

## Batching and idempotency
Consumer thread -> bounded channel (4 batches, back-pressure) -> writer: copy snapshots (idempotent, 8 parallel, 3 retries, missing frame => row without
snapshot), then **one Postgres transaction** (recognitions `ON CONFLICT DO NOTHING`, additive aggregate upsert, offsets), then Kafka commit.
* Recognitions: `recognition_id` PK.
* Aggregates are additive so they would double count on redelivery. The Kafka offsets of `detections`/`recognitions` are stored in `consumer_offsets`
  **in the same transaction** as the deltas. On partition assignment the consumer seeks to the DB offsets; each batch is also filtered against them
  (offset < stored => skipped). The tx locks the offset rows and aborts if another writer already advanced past the batch start (zombie fence; batch is
  re-filtered and retried). Kafka offset commits are only for lag monitoring. A partial (`partial:true`, `step_id`) detection is a separate Kafka message and is
  counted once like any other message.
* Poison messages are counted (`vp_results_invalid_messages_total`) and skipped.
Retention of `detection_counts`/recognitions is not implemented (add a purge job if needed).
Tests: `dotnet test Results.Tests`.
