#!/usr/bin/env bash
# End-to-end check of the running stack (run from Git Bash on the host). Read-only.
# Usage: infra/scripts/e2e-check.sh
set -u
"$(dirname "$0")/check-line-endings.sh" || exit 1
G=${GATEWAY:-http://localhost:8000}
C="curl -s -m 15"
tmp=$(mktemp -d)

echo "== services not healthy:"
(cd "$(dirname "$0")/.." && docker compose ps -a --format '{{.Service}} {{.Status}}' | grep -vE 'healthy|Exited \(0\)' || echo "   none")

echo "== cameras with an active plan: $($C $G/api/cameras | python -c "import json,sys; c=json.load(sys.stdin); print(sum(1 for x in c if x.get('plan_id')), '/', len(c))")"
echo "== health summary: $($C $G/api/health/summary)"

snap() { for p in 9101 9102; do $C localhost:$p/metrics | grep -E '^vp_extractor_frames_total'; done | sed -E 's/.*camera_id="([^"]+)".* ([0-9.e+]+)$/\1 \2/' | sort; }
a=$(snap); t0=$(date +%s)
$C "$G/api/recognitions?limit=200" > "$tmp/rec.json"
$C "$G/api/detections/stats?minutes=10" > "$tmp/stats.json"
python - "$tmp" <<'EOF'
import json, sys, collections
d = sys.argv[1]
r = json.load(open(f"{d}/rec.json")); items = r if isinstance(r, list) else r.get("items", r.get("recognitions", []))
print("== recognitions stored (last 200):", len(items))
print("   by type/value:", collections.Counter((i["type"], i["value"]) for i in items).most_common(6))
if items: print("   newest:", {k: items[0].get(k) for k in ("type", "value", "confidence", "camera_id", "snapshot_url")})
with_snap = [i for i in items if i.get("snapshot_url")]
print(f"   with snapshot: {len(with_snap)}/{len(items)}")
open(f"{d}/snap_url", "w").write(with_snap[0]["snapshot_url"] if with_snap else "")
s = json.load(open(f"{d}/stats.json")); c = collections.Counter()
for x in s: c[x["class"]] += x["count"]
print("== detections last 10 min by class:", dict(c.most_common(6)))
EOF
u=$(cat "$tmp/snap_url")
[ -n "$u" ] && echo "== snapshot fetch: $($C -o "$tmp/snap.jpg" -w 'HTTP %{http_code}, %{size_download} bytes, %{content_type}' "$G$u")"

dt=$(( $(date +%s) - t0 )); [ $dt -lt 10 ] && { b0=$(date +%s); while [ $(( $(date +%s) - b0 )) -lt $((10 - dt)) ]; do :; done; }
b=$(snap); dt=$(( $(date +%s) - t0 ))
echo "== ingest over ${dt}s:"; join <(echo "$a") <(echo "$b") | awk -v t=$dt '{f=($3-$2)/t; s+=f; if(f<2.4) low=low" "$1"="sprintf("%.1f",f)} END {printf "   %.1f frames/s total; cameras < 2.4 fps:%s\n", s, (low==""?" none":low)}'
rm -rf "$tmp"
