"""Edit a camera that belongs to a plan through the UI: plan_id must be kept; enable toggle persists."""
import sys, time, json, urllib.request
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
def api(path): return json.load(urllib.request.urlopen(API + path))
s = Session(); p = s.page; s.goto("/sources"); time.sleep(4)
print("before:", api("/api/cameras/qa-plan-cam")["plan_id"])
p.locator("[aria-label='Edit QA plan']").click(); time.sleep(.5)
d = p.get_by_role("dialog"); print("dialog plan select:", d.locator("select").evaluate("e=>e.options[e.selectedIndex].text"))
d.get_by_placeholder("Gate 3").fill("QA plan renamed"); d.locator("button[type=submit]").click(); time.sleep(3)
c = api("/api/cameras/qa-plan-cam"); print("after edit:", c["name"], c["plan_id"])
p.locator("[aria-label='Enable QA plan renamed']").click(); time.sleep(3)
print("enabled after toggle:", api("/api/cameras/qa-plan-cam")["enabled"])
time.sleep(8); print("health:", [h["state"] for h in api("/api/health/cameras") if h["camera_id"]=="qa-plan-cam"])
s.report(); s.close()
