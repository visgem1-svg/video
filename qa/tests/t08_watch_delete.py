"""Watch live metrics on plan graph for 100s, then delete the QA plan (lobby) through the UI and verify cameras cleared."""
import sys, time, json, urllib.request
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
def api(path): return json.load(urllib.request.urlopen(API + path))
plans = [x for x in api("/api/plans") if x["camera_ids"] == ["lobby-cam"]]
pid = plans[0]["plan_id"]
s = Session(); p = s.page
s.goto("/pipelines?plan=" + pid); time.sleep(100); s.shot("t08-watch-100s")
print("watch:", p.inner_text("main")[300:900].replace("\n"," | "))
s.goto("/live/lobby-cam"); time.sleep(8); s.shot("t08-focus-lobby"); print("focus:", p.inner_text("main")[:700].replace("\n"," | "))
s.goto("/pipelines?plan=" + pid); time.sleep(3)
p.locator("button", has_text="Delete").first.click(); time.sleep(1)
dlg = p.get_by_role("dialog"); print("confirm:", dlg.inner_text().replace("\n"," | ") if dlg.count() else "NO CONFIRM DIALOG")
if dlg.count(): dlg.get_by_role("button", name="Delete").click()
time.sleep(5)
print("plans after:", [x["name"] for x in api("/api/plans")], "lobby plan_id:", api("/api/cameras/lobby-cam")["plan_id"])
print("ui:", p.inner_text("main")[:300].replace("\n"," | "))
s.report(); s.close()
