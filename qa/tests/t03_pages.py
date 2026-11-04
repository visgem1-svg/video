"""Visit every page; dump text, console/network errors, screenshots."""
import sys, time
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
s = Session(); p = s.page
for name, path in [("recog","/recognitions"),("pipes","/pipelines"),("sources","/sources"),("health","/health"),("focus","/live/cam-001")]:
    s.goto(path); time.sleep(7); s.shot("t03-"+name)
    print("=====", path); print(p.inner_text("body")[:1800].replace("\n"," | "))
    if name=="focus": print(p.evaluate(VIDEO_JS))
s.report(); s.close()
