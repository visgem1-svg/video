"""Motion-gate calibration (contract 9.9 s3/s6). Runs INSIDE vp/frame-extractor:dev (Debian ffmpeg = production).
Chain under test (same as extractor/ffmpeg_reader.py): fps=3,mpdecimate=hi=H:lo=L:frac=F:max=M,scale=640:-2  -fps_mode vfr
Emitted-frame timestamps come from `showinfo` (pts_time) placed after the scale.
usage: calibrate.py CLIPDIR OUT.json [grid|final] [--jobs N]
  Env: GRID='[[H,L,F],...]' custom grid; PRESETS='{"low":[H,L,F],...}' + MAXDROP for final; EVENTS=events json path.
  CLIPDIR holds S_1.mp4..S_10.mp4 (static, events from S-events.json, mounted at /ev.json) and M_*.mp4 (moving pan/zoom).
  grid : max=0 (no forced frames) over a (H,L,F) grid  -> skipped %, recall, false-trigger rate (noise-only emissions)
  final: max=14 (heartbeat 5 s at 3 fps = production) for the presets in PRESETS
Definitions: sampled = frames out of fps=3 (180 per 60 s). skipped% = 1 - emitted/sampled.
  event hit = >=2 emitted frames with pts in [start, start+1.0] s.  false trigger = emitted frame outside every
  [start, end+1.0] window (noise / compression flicker); rate = false / non-event sampled frames.
"""
import itertools, json, os, re, subprocess, sys
from concurrent.futures import ThreadPoolExecutor

clipdir, outp = sys.argv[1], sys.argv[2]
mode = sys.argv[3] if len(sys.argv) > 3 else "grid"
jobs = int(sys.argv[sys.argv.index("--jobs") + 1]) if "--jobs" in sys.argv else 2
EV = json.load(open(os.environ.get("EVENTS", "/ev.json")))["cameras"]
S = {f"S_{i}.mp4": EV[f"cam-{30+i:03d}"]["events"] for i in range(1, 11)}
PRESETS = json.loads(os.environ.get("PRESETS", "{}"))  # {"low":[H,L,F],...} for final mode

def emitted(clip, H, L, F, M, loops=0):
    vf = f"fps=3,mpdecimate=hi={H}:lo={L}:frac={F}:max={M},scale=640:-2,showinfo"
    cmd = ["ffmpeg", "-hide_banner", "-nostdin", "-threads", "1", "-stream_loop", str(loops), "-i",
           os.path.join(clipdir, clip), "-an", "-vf", vf, "-fps_mode", "vfr", "-f", "null", "-"]
    err = subprocess.run(cmd, capture_output=True, text=True).stderr
    return [float(x) for x in re.findall(r"pts_time:\s*([0-9.]+)", err)]

def sampled(clip, loops=0):
    cmd = ["ffmpeg", "-hide_banner", "-nostdin", "-threads", "1", "-stream_loop", str(loops), "-i", os.path.join(clipdir, clip),
           "-an", "-vf", "fps=3,showinfo", "-f", "null", "-"]
    err = subprocess.run(cmd, capture_output=True, text=True).stderr
    return len(re.findall(r"pts_time:", err))

def score_static(clip, pts, n_sampled):
    ev = S[clip]
    hits = sum(1 for e in ev if sum(e["start"] <= p <= e["start"] + 1.0 for p in pts) >= 2)
    inwin = lambda p: any(e["start"] <= p <= e["end"] + 1.0 for e in ev)
    false = sum(1 for p in pts if not inwin(p))
    ev_frames = sum(1 for k in range(n_sampled) if inwin(k / 3.0))
    return dict(emitted=len(pts), sampled=n_sampled, events=len(ev), hits=hits, false=false, nonevent=n_sampled - ev_frames)

def run_setting(st):
    H, L, F, M = st
    rows = {"S": dict(emitted=0, sampled=0, events=0, hits=0, false=0, nonevent=0), "M": dict(emitted=0, sampled=0)}
    per_clip = {}
    for clip in sorted(os.listdir(clipdir)):
        if not clip.endswith(".mp4"):
            continue
        loops = 3 if clip.startswith("M_") else 0     # moving clips are 8 s seamless loops -> 32 s
        pts = emitted(clip, H, L, F, M, loops)
        ns = NS[(clip)]
        if clip.startswith("S_"):
            r = score_static(clip, pts, ns)
            per_clip[clip] = r
            for k, v in r.items(): rows["S"][k] += v
        else:
            per_clip[clip] = dict(emitted=len(pts), sampled=ns)
            rows["M"]["emitted"] += len(pts); rows["M"]["sampled"] += ns
    S_, M_ = rows["S"], rows["M"]
    return dict(H=H, L=L, F=F, M=M,
                static_skipped_pct=round(100 * (1 - S_["emitted"] / S_["sampled"]), 2),
                recall_pct=round(100 * S_["hits"] / S_["events"], 1), events=S_["events"], hits=S_["hits"],
                false_trigger_pct=round(100 * S_["false"] / S_["nonevent"], 2), false_frames=S_["false"],
                moving_skipped_pct=round(100 * (1 - M_["emitted"] / M_["sampled"]), 2) if M_["sampled"] else None,
                per_clip=per_clip)

NS = {c: sampled(c, 3 if c.startswith("M_") else 0) for c in sorted(os.listdir(clipdir)) if c.endswith(".mp4")}
if os.environ.get("GRID"):   # JSON [[H,L,F],...] overrides the default grid (max=0)
    pts = [(h, l, f, 0) for h, l, f in json.loads(os.environ["GRID"])]
elif mode == "grid":
    pts = [(h, l, f, 0) for h in (384, 768, 1536, 3072) for l in (160, 320, 640, 1280) if l <= h for f in (0.2, 0.33, 0.5)]
else:
    pts = [(*v, int(os.environ.get("MAXDROP", "14"))) for v in PRESETS.values()]
with ThreadPoolExecutor(jobs) as ex:
    out = list(ex.map(run_setting, pts))
    for o in out:
        print({k: v for k, v in o.items() if k != "per_clip"}, flush=True)
json.dump(out, open(outp, "w"), indent=1)
