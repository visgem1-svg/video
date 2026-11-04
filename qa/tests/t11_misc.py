"""Misc: hub reconnect (CDP offline), mock banner, unknown camera focus, focus prev/next, mobile viewport, enable toggle, edit keeps plan."""
import sys, time, re, json, urllib.request
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
def api(path, method="GET", body=None):
    r = urllib.request.Request(API+path, method=method, data=json.dumps(body).encode() if body else None, headers={"content-type":"application/json"})
    try: return json.load(urllib.request.urlopen(r))
    except Exception as e: return str(e)
s = Session(); p = s.page
s.goto("/"); time.sleep(6)
print("pill:", p.locator("header").inner_text().replace("\n"," "))
s.cdp.send("Network.enable"); s.cdp.send("Network.emulateNetworkConditions", {"offline": True, "latency":0, "downloadThroughput":-1, "uploadThroughput":-1})
time.sleep(8); print("offline pill:", p.locator("header").inner_text().replace("\n"," "), "| videos:", p.evaluate(VIDEO_JS)[:2]); s.shot("t11-offline")
s.cdp.send("Network.emulateNetworkConditions", {"offline": False, "latency":0, "downloadThroughput":-1, "uploadThroughput":-1})
time.sleep(25); print("online again pill:", p.locator("header").inner_text().replace("\n"," "), "| videos:", [(v["rs"],v["w"]) for v in p.evaluate(VIDEO_JS)][:9], "ws count", len(s.ws))
# focus
s.goto("/live/does-not-exist"); time.sleep(3); print("unknown cam:", p.inner_text("main")[:200].replace("\n"," | "))
s.goto("/live/cam-002"); time.sleep(3)
p.locator("button:has(svg.lucide-chevron-right)").first.click(); time.sleep(2); print("after next:", p.url)
p.locator("a", has_text="Watch").first.click(); time.sleep(2); print("watch link ->", p.url)
# mock param
s.goto("/?mock=1"); time.sleep(4); print("mock=1 banner:", p.inner_text("body")[:200].replace("\n"," | "))
s.close()
# mobile
s = Session(w=390, h=800); p = s.page
for path in ["/", "/recognitions", "/pipelines", "/sources", "/health"]:
    s.goto(path); time.sleep(4)
    sw = p.evaluate("[document.documentElement.scrollWidth, window.innerWidth]"); print("mobile", path, "scrollWidth/innerWidth", sw)
    s.shot("t11-mobile-" + (path.strip("/") or "wall"))
s.report(); s.close()
