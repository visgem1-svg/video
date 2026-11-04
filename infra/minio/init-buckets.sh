#!/bin/sh
# Buckets from CONTRACTS section 4. Idempotent.
set -e
until mc alias set local "http://${MINIO_ENDPOINT}" "${MINIO_ACCESS_KEY}" "${MINIO_SECRET_KEY}" >/dev/null 2>&1; do
  echo "waiting for minio"; sleep 1
done
mc mb --ignore-existing local/frames
mc mb --ignore-existing local/snapshots
# frames: 1-day lifecycle (replace any previous rule so re-runs do not stack rules)
mc ilm rule rm --all --force local/frames >/dev/null 2>&1 || true
mc ilm rule add --expire-days 1 local/frames
mc ilm rule ls local/frames
echo "buckets ready"
