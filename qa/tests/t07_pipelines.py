"""Bug 2: prompt -> draft -> valid plan -> activate -> in Active list -> camera plan_id switches -> watch metrics -> delete.
Uses only lobby-cam so the main plan on cam-001..020 is untouched. Draft takes 2-3 min on local CPU."""
import sys, time, re, json, urllib.request
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
def api(path): return json.load(urllib.request.urlopen(API + path))
s = Session(); p = s.page
s.goto("/pipelines"); time.sleep(3)
print("button state w/o query:", p.locator("button", has_text="Draft plan").is_disabled())
p.get_by_label("Pipeline query").fill("Detect people and read their faces in the lobby")
# pick only lobby-cam
p.locator("button", has_text="Clear").click(); time.sleep(.3)
p.locator("text=lobby-cam").first.click(); time.sleep(.3)
print("selected:", re.findall(r"\d+ of \d+ selected", p.inner_text("main")))
t0 = time.time(); p.locator("button", has_text="Draft plan").click(); time.sleep(5)
s.shot("t07-drafting"); print("drafting UI:", p.locator("[role=status]").inner_text().replace("\n"," | "))
while time.time() - t0 < 420:
    if p.locator("text=No draft yet").count() == 0 and p.locator("[role=status]").count() == 0: break
    time.sleep(5)
print("draft took %.0fs" % (time.time()-t0)); s.shot("t07-draft")
print("draft panel:", p.inner_text("main")[-900:].replace("\n"," | "))
act = p.locator("button", has_text="Activate")
print("activate enabled:", act.count() and act.is_enabled())
if act.count() and act.is_enabled():
    act.click(); time.sleep(6); s.shot("t07-activated")
    plans = api("/api/plans"); print("plans:", [(x["name"], x["plan_id"][:8], x["camera_ids"]) for x in plans])
    print("lobby-cam plan_id:", api("/api/cameras/lobby-cam")["plan_id"], " cam-001 plan:", api("/api/cameras/cam-001")["plan_id"])
    print("tab:", p.url, p.inner_text("main")[:500].replace("\n"," | "))
    time.sleep(25); s.shot("t07-watch")
    print("watch:", p.inner_text("main")[:1500].replace("\n"," | "))
s.report(); s.close()
