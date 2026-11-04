"""Generates grafana/dashboards/frame-extraction.json and appends 4 panels to vision-pipeline.json.
Run from infra/:  python grafana/gen_dashboards.py"""
import json

ds = {"type": "prometheus", "uid": "prometheus"}


def tg(exprs, instant=False):
    out = []
    for k, e in enumerate(exprs):
        e, l = e if isinstance(e, tuple) else (e, "")
        t = {"refId": chr(65 + k), "datasource": ds, "expr": e, "legendFormat": l}
        if instant:
            t.update(instant=True, range=False, format="table")
        out.append(t)
    return out


def ts(i, title, exprs, x, y, unit="short", w=12, h=8, desc="", stack=False, ymax=None, thresh=None):
    d = {"unit": unit, "custom": {"lineWidth": 1, "fillOpacity": 20 if stack else 8, "showPoints": "never"}}
    if stack:
        d["custom"]["stacking"] = {"mode": "normal"}
    if ymax is not None:
        d["max"] = ymax
    if thresh:
        d["custom"]["thresholdsStyle"] = {"mode": "line"}
        d["thresholds"] = {"mode": "absolute", "steps": [{"color": "green", "value": None}, {"color": "red", "value": thresh}]}
    return {"id": i, "type": "timeseries", "title": title, "description": desc, "datasource": ds,
            "gridPos": {"x": x, "y": y, "w": w, "h": h},
            "fieldConfig": {"defaults": d, "overrides": []},
            "options": {"legend": {"displayMode": "table", "placement": "bottom", "calcs": ["lastNotNull", "max"]},
                        "tooltip": {"mode": "multi"}},
            "targets": tg(exprs)}


def table(i, title, expr, x, y, w=12, h=8, desc="", unit="short", sort_asc=True, name="Value"):
    return {"id": i, "type": "table", "title": title, "description": desc, "datasource": ds,
            "gridPos": {"x": x, "y": y, "w": w, "h": h},
            "fieldConfig": {"defaults": {"unit": unit}, "overrides": []},
            "options": {"sortBy": [{"displayName": name, "desc": not sort_asc}]},
            "transformations": [{"id": "organize", "options": {
                "excludeByName": {"Time": True, "job": True, "instance": True, "__name__": True},
                "renameByName": {"Value": name}}}],
            "targets": tg([expr], instant=True)}


def text(i, title, md, x, y, w=12, h=4):
    return {"id": i, "type": "text", "title": title, "gridPos": {"x": x, "y": y, "w": w, "h": h},
            "options": {"mode": "markdown", "content": md}}


C = 'camera_id=~"$camera", worker=~"$worker"'
W = 'worker=~"$worker"'
P = [
    ts(1, "Total ingest frames/s (all workers)", [("sum(rate(vp_extractor_frames_total{%s}[1m]))" % W, "total")],
       0, 0, "ops", desc="Expected: cameras x 3 fps."),
    ts(2, "Ingest frames/s per worker", [("sum by (worker) (rate(vp_extractor_frames_total{%s}[1m]))" % W, "{{worker}}")],
       12, 0, "ops"),
    table(3, "Per-camera actual fps (worst first; target 3)",
          "sum by (camera_id, worker) (rate(vp_extractor_frames_total{%s}[2m]))" % C, 0, 8,
          desc="No fps_actual gauge in Prometheus; derived from the frames counter. Target = analysis_fps (3).",
          unit="ops", name="fps"),
    {"id": 4, "type": "heatmap", "title": "Per-camera fps heatmap (rows = cameras)", "datasource": ds,
     "gridPos": {"x": 12, "y": 8, "w": 12, "h": 8},
     "options": {"calculate": False, "yAxis": {"axisPlacement": "left"},
                 "color": {"mode": "scheme", "scheme": "RdYlGn", "scale": "linear", "min": 0, "max": 3.5, "fill": "dark-orange", "reverse": False},
                 "cellGap": 1, "tooltip": {"show": True}},
     "fieldConfig": {"defaults": {"unit": "ops", "custom": {"scaleDistribution": {"type": "linear"}}}, "overrides": []},
     "targets": tg([("sum by (camera_id) (rate(vp_extractor_frames_total{%s}[1m]))" % C, "{{camera_id}}")])},
    ts(5, "Cameras by state over time (extractor view)",
       [("sum by (state) (vp_extractor_camera_state{%s})" % C, "{{state}}")], 0, 16, stack=True,
       desc="States as exposed: starting, online, stalled, reconnecting, error, offline."),
    ts(6, "Cameras by health state (stream-health)", [("sum by (state) (vp_streamhealth_cameras)", "{{state}}")],
       12, 16, stack=True),
    ts(7, "ffmpeg restarts per camera (per 5 min)",
       [("sum by (camera_id) (increase(vp_extractor_ffmpeg_restarts_total{%s}[5m]))" % C, "{{camera_id}}")], 0, 24),
    table(8, "Top 10 cameras by ffmpeg restarts (1 h)",
          "topk(10, sum by (camera_id, worker) (increase(vp_extractor_ffmpeg_restarts_total{%s}[1h])))" % C,
          12, 24, sort_asc=False, name="restarts"),
    ts(9, "Dropped frames/s per camera",
       [("sum by (camera_id) (rate(vp_extractor_frames_dropped_total{%s}[1m]))" % C, "{{camera_id}}"),
        ("sum(rate(vp_extractor_publish_errors_total{%s}[1m]))" % W, "publish errors (all)")], 0, 32, "ops"),
    ts(10, "Object-store upload latency p50/p95/p99",
       [("histogram_quantile(%s, sum by (le) (rate(vp_extractor_upload_seconds_bucket{%s}[2m])))" % (q, W), "p%d" % round(q * 100))
        for q in (0.5, 0.95, 0.99)], 12, 32, "s"),
    ts(11, "Cameras per worker vs capacity (10)",
       [("sum by (worker) (vp_extractor_cameras_running{%s})" % W, "{{worker}}")], 0, 40, thresh=10, ymax=12,
       desc="Capacity = WORKER_CAPACITY=10 (no capacity metric exists; red line = 10)."),
    ts(12, "Executor frames: processed vs superseded vs stale (/s)",
       [("sum(rate(vp_executor_frames_total[1m]))", "processed"),
        ("sum by (reason) (rate(vp_executor_frames_skipped_total[1m]))", "skipped {{reason}}")],
       12, 40, "ops", stack=True, desc="Executor metrics carry no camera label."),
    text(13, "Detection age",
         "The executor exposes no detection-age metric. Proxy: share of frames skipped as `stale` in the panel above. "
         "Proposal: histogram `vp_executor_detection_age_seconds` (now - frame ts when the detection is published).", 0, 48),
    ts(14, "Duplicate cameras (vp_registry_duplicate_cameras)", [("vp_registry_duplicate_cameras", "duplicates")],
       12, 48, h=4, desc="No data until the registry exposes this metric."),
    table(15, "Cameras with duplicate_of",
          "max by (camera_id, duplicate_of) (vp_registry_camera_duplicate_info)", 0, 52, w=24, h=6,
          desc="Needs a registry gauge vp_registry_camera_duplicate_info{camera_id,duplicate_of}=1 (not exposed yet); "
               "otherwise read GET /api/cameras (field duplicate_of).", name="dup"),
]


def var(n, q, lab):
    return {"name": n, "label": lab, "type": "query", "datasource": ds, "query": {"query": q, "refId": "v"},
            "definition": q, "refresh": 2, "includeAll": True, "multi": True, "allValue": ".*",
            "current": {"text": "All", "value": "$__all"}, "sort": 1}


d = {"uid": "frame-extraction", "title": "Frame extraction", "tags": ["vision-pipeline"], "timezone": "browser",
     "schemaVersion": 39, "version": 1, "refresh": "10s", "time": {"from": "now-30m", "to": "now"},
     "editable": False, "annotations": {"list": []},
     "templating": {"list": [var("worker", "label_values(vp_extractor_frames_total, worker)", "Worker"),
                             var("camera", 'label_values(vp_extractor_frames_total{worker=~"$worker"}, camera_id)', "Camera")]},
     "panels": P}
json.dump(d, open("grafana/dashboards/frame-extraction.json", "w"), indent=1)

f = "grafana/dashboards/vision-pipeline.json"
m = json.load(open(f))
m["panels"] = [p for p in m["panels"] if p["id"] < 100]
m["panels"] += [
    ts(101, "Total ingest frames/s", [("sum(rate(vp_extractor_frames_total[1m]))", "ingest")], 0, 24, "ops", w=6),
    ts(102, "Cameras by health state", [("sum by (state) (vp_streamhealth_cameras)", "{{state}}")], 6, 24, w=6, stack=True),
    ts(103, "Dropped frames/s", [("sum(rate(vp_extractor_frames_dropped_total[1m]))", "dropped")], 12, 24, "ops", w=6),
    ts(104, "Executor processed vs superseded (/s)",
       [("sum(rate(vp_executor_frames_total[1m]))", "processed"),
        ("sum by (reason) (rate(vp_executor_frames_skipped_total[1m]))", "skipped {{reason}}")], 18, 24, "ops", w=6, stack=True),
]
json.dump(m, open(f, "w"), indent=1)
