"""Decode benchmark. Runs INSIDE vp/frame-extractor:dev with --cpus=1 (see run.sh). Measures CPU time (rusage
user+sys of the ffmpeg child, via wait4), NOT wall clock, so a busy host does not matter (cache/SMT effects still
add some noise: hence 3 repeats, median + spread). Output chain = production (extractor/ffmpeg_reader.py):
  ffmpeg -threads 1 [dec opts] -i clip -an -vf fps=3,scale='min(640,iw)':-2 -c:v mjpeg -q:v 5 -threads 1 -f image2pipe -> /dev/null
usage: bench.py CLIPDIR OUT.json [REPEATS]
"""
import json, os, statistics, subprocess, sys, time

clipdir, outp = sys.argv[1], sys.argv[2]
reps = int(sys.argv[3]) if len(sys.argv) > 3 else 3
ffmpeg = "ffmpeg"

def probe_dur(p):
    o = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", p],
                       capture_output=True, text=True).stdout.strip()
    return float(o)

def cmd(clip, fps, dec):
    return [ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error", "-threads", "1", *dec, "-i", clip,
            "-an", "-sn", "-dn", "-vf", f"fps={fps},scale='min(640,iw)':-2",
            "-c:v", "mjpeg", "-q:v", "5", "-threads", "1", "-f", "image2pipe", "-"]

def run(argv):
    t0 = time.time()
    p = subprocess.Popen(argv, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    _, status, ru = os.wait4(p.pid, 0)
    err = p.stderr.read().decode(errors="replace")
    if status != 0 or err.strip():
        print("WARN", argv[-6:], status, err[:200], file=sys.stderr)
    return ru.ru_utime, ru.ru_stime, time.time() - t0

# (res, codec, variant label, clip, fps, decoder opts)
cfgs = []
for res in ("360p", "540p", "720p", "1080p"):
    g2, g1 = f"h264_{res}_gop2s.mp4", f"h264_{res}_gop1s.mp4"
    cfgs += [(res, "h264", "baseline", g2, 3, []),
             (res, "h264", "skip_loop_filter", g2, 3, ["-skip_loop_filter", "all"]),
             (res, "h264", "keyframe-only GOP1s (fps=3)", g1, 3, ["-skip_frame", "nokey"]),
             (res, "h264", "keyframe-only GOP1s (fps=1)", g1, 1, ["-skip_frame", "nokey"])]
for res in ("720p", "1080p"):
    c = f"h265_{res}_gop2s.mp4"
    if os.path.exists(os.path.join(clipdir, c)):
        cfgs += [(res, "h265", "baseline", c, 3, []),
                 (res, "h265", "skip_loop_filter", c, 3, ["-skip_loop_filter", "all"])]
durs = {}
res_ = {i: [] for i in range(len(cfgs))}
for r in range(reps):
    for i, (res, codec, var, clip, fps, dec) in enumerate(cfgs):
        path = os.path.join(clipdir, clip)
        if clip not in durs:
            durs[clip] = probe_dur(path)
        u, s, w = run(cmd(path, fps, dec))
        res_[i].append((u + s, w))
        print(f"rep{r+1} {codec} {res} {var}: cpu={u+s:.2f}s ({(u+s)/durs[clip]:.4f} cpu-s/src-s) wall={w:.1f}s", flush=True)
rows = []
for i, (res, codec, var, clip, fps, dec) in enumerate(cfgs):
    d = durs[clip]
    per = sorted(c / d for c, _ in res_[i])
    med = statistics.median(per)
    rows.append(dict(res=res, codec=codec, variant=var, clip=clip, src_seconds=round(d, 2), repeats=len(per),
                     cpu_per_src_s_median=round(med, 4), min=round(per[0], 4), max=round(per[-1], 4),
                     spread_pct=round((per[-1] - per[0]) / med * 100, 1), cores_per_100_cams=round(med * 100, 1)))
json.dump(rows, open(outp, "w"), indent=1)
