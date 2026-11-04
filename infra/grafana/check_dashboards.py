"""Runs every dashboard target against live Prometheus (variables replaced by .*)."""
import json, urllib.request, urllib.parse

for f in ["frame-extraction", "vision-pipeline"]:
    d = json.load(open(f"grafana/dashboards/{f}.json"))
    for p in d["panels"]:
        for t in p.get("targets", []):
            e = t["expr"].replace("$worker", ".*").replace("$camera", ".*")
            r = json.load(urllib.request.urlopen("http://localhost:9090/api/v1/query?query=" + urllib.parse.quote(e)))
            print(f[:5], p["id"], p["title"][:45], "series:", len(r["data"]["result"]), r["status"])
