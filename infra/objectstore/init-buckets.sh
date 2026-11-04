#!/bin/sh
# Buckets from CONTRACTS section 4 on SeaweedFS (S3-compatible). Idempotent.
set -e
until echo "s3.bucket.list" | weed shell -master=minio:9333 -filer=minio:8888 >/dev/null 2>&1; do
  echo "waiting for objectstore"; sleep 1
done
existing=$(echo "s3.bucket.list" | weed shell -master=minio:9333 -filer=minio:8888 2>/dev/null)
for b in frames snapshots; do
  echo "$existing" | grep -qw "$b" || echo "s3.bucket.create -name $b" | weed shell -master=minio:9333 -filer=minio:8888
done
# frames: TTL ${FRAMES_TTL:-10m} (contract v1.3). Frames are read within ~1.5 s (executor) / ~1 s (snapshot copy);
# a 1-day TTL filled the laptop disk at ~12 GB/h (65 frames/s x ~50 KB) and would be ~5 TB/day at 400 cameras.
echo "fs.configure -locationPrefix=/buckets/frames/ -ttl=${FRAMES_TTL:-10m} -apply" | weed shell -master=minio:9333 -filer=minio:8888
# snapshots: retention per contract v1.6 (local test 1d; production 7d) — applies to newly written files
echo "fs.configure -locationPrefix=/buckets/snapshots/ -ttl=${SNAPSHOT_TTL:-1d} -apply" | weed shell -master=minio:9333 -filer=minio:8888
echo "s3.bucket.list" | weed shell -master=minio:9333 -filer=minio:8888
echo "buckets ready"
