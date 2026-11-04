"""Bug 3: Sources add/edit/delete through the UI, validation, live wall shows new camera + video."""
import sys, time, re, json, urllib.request
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
def api(path): return json.load(urllib.request.urlopen(API + path))
s = Session(); p = s.page
s.goto("/sources"); time.sleep(3)
p.get_by_role("button", name="Add camera").click(); time.sleep(.5)
d = p.get_by_role("dialog")
PH={"Camera id":"cam-021","Name":"Gate 3","Stream URL":"rtsp://user:pass"}
def fill(label, v): d.get_by_placeholder(PH[label], exact=False).first.fill(v)
# validation
fill("Camera id", "Bad_ID"); fill("Name", "QA UI cam"); fill("Stream URL", "ftp://x")
d.locator("button[type=submit]").click(); time.sleep(.5)
print("validation text:", d.inner_text()[:500].replace("\n"," | ")); s.shot("t06-validation")
fill("Camera id", "qa-ui-cam"); fill("Stream URL", "rtsp://mediamtx:8554/cam-005")
d.locator("button[type=submit]").click(); time.sleep(4)
print("dialog open after save:", p.get_by_role("dialog").count())
c = api("/api/cameras/qa-ui-cam"); print("API camera:", c)
print("row in table:", "qa-ui-cam" in p.inner_text("main"))
time.sleep(10)
print("health:", [h for h in api("/api/health/cameras") if h["camera_id"]=="qa-ui-cam"])
# live wall
s.goto("/live/qa-ui-cam"); time.sleep(10)
print("focus video:", p.evaluate(VIDEO_JS)); s.shot("t06-new-cam-focus")
s.goto("/"); time.sleep(2)
p.get_by_placeholder("Search camera / tag").fill("qa-ui"); time.sleep(8)
print("wall video:", p.evaluate(VIDEO_JS), "tiles:", p.inner_text("main")[:200].replace("\n"," | "))
# edit
s.goto("/sources"); time.sleep(3)
p.locator("[aria-label=\"Edit QA UI cam\"]").click(); time.sleep(.5)
d = p.get_by_role("dialog"); print("edit dialog:", d.inner_text()[:300].replace("\n"," | "))
fill("Name", "QA UI cam renamed"); d.locator("button[type=submit]").click(); time.sleep(3)
print("after edit:", api("/api/cameras/qa-ui-cam")["name"])
# duplicate id
p.get_by_role("button", name="Add camera").click(); d = p.get_by_role("dialog")
fill("Camera id", "cam-001"); fill("Name", "dup"); fill("Stream URL", "rtsp://a/b"); d.locator("button[type=submit]").click(); time.sleep(1)
print("dup text:", d.inner_text()[:300].replace("\n"," | "))
p.keyboard.press("Escape"); time.sleep(.5)
# delete
p.locator("[aria-label=\"Delete QA UI cam renamed\"]").click(); time.sleep(.5)
p.get_by_role("dialog").get_by_role("button", name="Delete").click(); time.sleep(3)
try: api("/api/cameras/qa-ui-cam"); print("STILL EXISTS")
except Exception as e: print("deleted ->", e)
try: urllib.request.urlopen("http://localhost:9997/v3/config/paths/get/qa-ui-cam"); print("MediaMTX path STILL EXISTS")
except Exception as e: print("mediamtx path gone ->", e)
print("table has it:", "qa-ui-cam" in p.inner_text("main"))
s.report(); s.close()
