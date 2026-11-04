"""Focus view: wait until a box is on the canvas, screenshot; report how long boxes are visible and gaps between detections."""
import sys, time
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
cam = sys.argv[1] if len(sys.argv) > 1 else "cam-005"
s = Session(); p = s.page; s.goto("/live/" + cam); time.sleep(6)
js = "() => { const c=document.querySelector('canvas'); const d=c.getContext('2d',{willReadFrequently:true}).getImageData(0,0,c.width,c.height).data; for(let i=3;i<d.length;i+=28) if(d[i]>0) return true; return false }"
t0 = time.time(); vis = []; shot = False; prev = False; on_since = None; spans = []; starts = []
while time.time() - t0 < 45:
    v = p.evaluate(js); now = time.time()
    if v and not prev: on_since = now; starts.append(now - t0)
    if not v and prev: spans.append(now - on_since)
    if v and not shot and now - t0 > 3: s.shot("t13-focus-box-" + cam); shot = True
    prev = v; time.sleep(0.08)
print("box appearances:", len(starts), "visible spans(s):", [round(x,2) for x in spans][:15], "gaps between appearances:", [round(b-a,1) for a,b in zip(starts, starts[1:])][:15])
print(p.inner_text("main")[-400:].replace("\n"," | "))
s.report(); s.close()
