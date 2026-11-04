"""Detection-quality check for `-skip_loop_filter all`: posts the A (baseline) and B (flag) frames to the detector
(multipart `image`) and compares counts / confidences. usage: detq.py FRAMESDIR [URL] """
import json, os, statistics, sys, time, urllib.error, urllib.request, uuid
root = sys.argv[1]; url = sys.argv[2] if len(sys.argv) > 2 else "http://localhost:8101/detect_objects"
def detect(path):
    b = uuid.uuid4().hex; data = open(path, "rb").read()
    body = (f"--{b}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"f.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n").encode() + data + f"\r\n--{b}--\r\n".encode()
    r = urllib.request.Request(url, body, {"Content-Type": f"multipart/form-data; boundary={b}"})

    for k in range(60):  # detector sheds load with 429/503 while the live system is busy: back off and retry
        try:
            return json.load(urllib.request.urlopen(r, timeout=120))["objects"]
        except urllib.error.HTTPError as e:
            if e.code not in (429, 503): raise
            time.sleep(1 + k * 0.5)
    raise RuntimeError("detector kept shedding load")
def iou(a, b):
    x1, y1, x2, y2 = max(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), min(a[3], b[3])
    i = max(0, x2 - x1) * max(0, y2 - y1); u = (a[2]-a[0])*(a[3]-a[1]) + (b[2]-b[0])*(b[3]-b[1]) - i
    return i / u if u else 0
frames = cntA = cntB = 0; dconf = []; matched = unA = unB = 0; dcnt = []; c50A = c50B = 0
per = {}
for img in sorted(d for d in os.listdir(root) if os.path.isdir(os.path.join(root, d))):
    for f in sorted(os.listdir(f"{root}/{img}/A")):
        A = detect(f"{root}/{img}/A/{f}"); B = detect(f"{root}/{img}/B/{f}")
        frames += 1; cntA += len(A); cntB += len(B); dcnt.append(len(B) - len(A))
        c50A += sum(o["confidence"] >= .5 for o in A); c50B += sum(o["confidence"] >= .5 for o in B)
        used = set()
        for a in A:
            best = max(((iou(a["box"], b["box"]), j) for j, b in enumerate(B) if j not in used and b["class"] == a["class"]), default=(0, -1))
            if best[0] >= .5:
                used.add(best[1]); matched += 1; dconf.append(B[best[1]]["confidence"] - a["confidence"])
            else: unA += 1
        unB += len(B) - len(used)
print(json.dumps(dict(frames=frames, objects_baseline=cntA, objects_flag=cntB, objects_conf_ge_0_5_baseline=c50A, objects_conf_ge_0_5_flag=c50B,
    mean_count_delta_per_frame=round(statistics.mean(dcnt), 3), frames_with_count_diff=sum(1 for d in dcnt if d), matched_iou50=matched,
    only_in_baseline=unA, only_in_flag=unB, conf_delta_mean=round(statistics.mean(dconf), 4), conf_delta_sd=round(statistics.pstdev(dconf), 4),
    conf_delta_min=round(min(dconf), 3), conf_delta_max=round(max(dconf), 3)), indent=1))
