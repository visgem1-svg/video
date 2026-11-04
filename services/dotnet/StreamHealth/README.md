# StreamHealth (port 5002)

Consumes `camera.status` (group `stream-health`, latest) and `camera.config` (all partitions from the beginning on every start,
no committed offsets), computes `camera.health` (contract 5.6) in memory, publishes to compacted `camera.health` on change or every 10 s
(tombstone when a camera is deleted), serves `GET /api/health/cameras`, `/api/health/cameras/{id}`, `/api/health/summary`.

## Environment variables
| Var | Default | Meaning |
|---|---|---|
| `KAFKA_BOOTSTRAP` | `redpanda:9092` | Kafka |
| `REGISTRY_URL` | (unset) | polled every 5 s at `/api/workers` to know which cameras have an owner |
| `ASPNETCORE_URLS` | `http://+:5002` | listen address |

`/health` is 503 until camera.config has been read up to its end offsets. `/metrics` Prometheus.

## State rules
* no config / disabled / no status => `unassigned`; status silent > 15 s => `offline` (or `unassigned` when the registry lists no owner);
  `offline`/`error` => `offline`; `starting|stalled|reconnecting` => `degraded`; `online` => `degraded` if fps < 70 % of target or >= 3 reconnects in 10 min.
* `offline` from an extractor is a final release: the previous state is held for 15 s (handover window); a late release from a former owner is ignored
  when a new owner already reports. No downtime, no reconnect counted.
* `reconnects_1h`: increments of the cumulative `reconnects` counter (same worker only; counter reset / worker change re-baselines), event queue pruned at 1 h.
  Counting starts from the first status seen after start.
* `uptime_pct_1h`: 360 x 10 s ring buckets with running sums (O(1)); sampled once a second for enabled cameras; up = status `online` and fresh.
  Starts at the time the service starts (no history is reloaded).
Single replica assumed (state is in memory; a second replica would publish identical values).
Tests: `dotnet test StreamHealth.Tests`.

## Error classes (contract 9.9)
`camera.status` `error_class` / `motion_skipped_pct` (missing = null) are exposed on health items together with `error_hint`
(human text per class). Terminal classes `auth_failed`, `not_found`, `codec_unsupported` force state `offline` (never `degraded`).
Both fields are dropped when the status is stale/released. `/api/health/summary` adds `by_error_class` (non-zero classes only).
