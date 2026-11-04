#!/bin/bash
# Creates all topics from CONTRACTS section 5. Idempotent (existing topics are left as they are).
# RPK_BROKERS is provided by compose.
set -u
H=3600000; H6=21600000; D7=604800000   # retention.ms: 1h, 6h, 7d
export RPK_ADMIN_HOSTS="${RPK_ADMIN_HOSTS:-redpanda:9644}"   # cluster config goes through the admin API

rpk cluster config set auto_create_topics_enabled false || exit 1
# Consumer lag: Redpanda 24.2 has no consumer-group lag metric; see Redpanda Console (8080) — BACKLOG #13.

mk() { # name partitions policy [retention_ms]
  if [ "$3" = compact ]; then
    rpk topic create "$1" -p "$2" -c cleanup.policy=compact
  else
    rpk topic create "$1" -p "$2" -c cleanup.policy=delete -c retention.ms="$4"
  fi
}

mk camera.config    12 compact
mk frames.raw       48 delete 300000     # v1.7: JPEG bytes travel in Kafka, 5 min
mk detections       24 delete $H6
mk recognitions     12 delete $D7
mk camera.status    12 delete $H
mk camera.health    12 compact
mk pipeline.plans    6 compact
mk pipeline.metrics  6 delete $H
mk jobs.heavy       12 delete $H6
mk camera.demand    12 compact            # v1.8: viewers per camera (realtime-gateway -> registry, executor)

# frames.raw (contract 9.7): applied with alter-config so re-runs converge on an existing topic.
rpk topic alter-config frames.raw --set retention.ms=300000 --set segment.ms=60000 --set segment.bytes=67108864 --set max.message.bytes=2097152
# jobs.heavy carries the JPEG too (executor: value = JPEG, header `job`)
rpk topic alter-config jobs.heavy --set max.message.bytes=2097152

# Verify (rpk may exit 0 or 1 for already-existing topics, so check the end state).
missing=0
for t in camera.config frames.raw detections recognitions camera.status camera.health pipeline.plans pipeline.metrics jobs.heavy camera.demand; do
  rpk topic describe "$t" >/dev/null 2>&1 || { echo "MISSING topic $t"; missing=1; }
done
[ $missing -eq 0 ] && { echo "all topics present"; rpk topic list; }
exit $missing
