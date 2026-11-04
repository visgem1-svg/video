"""Live wall: banner, video health, frames advancing, boxes drawn, console/network errors."""
import sys, time, json
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
s = Session()
s.goto("/")
time.sleep(12)
s.shot("t01-live-wall")
p = s.page
print("title", p.title())
print("body head:", p.inner_text("body")[:600].replace("\n"," | "))
v1 = p.evaluate(VIDEO_JS); time.sleep(3); v2 = p.evaluate(VIDEO_JS)
print("videos", len(v1))
for a, b in zip(v1, v2): print(a, "advance=%.2f" % (b["t"]-a["t"]))
print("canvases", p.evaluate("document.querySelectorAll('canvas').length"))
s.report()
s.close()
