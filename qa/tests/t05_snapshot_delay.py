"""Bug 5: how long after a live recognition until its snapshot returns 200 (via API, polling)."""
import time, statistics, json, urllib.request, urllib.error
class requests:
    @staticmethod
    def get(u):
        class R: pass
        r=R()
        try:
            with urllib.request.urlopen(u) as x: b=x.read(); r.status_code=x.status
        except urllib.error.HTTPError as e: b=b''; r.status_code=e.code
        r.json=lambda: json.loads(b); return r
seen = set(r["recognition_id"] for r in requests.get("http://localhost:8000/api/recognitions?limit=200").json())
pending = {}; delays = []; missing = 0
end = time.time() + 40
while time.time() < end:
    for r in requests.get("http://localhost:8000/api/recognitions?limit=30").json():
        if r["recognition_id"] not in seen:
            seen.add(r["recognition_id"]); pending[r["recognition_id"]] = (time.time(), r["snapshot_url"])
    for k, (t0, u) in list(pending.items()):
        c = requests.get("http://localhost:8000" + u).status_code
        if c == 200: delays.append(time.time() - t0); del pending[k]
        elif time.time() - t0 > 30: missing += 1; del pending[k]
    time.sleep(0.5)
print("n", len(delays), "median %.1fs max %.1fs" % (statistics.median(delays), max(delays)), "still-missing(>30s)", missing, "pending", len(pending))
