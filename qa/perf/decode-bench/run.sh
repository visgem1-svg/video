#!/bin/bash
# Usage: run.sh [WORKDIR]  (default $TEMP/decode-bench). Needs docker + image vp/frame-extractor:dev already built.
set -e
export MSYS_NO_PATHCONV=1
HERE=$(cd "$(dirname "$0")" && pwd); IMG=vp/frame-extractor:dev
W=${1:-${TEMP:-/tmp}/decode-bench}; mkdir -p "$W"
WW=$(cygpath -m "$W" 2>/dev/null || echo "$W"); HH=$(cygpath -m "$HERE" 2>/dev/null || echo "$HERE")
ASSETS=$(cygpath -m "$HERE/../../../infra/sim-cameras/assets" 2>/dev/null || echo "$HERE/../../../infra/sim-cameras/assets")
echo "== generating clips (not measured)"
docker run --rm --cpus=2 --entrypoint sh -v "$ASSETS:/img:ro" -v "$HH:/b:ro" -v "$WW:/w" $IMG /b/gen-clips.sh /img/zeedijk.jpg /w/clips 62
echo "== environment"
docker run --rm --entrypoint sh $IMG -c 'ffmpeg -version | head -1; grep -m1 "model name" /proc/cpuinfo' | tee "$W/env.txt"
echo "== benchmark (--cpus=1)"
docker run --rm --cpus=1 --entrypoint python3 -v "$HH:/b:ro" -v "$WW:/w" $IMG /b/bench.py /w/clips /w/results.json 3 | tee "$W/bench.log"
cp "$W/results.json" "$HERE/results.json"
