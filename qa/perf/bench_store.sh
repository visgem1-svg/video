#!/bin/bash
# usage: bench_store.sh <variant> ; variants: seaweed-base seaweed-b1 seaweed-b2 garage
# Runs on isolated network vpbench with temp containers vpbench-*; cleans up after itself.
export MSYS_NO_PATHCONV=1
V=$1; SECS=${SECS:-120}; PUT=${PUT:-300}; GET=${GET:-30}; SIZE=${SIZE:-30000}
HERE="$(cd "$(dirname "$0")" && pwd -W 2>/dev/null || pwd)"
OUT="$HERE/results/store-$V"; mkdir -p "$HERE/results"
docker network create vpbench >/dev/null 2>&1
docker rm -f vpbench-store >/dev/null 2>&1; docker volume rm -f vpbench-data >/dev/null 2>&1
SW=chrislusf/seaweedfs:3.80
AK=minio; SK=minio12345; REGION=us-east-1
S3J="$HERE/s3.json"
cat > "$HERE/results/s3.json" <<J
{"identities":[{"name":"vp","credentials":[{"accessKey":"minio","secretKey":"minio12345"}],"actions":["Admin","Read","Write","List","Tagging"]}]}
J
S3J="$HERE/results/s3.json"
sw() { docker run -d --name vpbench-store --network vpbench --network-alias store --memory 400m -e GOMEMLIMIT=${GML:-200MiB} \
  -v vpbench-data:/data -v "$S3J:/etc/s3.json:ro" $SW server -dir=/data -ip=store -ip.bind=0.0.0.0 -master.volumeSizeLimitMB=128 -volume.max=0 \
  -filer -s3 -s3.port=9000 -s3.config=/etc/s3.json "$@" >/dev/null; }
case $V in
 seaweed-base) sw -metricsPort=9324;;
 seaweed-b1) sw -master.volumePreallocate=false -volume.index=memory;;
 seaweed-b2) sw -master.volumePreallocate=false -volume.index=memory -filer.saveToFilerLimit=65536;;
 seaweed-b3) sw -master.volumePreallocate=false -volume.index=memory -filer.saveToFilerLimit=65536 -volume.concurrentUploadLimitMB=0 -filer.concurrentUploadLimitMB=0 -s3.allowEmptyFolder=false;;
 garage)
  GI=dxflrs/garage:v2.4.1; AK=GK0123456789abcdef01234567; SK=$(printf 'ab%.0s' {1..32}); REGION=garage
  cat > "$HERE/results/garage.toml" <<T
metadata_dir = "/meta"
data_dir = "/data"
db_engine = "lmdb"
replication_factor = 1
rpc_bind_addr = "0.0.0.0:3901"
rpc_public_addr = "127.0.0.1:3901"
rpc_secret = "1799bccfd7411eddcf9ebd316bc1f5287ad12a68094e1c6ac6abde7e6feae1ec"
[s3_api]
s3_region = "garage"
api_bind_addr = "0.0.0.0:9000"
T
  docker run -d --name vpbench-store --network vpbench --network-alias store --memory 400m -v vpbench-data:/data -v "$HERE/results/garage.toml:/etc/garage.toml:ro" $GI >/dev/null
  sleep 4
  G="docker exec vpbench-store /garage"
  NID=$($G node id -q 2>/dev/null | cut -d@ -f1)
  $G layout assign -z dc1 -c 2G $NID; $G layout apply --version 1; sleep 2
  $G key import --yes -n bench $AK $SK
  $G bucket create frames; $G bucket allow --read --write --owner frames --key bench;;
esac
sleep 8
LG="docker run --rm --network vpbench --memory 200m -v $HERE:/w:ro -v $HERE/results:/w/results python:3.12-alpine python /w/s3load.py --endpoint store:9000 --ak $AK --sk $SK --region $REGION"
echo "warmup"; $LG --make-bucket --secs 15 --put-rate 100 --get-rate 10 --size $SIZE | tail -1
sleep 3
( while true; do docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}}' vpbench-store; done ) > "$OUT.stats" 2>/dev/null &
SP=$!
echo "run $V"; $LG --secs $SECS --put-rate $PUT --get-rate $GET --size $SIZE --out /w/results/store-$V.json
kill $SP 2>/dev/null; wait $SP 2>/dev/null
python - <<P
import re,json
c=[];m=[]
for l in open(r"$OUT.stats"):
    p=l.split()
    if len(p)<2: continue
    c.append(float(p[0].rstrip('%')))
    x=re.match(r'([\d.]+)(\w+)',p[1]); m.append(float(x[1])*{'MiB':1,'GiB':1024,'KiB':1/1024}[x[2]])
c=c[2:] or c
print("CPU avg %.0f%% max %.0f%% | RSS max %.0f MiB | samples %d"%(sum(c)/len(c),max(c),max(m),len(c)))
P
docker rm -f vpbench-store >/dev/null; docker volume rm -f vpbench-data >/dev/null
