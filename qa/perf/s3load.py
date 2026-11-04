#!/usr/bin/env python3
"""Minimal S3 load generator (stdlib only, SigV4, keep-alive). Open-loop PUT/GET at fixed rates.
usage: s3load.py --endpoint host:port --put-rate 300 --get-rate 30 --size 30000 --secs 120 [--ttl 30]
Objects older than --ttl s are DELETEd (mimics frame TTL; delete ops reported separately)."""
import argparse, hashlib, hmac, http.client, json, os, queue, random, threading, time, datetime

def sign(k, m): return hmac.new(k, m.encode(), hashlib.sha256).digest()

class Cli:
    def __init__(s, ep, ak, sk, region):
        s.ep, s.ak, s.sk, s.region = ep, ak, sk, region
        s.c = None
        s.kd = {}
    def _key(s, d):
        if d not in s.kd:
            k = sign(("AWS4" + s.sk).encode(), d); k = sign(k, s.region); k = sign(k, "s3"); k = sign(k, "aws4_request")
            s.kd = {d: k}
        return s.kd[d]
    def req(s, method, path, body=b""):
        t = datetime.datetime.now(datetime.timezone.utc)
        amz, d = t.strftime("%Y%m%dT%H%M%SZ"), t.strftime("%Y%m%d")
        ph = hashlib.sha256(body).hexdigest()
        hdrs = {"host": s.ep, "x-amz-content-sha256": ph, "x-amz-date": amz}
        sh = ";".join(sorted(hdrs))
        ch = "".join(f"{k}:{hdrs[k]}\n" for k in sorted(hdrs))
        cr = f"{method}\n{path}\n\n{ch}\n{sh}\n{ph}"
        scope = f"{d}/{s.region}/s3/aws4_request"
        sts = f"AWS4-HMAC-SHA256\n{amz}\n{scope}\n{hashlib.sha256(cr.encode()).hexdigest()}"
        sig = hmac.new(s._key(d), sts.encode(), hashlib.sha256).hexdigest()
        hdrs["Authorization"] = f"AWS4-HMAC-SHA256 Credential={s.ak}/{scope}, SignedHeaders={sh}, Signature={sig}"
        hdrs["Content-Length"] = str(len(body))
        for attempt in (0, 1):
            try:
                if s.c is None: s.c = http.client.HTTPConnection(s.ep, timeout=30)
                s.c.request(method, path, body=body, headers=hdrs)
                r = s.c.getresponse(); data = r.read()
                return r.status, data
            except Exception:
                try: s.c.close()
                except Exception: pass
                s.c = None
                if attempt: raise

def pct(a, p):
    if not a: return None
    a = sorted(a); return round(a[min(len(a)-1, int(len(a)*p/100))]*1000, 1)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--endpoint", default="localhost:9000"); ap.add_argument("--ak", default="minio"); ap.add_argument("--sk", default="minio12345")
    ap.add_argument("--region", default="us-east-1"); ap.add_argument("--bucket", default="frames")
    ap.add_argument("--put-rate", type=float, default=300); ap.add_argument("--get-rate", type=float, default=30)
    ap.add_argument("--size", type=int, default=30000); ap.add_argument("--secs", type=float, default=120)
    ap.add_argument("--ttl", type=float, default=30); ap.add_argument("--threads", type=int, default=64)
    ap.add_argument("--make-bucket", action="store_true"); ap.add_argument("--out")
    a = ap.parse_args()
    c0 = Cli(a.endpoint, a.ak, a.sk, a.region)
    if a.make_bucket:
        for _ in range(60):
            try:
                st, _d = c0.req("PUT", f"/{a.bucket}")
                if st in (200, 409): break
            except Exception: pass
            time.sleep(1)
    payloads = [os.urandom(a.size) for _ in range(32)]
    jobs = queue.Queue(); lock = threading.Lock()
    lat = {"PUT": [], "GET": [], "DEL": []}; err = {"PUT": 0, "GET": 0, "DEL": 0}; late = [0]; shed = [0]
    keys = []; dele = queue.Queue()
    stop = time.time() + a.secs
    def worker():
        c = Cli(a.endpoint, a.ak, a.sk, a.region)
        while True:
            j = jobs.get()
            if j is None: return
            kind, key, sched = j
            t0 = time.perf_counter()
            try:
                if kind == "PUT": st, _d = c.req("PUT", f"/{a.bucket}/{key}", random.choice(payloads)); ok = st == 200
                elif kind == "GET": st, d = c.req("GET", f"/{a.bucket}/{key}"); ok = st == 200 and len(d) == a.size
                else: st, _d = c.req("DELETE", f"/{a.bucket}/{key}"); ok = st in (200, 204)
            except Exception: ok = False
            dt = time.perf_counter() - t0
            with lock:
                if ok:
                    # latency measured from request start (service time); queueing tracked in 'late'
                    lat[kind].append(dt)
                    if kind == "PUT": keys.append((time.time(), key))
                else: err[kind] += 1
    ths = [threading.Thread(target=worker, daemon=True) for _ in range(a.threads)]
    [t.start() for t in ths]
    t0 = time.time(); n = 0; ng = 0; nxt_p = t0; nxt_g = t0
    cams = 100
    while time.time() < stop:
        now = time.time()
        while nxt_p <= now:
            if jobs.qsize() > 1000:  # overload: shed (counted) instead of unbounded backlog
                shed[0] += 1; n += 1; nxt_p += 1 / a.put_rate; continue
            jobs.put(("PUT", f"cam-{n % cams:03d}/{int(t0)}-{n}.jpg", nxt_p)); n += 1; nxt_p += 1 / a.put_rate
        while nxt_g <= now:
            with lock: pool = keys[-600:]
            if pool: jobs.put(("GET", random.choice(pool)[1], nxt_g)); ng += 1
            nxt_g += 1 / a.get_rate
        # ttl deletes
        with lock:
            while keys and keys[0][0] < now - a.ttl:
                dele.put(keys.pop(0)[1])
        while not dele.empty() and jobs.qsize() < 200: jobs.put(("DEL", dele.get(), 0))
        if jobs.qsize() > 500: late[0] += 1
        time.sleep(0.002)
    try:
        while True: jobs.get_nowait()
    except queue.Empty: pass
    for _ in ths: jobs.put(None)
    for t in ths: t.join(timeout=30)
    el = time.time() - t0
    res = {"endpoint": a.endpoint, "secs": round(el, 1), "put_target": a.put_rate, "get_target": a.get_rate, "size": a.size,
           "put_ok_rate": round(len(lat["PUT"])/el, 1), "get_ok_rate": round(len(lat["GET"])/el, 1),
           "errors": err, "backlog_ticks": late[0], "put_shed": shed[0]}
    for k in lat:
        res[k] = {"n": len(lat[k]), "p50_ms": pct(lat[k], 50), "p99_ms": pct(lat[k], 99), "max_ms": pct(lat[k], 100)}
    print(json.dumps(res))
    if a.out: open(a.out, "w").write(json.dumps(res))
main()
