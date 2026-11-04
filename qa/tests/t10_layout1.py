"""Live wall layout '1': tile geometry."""
import sys, time, re
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
for (w,h) in [(1920,1080),(1280,720)]:
    s = Session(w=w,h=h); p = s.page; s.goto("/"); time.sleep(2)
    p.locator("button", has_text=re.compile("^1$")).click(); time.sleep(6)
    print(w,h, p.evaluate("""() => { const v=document.querySelector('video'); const r=v.getBoundingClientRect(); let a=v.parentElement; const chain=[]; for(let i=0;i<4&&a;i++){const b=a.getBoundingClientRect(); chain.push([a.className.slice(0,60), Math.round(b.width), Math.round(b.height)]); a=a.parentElement}
      return {video:[r.x,r.y,r.width,r.height], chain, vw:v.videoWidth} }"""))
    s.shot("t10-layout1-%d" % w); s.close()
