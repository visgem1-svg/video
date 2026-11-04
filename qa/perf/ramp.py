#!/usr/bin/env python3
"""Camera ramp harness (SCALE-TEST.md). Stdlib only.

  python ramp.py                       # steps 20,50,80,100: settle 120 s, measure 180 s, resumable
  python ramp.py --steps 20,50 --force # redo steps
  python ramp.py --dry --steps 21 --no-register --settle 5 --measure 30   # collector smoke test (no new cameras)
  python ramp.py --down-to 20          # disable scale cameras above cam-020 (a later ramp re-enables them)
  python ramp.py --report              # only regenerate SCALE-REPORT.md from results/step-*.json

Safe: registration is idempotent (existing cameras are skipped, only re-enabled if disabled); Ctrl-C stops at the
next checkpoint, saves results/<tag>step-N.partial.json and leaves cameras registered (use --down-to to shrink).
"""
import argparse, json, math, os, re, signal, statistics, subprocess, sys, time, urllib.request, urllib.parse, urllib.error

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, "results")
INFRA = os.path.abspath(os.path.join(HERE, "..", "..", "infra"))
API, PROM = "http://localhost:8000", "http://localhost:9090"
STOP = False


def on_sig(*_):
    global STOP
    STOP = True
    print("\n[ctrl-c] stopping at next checkpoint; cameras stay registered (use --down-to N to shrink)", flush=True)


signal.signal(signal.SIGINT, on_sig)


def sleep(s):
    end = time.time() + s
    while time.time() < end and not STOP:
        time.sleep(max(0, min(1, end - time.time())))


# ---------- http / shell helpers
def http(method, url, body=None, timeout=40):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            t = r.read()
            return r.status, (json.loads(t) if t else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")[:300]
    except Exception as e:
        return 0, str(e)


def sh(cmd, timeout=60, cwd=None):
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, cwd=cwd, encoding="utf-8", errors="replace")
        return r.stdout or ""
    except Exception:
        return ""


def prom(q):
    st, j = http("GET", PROM + "/api/v1/query?" + urllib.parse.urlencode({"query": q}))
    if st != 200 or not isinstance(j, dict) or j.get("status") != "success":
        return []
    return [(x["metric"], float(x["value"][1])) for x in j["data"]["result"] if x["value"][1] not in ("NaN", "+Inf", "-Inf")]


def pscalar(q, default=None):
    r = prom(q)
    return r[0][1] if r else default


def pct(a, p):
    if not a:
        return None
    a = sorted(a)
    k = (len(a) - 1) * p / 100
    lo, hi = math.floor(k), math.ceil(k)
    return a[lo] + (a[hi] - a[lo]) * (k - lo)


def r2(x, n=2):
    return None if x is None else round(x, n)


# ---------- cameras
def cam_id(n):
    return f"cam-{n:03d}"


def get_cameras():
    st, j = http("GET", API + "/api/cameras")
    return {c["camera_id"]: c for c in j} if st == 200 and isinstance(j, list) else {}


def ensure_cameras(n):
    """Idempotent: create missing cam-001..cam-n, re-enable disabled ones. Returns ids created/enabled."""
    have = get_cameras()
    touched = []
    for i in range(1, n + 1):
        if STOP:
            break
        cid = cam_id(i)
        c = have.get(cid)
        if c is None:
            st, j = http("POST", API + "/api/cameras", {"camera_id": cid, "name": f"Camera {i:03d}", "stream_url": "publisher",
                                                        "analysis_fps": 3, "enabled": True, "plan_id": None, "tags": ["scale"]})
            if st in (200, 201):
                touched.append(cid)
            else:
                print(f"  ! create {cid}: {st} {j}")
        elif not c.get("enabled", True):
            c = {k: v for k, v in c.items() if k != "updated_at"}
            c["enabled"] = True
            st, j = http("PUT", f"{API}/api/cameras/{cid}", c)
            if st in (200, 204):
                touched.append(cid)
            else:
                print(f"  ! enable {cid}: {st} {j}")
    return touched


def down_to(n):
    have = get_cameras()
    k = 0
    for cid, c in sorted(have.items()):
        m = re.fullmatch(r"cam-(\d{3})", cid)
        if m and int(m[1]) > n and "scale" in (c.get("tags") or []) and c.get("enabled", True):
            c = {a: b for a, b in c.items() if a != "updated_at"}
            c["enabled"] = False
            st, _ = http("PUT", f"{API}/api/cameras/{cid}", c)
            k += st in (200, 204)
    print(f"disabled {k} scale cameras above cam-{n:03d}")


def wait_assigned(ids, timeout=300):
    t0 = time.time()
    seen = {}
    ids = set(ids)
    while ids - set(seen) and time.time() - t0 < timeout and not STOP:
        st, ws = http("GET", API + "/api/workers")
        if st == 200:
            now = time.time() - t0
            for w in ws:
                for c in w.get("assigned") or []:
                    cid = c if isinstance(c, str) else c.get("camera_id")
                    if cid in ids and cid not in seen:
                        seen[cid] = now
        time.sleep(1)
    v = sorted(seen.values())
    return {"requested": len(ids), "assigned": len(seen), "p50_s": r2(pct(v, 50), 1), "max_s": r2(max(v) if v else None, 1),
            "unassigned_after_timeout": sorted(ids - set(seen))[:20]}


# ---------- collectors
def parse_mem(s):
    m = re.match(r"([\d.]+)\s*([KMG]i?B)", s.strip())
    if not m:
        return None
    return float(m[1]) * {"KB": 1 / 1024, "KiB": 1 / 1024, "MB": 1, "MiB": 1, "GB": 1024, "GiB": 1024}[m[2]]


def docker_stats():
    out = {}
    for l in sh(["docker", "stats", "--no-stream", "--format", "{{json .}}"], 90).splitlines():
        try:
            d = json.loads(l)
        except Exception:
            continue
        out[d["Name"]] = {"cpu": float(d["CPUPerc"].rstrip("%") or 0), "mem_mib": parse_mem(d["MemUsage"].split("/")[0]),
                          "limit_mib": parse_mem(d["MemUsage"].split("/")[-1])}
    return out


def host_stats():
    o = sh(["powershell", "-NoProfile", "-Command",
            "$o=Get-CimInstance Win32_OperatingSystem; \"$($o.FreePhysicalMemory) $($o.TotalVisibleMemorySize) $((Get-PSDrive C).Free)\""], 30).split()
    if len(o) < 3:
        return {}
    return {"host_free_ram_mib": round(int(o[0]) / 1024), "host_total_ram_mib": round(int(o[1]) / 1024), "c_free_mib": round(int(o[2]) / 1048576)}


def docker_info():
    try:
        j = json.loads(sh(["docker", "info", "--format", "{{json .}}"], 30))
        return {"vm_mem_total_mib": round(j["MemTotal"] / 1048576), "vm_cpus": j["NCPU"], "containers_running": j["ContainersRunning"]}
    except Exception:
        return {}


def oom_and_restarts():
    names = sh(["docker", "ps", "-q"], 30).split()
    out = {}
    if not names:
        return out
    for l in sh(["docker", "inspect", "-f", "{{.Name}} {{.State.OOMKilled}} {{.RestartCount}}"] + names, 60).splitlines():
        p = l.split()
        if len(p) == 3:
            out[p[0].lstrip("/")] = {"oom": p[1] == "true", "restarts": int(p[2])}
    return out


def kafka_lag(group="executor"):
    o = sh(["docker", "compose", "exec", "-T", "redpanda", "rpk", "group", "describe", group], 60, cwd=INFRA)
    m = re.search(r"TOTAL-LAG\s+(\d+)", o)
    st = re.search(r"STATE\s+(\S+)", o)
    return {"total_lag": int(m[1]) if m else None, "state": st[1] if st else None}


def redpanda_disk_mib():
    o = sh(["docker", "compose", "exec", "-T", "redpanda", "du", "-sm", "/var/lib/redpanda/data"], 60, cwd=INFRA).split()
    return int(o[0]) if o and o[0].isdigit() else None


def sample():
    s = {"t": time.time(), "health": http("GET", API + "/api/health/summary")[1], "docker": docker_stats(), "host": host_stats(), "lag": kafka_lag(), "lag_catalog": kafka_lag("frame-catalog"), "rp_disk_mib": redpanda_disk_mib()}
    st, ws = http("GET", API + "/api/workers")
    st, au = http("GET", API + "/api/autoscaler")
    s["autoscaler"] = ({"mode": au.get("mode"), "current": au.get("current"), "desired": au.get("desired"), "events": (au.get("events") or [])[-5:]}
                       if st == 200 and isinstance(au, dict) else {"unavailable": st})
    s["workers"] = [{"worker_id": w["worker_id"], "capacity": w.get("capacity"), "n": len(w.get("assigned") or []), "state": w.get("state"),
                     "managed_by": w.get("managed_by")} for w in ws] if st == 200 else []
    return s


def prom_collect(win):
    W = f"{int(win)}s"
    P = {}
    fps = prom(f"sum by (camera_id) (rate(vp_extractor_frames_total[{W}]))")
    online = {m["camera_id"] for m, v in prom("vp_extractor_camera_state{state='online'}") if v == 1}
    allv = [v for m, v in fps]
    onv = [v for m, v in fps if m["camera_id"] in online]
    P["ingest_fps_all"] = {"n": len(allv), "p5": r2(pct(allv, 5)), "p50": r2(pct(allv, 50)), "min": r2(min(allv) if allv else None)}
    P["ingest_fps_online_now"] = {"n": len(onv), "p5": r2(pct(onv, 5)), "p50": r2(pct(onv, 50)), "min": r2(min(onv) if onv else None)}
    P["cameras_below_2_4fps_online"] = sorted(m["camera_id"] for m, v in fps if m["camera_id"] in online and v < 2.4)[:30]
    P["frames_raw_produce_rate"] = r2(sum(allv))
    for key, metric in (("dropped_frames", "vp_extractor_frames_dropped_total"), ("bad_frames", "vp_extractor_bad_frames_total"),
                        ("publish_errors", "vp_extractor_publish_errors_total"), ("ffmpeg_restarts", "vp_extractor_ffmpeg_restarts_total")):
        P[key] = r2(pscalar(f"sum(increase({metric}[{W}]))", 0), 0)
    P["ffmpeg_restarts_by_camera_top"] = {m.get("camera_id", "?"): round(v) for m, v in sorted(
        prom(f"sum by (camera_id) (increase(vp_extractor_ffmpeg_restarts_total[{W}]))"), key=lambda x: -x[1])[:10] if v >= 1}
    P["extractor_camera_states"] = {m["state"]: int(v) for m, v in prom("sum by (state) (vp_extractor_camera_state == 1)")}
    P["extractor_cameras_running"] = {m.get("worker"): int(v) for m, v in prom("vp_extractor_cameras_running")}
    P["worker_targets_up"] = int(pscalar("count(up{job='frame-extractor'} == 1)", 0))
    P["kafka_frames_raw"] = {
        "produce_msgs_per_s": r2(pscalar(f"sum(rate(redpanda_kafka_records_produced_total{{redpanda_topic='frames.raw'}}[{W}]))")),
        "produce_MB_per_s": r2((pscalar(f"sum(rate(redpanda_kafka_request_bytes_total{{redpanda_topic='frames.raw',redpanda_request='produce'}}[{W}]))") or 0) / 1048576),
        "consume_MB_per_s": r2((pscalar(f"sum(rate(redpanda_kafka_request_bytes_total{{redpanda_topic='frames.raw',redpanda_request='consume'}}[{W}]))") or 0) / 1048576)}
    pm, pb = P["kafka_frames_raw"]["produce_msgs_per_s"], P["kafka_frames_raw"]["produce_MB_per_s"]
    P["kafka_frames_raw"]["avg_jpeg_KB"] = r2(pb * 1024 / pm) if pm else None
    P["redpanda_disk_free_GiB"] = r2((pscalar("redpanda_storage_disk_free_bytes") or 0) / 2**30, 1)
    for st in ("first", "final"):
        P[f"detection_age_{st}_s"] = {q: r2(pscalar(f"histogram_quantile({q}, sum by (le) (rate(vp_executor_detection_age_seconds_bucket{{stage='{st}'}}[{W}])))")) for q in ("0.5", "0.95")}
    P["frame_catalog"] = {"records": pscalar("vp_catalog_records"), "pending": pscalar("vp_catalog_pending"),
                          "frames_per_s": r2(pscalar(f"rate(vp_catalog_frames_total[{W}])")),
                          "image_requests": {m.get("result"): round(v) for m, v in prom(f"increase(vp_catalog_image_requests_total[{W}])")}}
    P["controller_desired_workers"] = pscalar("vp_controller_desired_workers")
    P["executor_rates_per_s"] = {m.get("result", "?"): r2(v) for m, v in prom(f"sum by (result) (rate(vp_executor_frames_total[{W}]))")}
    P["executor_skipped_per_s"] = {m.get("reason", "?"): r2(v) for m, v in prom(f"sum by (reason) (rate(vp_executor_frames_skipped_total[{W}]))")}
    P["executor_step_mean_ms"] = r2((pscalar(f"sum(rate(vp_executor_step_duration_seconds_sum[{W}]))/sum(rate(vp_executor_step_duration_seconds_count[{W}]))") or 0) * 1000, 1)
    P["executor_inflight"] = pscalar("sum(vp_executor_inflight)")
    P["realtime_consumer_lag_ms"] = {m.get("topic"): v for m, v in prom("vp_realtime_consumer_lag_ms")}
    P["realtime_dropped_per_s"] = {m.get("reason"): r2(v, 3) for m, v in prom(f"sum by (reason) (rate(vp_realtime_dropped_total[{W}]))")}
    P["realtime_connections"] = pscalar("sum(vp_realtime_connections)")
    P["registry_cameras"] = {m.get("state", "all"): v for m, v in prom("vp_registry_cameras")}
    P["controller_decisions"] = {m.get("action"): round(v) for m, v in prom(f"increase(vp_controller_decisions_total[{W}])") if v}
    P["controller_workers"] = {m.get("state"): v for m, v in prom("vp_controller_workers")}
    P["stream_health_cameras"] = {m.get("state", "all"): v for m, v in prom("vp_streamhealth_cameras")}
    return P


def run_step(n, args, tag):
    out = {"step": n, "tag": tag, "started": time.strftime("%Y-%m-%d %H:%M:%S"), "complete": False}
    path = os.path.join(RES, f"{tag}step-{n:03d}.json")
    print(f"== step {n} cameras ==", flush=True)
    t0 = time.time()
    if not args.no_register:
        new = ensure_cameras(n)
        out["registered_or_enabled"] = len(new)
        print(f"  registered/enabled {len(new)}; waiting for assignment...", flush=True)
        out["assignment"] = wait_assigned(new, 300) if new else {"requested": 0, "max_s": None}
        print(f"  assignment: {out['assignment']}", flush=True)
    out["registration_total_s"] = r2(time.time() - t0, 1)
    out["cameras_enabled"] = sum(1 for c in get_cameras().values() if c.get("enabled", True))
    print(f"  settle {args.settle}s", flush=True)
    sleep(args.settle)
    ts = []
    out["measure_start"] = time.time()
    ms = out["measure_start"] + args.measure
    print(f"  measure {args.measure}s", flush=True)
    while not STOP:
        ts.append(sample())
        print(f"    sample {len(ts)}: lag={ts[-1]['lag']['total_lag']} health={ts[-1]['health']}", flush=True)
        if time.time() >= ms:
            break
        sleep(max(0, min(args.interval, ms - time.time())))
    out["measure_end"] = time.time()
    win = max(10, out["measure_end"] - out["measure_start"])
    if STOP:
        out["interrupted"] = True
        out["samples"] = ts
        json.dump(out, open(path.replace(".json", ".partial.json"), "w"), indent=1)
        return None
    out["prometheus"] = prom_collect(win)
    agg = {}
    for s in ts:
        for k, d in s["docker"].items():
            a = agg.setdefault(k, {"cpu": [], "mem": [], "limit_mib": d["limit_mib"]})
            a["cpu"].append(d["cpu"])
            a["mem"].append(d["mem_mib"] or 0)
    out["docker"] = {k: {"cpu_avg": r2(statistics.mean(a["cpu"])), "cpu_max": r2(max(a["cpu"])), "mem_avg_mib": r2(statistics.mean(a["mem"]), 1),
                         "mem_max_mib": r2(max(a["mem"]), 1), "limit_mib": a["limit_mib"]} for k, a in agg.items()}
    out["docker_total_cpu_avg"] = r2(sum(v["cpu_avg"] for v in out["docker"].values()))
    out["docker_total_mem_avg_mib"] = r2(sum(v["mem_avg_mib"] for v in out["docker"].values()), 0)
    out["docker_info"] = docker_info()
    hs = [s["host"] for s in ts if s["host"]]
    out["host"] = {"free_ram_mib_min": min((h["host_free_ram_mib"] for h in hs), default=None),
                   "free_ram_mib_last": hs[-1]["host_free_ram_mib"] if hs else None,
                   "total_ram_mib": hs[-1]["host_total_ram_mib"] if hs else None, "c_free_mib_min": min((h["c_free_mib"] for h in hs), default=None)}
    lags = [s["lag"]["total_lag"] for s in ts if s["lag"]["total_lag"] is not None]
    out["kafka_executor_lag"] = {"first": lags[0] if lags else None, "last": lags[-1] if lags else None, "max": max(lags) if lags else None,
                                 "growing": bool(len(lags) >= 4 and lags[-1] > lags[0] * 1.5 + 50)}
    cl = [s["lag_catalog"]["total_lag"] for s in ts if s["lag_catalog"]["total_lag"] is not None]
    out["kafka_catalog_lag"] = {"first": cl[0] if cl else None, "last": cl[-1] if cl else None, "max": max(cl) if cl else None}
    rd = [s["rp_disk_mib"] for s in ts if s["rp_disk_mib"] is not None]
    out["redpanda_data_mib"] = {"first": rd[0] if rd else None, "last": rd[-1] if rd else None}
    out["autoscaler_last"] = ts[-1]["autoscaler"]
    out["health_last"] = ts[-1]["health"]
    out["workers_last"] = ts[-1]["workers"]
    out["workers_utilization"] = [r2(w["n"] / w["capacity"], 2) if w.get("capacity") else None for w in ts[-1]["workers"]]
    ok = oom_and_restarts()
    out["oom_killed"] = [k for k, v in ok.items() if v["oom"]]
    out["container_restart_counts"] = {k: v["restarts"] for k, v in ok.items() if v["restarts"]}
    out["ui_live_wall_4x4"] = "MANUAL: open http://localhost:5173 Live wall 4x4"
    out["detection_age"] = "see prometheus.detection_age_first_s / detection_age_final_s (p50, p95)"
    fails = []
    pr = out["prometheus"]
    if pr["cameras_below_2_4fps_online"]:
        fails.append(f"{len(pr['cameras_below_2_4fps_online'])} online cameras < 2.4 fps")
    if out["oom_killed"]:
        fails.append(f"OOM: {out['oom_killed']}")
    if (out.get("assignment") or {}).get("max_s") and out["assignment"]["max_s"] > 30:
        fails.append("assignment > 30 s")
    if out["kafka_executor_lag"]["growing"]:
        fails.append("executor lag growing")
    out["failure_criteria_hit"] = fails
    out["complete"] = True
    json.dump(out, open(path, "w"), indent=1)
    print(f"  saved {path}; failures: {fails}", flush=True)
    return out


# ---------- report
def fmt(x):
    return "-" if x is None else str(x)


def report(tag, outname):
    steps = []
    for f in sorted(os.listdir(RES)):
        if f.startswith(tag + "step-") and f.endswith(".json") and ".partial" not in f:
            steps.append(json.load(open(os.path.join(RES, f))))
    steps = sorted([s for s in steps if s.get("complete")], key=lambda s: s["cameras_enabled"])
    L = ["# Scale report", "", f"Generated {time.strftime('%Y-%m-%d %H:%M')} from {len(steps)} step(s). Raw JSON: `qa/perf/results/`.", ""]
    if not steps:
        L.append("No completed steps.")
        open(os.path.join(HERE, outname), "w").write("\n".join(L))
        return
    L += ["## 1. Per-step results", "", "| metric | " + " | ".join(f"{s['cameras_enabled']} cams" for s in steps) + " |", "|---|" + "---|" * len(steps)]

    def row(name, fn):
        L.append(f"| {name} | " + " | ".join(fmt(fn(s)) for s in steps) + " |")

    P = lambda s: s["prometheus"]
    row("ingest fps p5 / p50 / min (online cams)", lambda s: "{} / {} / {}".format(*(P(s)["ingest_fps_online_now"][k] for k in ("p5", "p50", "min"))))
    row("online cameras < 2.4 fps", lambda s: len(P(s)["cameras_below_2_4fps_online"]))
    row("frames.raw produce rate (fps)", lambda s: P(s)["frames_raw_produce_rate"])
    row("dropped frames (window)", lambda s: P(s)["dropped_frames"])
    row("ffmpeg restarts (window)", lambda s: P(s)["ffmpeg_restarts"])
    row("camera states (extractor)", lambda s: P(s)["extractor_camera_states"])
    row("health summary", lambda s: s["health_last"])
    row("time to assign new cams p50 / max (s)", lambda s: "{} / {}".format((s.get("assignment") or {}).get("p50_s"), (s.get("assignment") or {}).get("max_s")))
    row("workers / utilization", lambda s: f"{len(s['workers_last'])} / {s['workers_utilization']}")
    row("docker total CPU avg (% of 1 core) / mem (MiB)", lambda s: f"{s['docker_total_cpu_avg']} / {s['docker_total_mem_avg_mib']}")
    row("Docker VM mem total (MiB)", lambda s: s["docker_info"].get("vm_mem_total_mib"))
    row("host free RAM min (MiB) / C: free min (MiB)", lambda s: f"{s['host']['free_ram_mib_min']} / {s['host']['c_free_mib_min']}")
    row("executor lag first -> last (max)", lambda s: f"{s['kafka_executor_lag']['first']} -> {s['kafka_executor_lag']['last']} ({s['kafka_executor_lag']['max']})")
    row("frames.raw produce msgs/s / MB/s / avg KB", lambda s: "{} / {} / {}".format(*(P(s)["kafka_frames_raw"][k] for k in ("produce_msgs_per_s", "produce_MB_per_s", "avg_jpeg_KB"))))
    row("redpanda CPU avg/max % ; mem max MiB", lambda s: next(("{} / {} ; {}".format(v["cpu_avg"], v["cpu_max"], v["mem_max_mib"]) for k, v in s["docker"].items() if "redpanda-1" in k), None))
    row("redpanda data dir (MiB) first -> last", lambda s: "{} -> {}".format(s["redpanda_data_mib"]["first"], s["redpanda_data_mib"]["last"]))
    row("catalog lag last (max); records; img req", lambda s: "{} ({}); {}; {}".format(s["kafka_catalog_lag"]["last"], s["kafka_catalog_lag"]["max"], P(s)["frame_catalog"]["records"], P(s)["frame_catalog"]["image_requests"]))
    row("detection age first p50/p95 (s)", lambda s: "{} / {}".format(P(s)["detection_age_first_s"]["0.5"], P(s)["detection_age_first_s"]["0.95"]))
    row("detection age final p50/p95 (s)", lambda s: "{} / {}".format(P(s)["detection_age_final_s"]["0.5"], P(s)["detection_age_final_s"]["0.95"]))
    row("autoscaler current/desired", lambda s: "{} / {}".format((s["autoscaler_last"] or {}).get("current", P(s)["controller_workers"].get("live")), (s["autoscaler_last"] or {}).get("desired", P(s)["controller_desired_workers"])))
    row("executor ok / superseded / stale (per s)", lambda s: "{} / {} / {}".format(P(s)["executor_rates_per_s"].get("ok"), P(s)["executor_skipped_per_s"].get("superseded"), P(s)["executor_skipped_per_s"].get("stale")))
    row("executor step mean (ms)", lambda s: P(s)["executor_step_mean_ms"])
    row("realtime-gateway lag detections (ms)", lambda s: (P(s)["realtime_consumer_lag_ms"] or {}).get("detections"))
    row("OOM kills / container restarts", lambda s: f"{s['oom_killed']} / {s['container_restart_counts']}")
    row("failure criteria hit", lambda s: s["failure_criteria_hit"] or "none")
    row("UI Live wall 4x4", lambda s: "manual check")
    last = steps[-1]
    L += ["", f"## 2. Top containers by CPU at {last['cameras_enabled']} cameras", "",
          "| container | CPU avg % | CPU max % | mem max MiB | limit MiB | mem % of limit |", "|---|---|---|---|---|---|"]
    for k, v in sorted(last["docker"].items(), key=lambda kv: -kv[1]["cpu_avg"])[:12]:
        L.append(f"| {k} | {v['cpu_avg']} | {v['cpu_max']} | {v['mem_max_mib']} | {v['limit_mib']} | {round(100 * v['mem_max_mib'] / v['limit_mib']) if v['limit_mib'] else '-'} |")
    L += ["", "## 3. First resource to saturate (share of capacity, last step)", ""]
    cand = []
    vm = last["docker_info"].get("vm_mem_total_mib")
    for k, v in last["docker"].items():
        if v["limit_mib"] and (not vm or v["limit_mib"] < vm * 0.9):
            cand.append((v["mem_max_mib"] / v["limit_mib"], f"memory limit of `{k}` ({v['mem_max_mib']}/{v['limit_mib']} MiB)"))
        if v["cpu_avg"] >= 95:
            cand.append((v["cpu_avg"] / 100, f"CPU of `{k}` pinned at {v['cpu_avg']}% (single-threaded/limit bound)"))
    ncpu = last["docker_info"].get("vm_cpus") or 16
    cand.append((last["docker_total_cpu_avg"] / (100 * ncpu), f"total CPU ({last['docker_total_cpu_avg']}% of {ncpu} cores)"))
    if vm:
        cand.append((last["docker_total_mem_avg_mib"] / vm, f"Docker VM memory ({last['docker_total_mem_avg_mib']}/{vm} MiB)"))
    cand.sort(reverse=True)
    L += [f"* {round(100 * c)}% - {d}" for c, d in cand[:6]]
    L += ["", "## 4. Per-camera cost (slope first -> last step) and 400-camera extrapolation", ""]
    a, b = steps[0], steps[-1]
    dn = b["cameras_enabled"] - a["cameras_enabled"]
    if dn > 0:
        per = lambda f: (f(b) - f(a)) / dn
        cpu_c = per(lambda s: s["docker_total_cpu_avg"]) / 100
        mem_c = per(lambda s: s["docker_total_mem_avg_mib"])
        isfx = lambda k: "frame-extractor" in k or "fx-auto" in k
        fx = lambda s: sum(v["cpu_avg"] for k, v in s["docker"].items() if isfx(k))
        fxm = lambda s: sum(v["mem_avg_mib"] for k, v in s["docker"].items() if isfx(k))
        st_c = per(lambda s: next((v["cpu_avg"] for k, v in s["docker"].items() if "redpanda-1" in k), 0)) / 100
        fps_c = P(b)["frames_raw_produce_rate"] / max(1, b["cameras_enabled"])
        kb = P(b)["kafka_frames_raw"]["avg_jpeg_KB"] or 40
        L += [f"* CPU per camera (all containers): {cpu_c:.3f} cores; extractors only {per(fx) / 100:.3f}; redpanda {st_c:.4f}",
              f"* Memory per camera (all containers): {mem_c:.1f} MiB; extractors only {per(fxm):.1f} MiB",
              f"* Frames: {fps_c:.2f} fps/camera; at {kb:.0f} KB/JPEG = {fps_c * kb / 1024:.2f} MB/s/camera into Kafka",
              "", "| 400 cameras (linear extrapolation) | value |", "|---|---|"]
        base_cpu = a["docker_total_cpu_avg"] / 100 - cpu_c * a["cameras_enabled"]
        base_mem = a["docker_total_mem_avg_mib"] - mem_c * a["cameras_enabled"]
        L += [f"| extractor workers (capacity 12, 70% util) | {math.ceil(400 / (12 * 0.7))} |", f"| CPU cores | {base_cpu + cpu_c * 400:.1f} (fixed part {base_cpu:.1f}) |",
              f"| RAM | {(base_mem + mem_c * 400) / 1024:.1f} GiB |", f"| frames.raw / Kafka produce | {fps_c * 400:.0f} msg/s |",
              f"| Kafka frames.raw bandwidth/retention | {fps_c * 400 * kb / 1024:.0f} MB/s produce, ~{fps_c * 400 * kb * 300 / 1024 / 1024:.1f} GB on disk (5 min retention, 1 replica) |"]
    else:
        L.append("Need at least two steps with different camera counts for slopes.")
    L += ["", "## 5. Notes", "", "* CPU = `docker stats` (100% = one core), averaged over samples in the measure window; fps/rates from Prometheus over the same window.",
          "* Detection age is not exported as a metric; proxies are executor stale/superseded rates and realtime-gateway consumer lag.",
          "* UI Live wall 4x4 must be checked by eye."]
    open(os.path.join(HERE, outname), "w", encoding="utf-8").write("\n".join(L) + "\n")
    print("wrote", outname)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--steps", default="20,50,80,100")
    ap.add_argument("--settle", type=int, default=120)
    ap.add_argument("--measure", type=int, default=180)
    ap.add_argument("--interval", type=int, default=30)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--no-register", action="store_true")
    ap.add_argument("--down-to", type=int)
    ap.add_argument("--report", action="store_true")
    ap.add_argument("--dry", action="store_true")
    args = ap.parse_args()
    os.makedirs(RES, exist_ok=True)
    tag = "dry-" if args.dry else ""
    rep = "SCALE-REPORT-dry.md" if args.dry else "SCALE-REPORT.md"
    if args.down_to is not None:
        return down_to(args.down_to)
    if args.report:
        return report(tag, rep)
    for n in [int(x) for x in args.steps.split(",")]:
        if STOP:
            break
        if not args.force and os.path.exists(os.path.join(RES, f"{tag}step-{n:03d}.json")):
            print(f"step {n} already done (use --force)")
            continue
        if run_step(n, args, tag) is None:
            break
    report(tag, rep)
    sys.exit(130 if STOP else 0)


main()
