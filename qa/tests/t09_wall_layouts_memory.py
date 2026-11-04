"""Live wall: layouts 1/2x2/3x3/4x4, paging, video playing per tile, filters, JS heap sampling over 2 min, FPS of main thread."""
import sys, time, re, json
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
s = Session(w=1920, h=1080); p = s.page
s.goto("/"); time.sleep(3)
print("mock banner present:", "mock" in p.inner_text("body").lower() or "simulated" in p.inner_text("body").lower())
def status(): 
    v = p.evaluate(VIDEO_JS); return len(v), sum(1 for x in v if x["rs"]>=2 and x["w"]>0), p.inner_text("main")[170:260].replace("\n"," | ")
for lay in ["1", "2×2", "3×3", "4×4"]:
    p.locator("button", has_text=re.compile("^"+lay+"$")).click(); time.sleep(9)
    print(lay, "videos/playing/text:", status()); s.shot("t09-layout-"+lay.replace("×","x"))
# paging in 4x4
for i in range(1):
    p.locator("button", has_text="Next").click(); time.sleep(6)
    ids = p.evaluate("[...document.querySelectorAll('main .font-mono')].map(e=>e.textContent).slice(0,20)")
    print("page", i+2, len(ids), ids[:3], status()[:2], "next disabled:", p.locator("button", has_text="Next").is_disabled())
# filters
sel = p.locator("main select").first
for st in ["Degraded", "Offline", "Unassigned", "Online"]:
    sel.select_option(label=st); time.sleep(2); print("filter", st, p.inner_text("main")[170:300].replace("\n"," | "))
sel.select_option(index=0)
# memory/CPU sampling on 4x4 page 1
time.sleep(3)
samples = []
for i in range(13):
    m = s.metrics(); h = p.evaluate("performance.memory.usedJSHeapSize")
    samples.append((i*10, round(h/1e6,1), round(m["JSHeapUsedSize"]/1e6,1), m["Nodes"], m["JSEventListeners"], round(m["TaskDuration"],1)))
    time.sleep(10)
print("t(s), usedJSHeap MB, cdp heap MB, DOM nodes, listeners, cumulative task s:"); [print(x) for x in samples]
print("videos:", status()[:2]); s.shot("t09-after-2min")
s.report(); s.close()
