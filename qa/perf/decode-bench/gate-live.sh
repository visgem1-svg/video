#!/bin/bash
# Part D: emitted frames/s of the given cameras (Prometheus vp_extractor_frames_total) over a window; run once with motion disabled and once enabled.
# usage: gate-live.sh "cam-031|cam-032|cam-033|cam-034" SECONDS
CAMS=$1; WIN=${2:-180}
q() { curl -s -G localhost:9090/api/v1/query --data-urlencode "query=sum(vp_extractor_frames_total{camera_id=~\"$CAMS\"})" | python -I -c "import json,sys; r=json.load(sys.stdin)['data']['result']; print(r[0]['value'][1] if r else 0)"; }
a=$(q); t0=$(date +%s); sleep "$WIN"; b=$(q); t1=$(date +%s)
python -I -c "a,b,d=$a,$b,$t1-$t0; print(f'frames={b-a:.0f} over {d}s = {(b-a)/d:.3f} fps total, {(b-a)/d/4:.3f} fps/camera (4 cameras)')"
