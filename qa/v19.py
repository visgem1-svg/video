import json, re
from pathlib import Path
from playwright.sync_api import sync_playwright

B = "http://localhost:5173"
S = Path(__file__).resolve().parent / "screenshots"
errs = []
OVR = {
    "cam-001": dict(state="offline", fps_actual=0, last_error="rtsp 401 Unauthorized", error_class="auth_failed", error_hint="Camera rejected the credentials. Edit the source and fix user/password."),
    "cam-002": dict(state="offline", fps_actual=0, error_class="unreachable"),
    "cam-003": dict(motion_skipped_pct=87.4),
    "cam-004": dict(state="degraded", error_class="stalled", error_hint="No frames for 12 s"),
    "cam-005": dict(motion_skipped_pct=34),
}


def patch_health(route):
    r = route.fetch()
    data = r.json()
    for h in data:
        h.update(OVR.get(h["camera_id"], {}))
        h["ts"] = h["ts"] + 3_600_000
    route.fulfill(response=r, body=json.dumps(data))


def patch_cams(route):
    if route.request.method != "GET":
        return route.continue_()
    r = route.fetch()
    data = r.json()
    for c in data:
        if c["camera_id"] in ("cam-003", "cam-005"):
            c["motion"] = {"enabled": True, "sensitivity": "low", "heartbeat_s": 10}
            c["analysis_stream_url"] = "rtsp://u:p@10.0.0.5/sub" if c["camera_id"] == "cam-003" else None
    route.fulfill(response=r, body=json.dumps(data))


with sync_playwright() as p:
    br = p.chromium.launch(headless=True)
    pg = br.new_page(viewport={"width": 1440, "height": 900})
    pg.on("console", lambda m: errs.append(m.text) if m.type == "error" else None)
    pg.on("pageerror", lambda e: errs.append(f"pageerror {e}"))
    pg.route(re.compile(r".*/hubs/live.*"), lambda r: r.abort())  # no live push: REST seed shows injected fields
    pg.route(re.compile(r".*/api/health/cameras$"), patch_health)
    pg.route(re.compile(r".*/api/cameras$"), patch_cams)

    def go(path, wait=4500):
        pg.goto(B + path, wait_until="domcontentloaded")
        pg.wait_for_timeout(wait)

    # Sources: add dialog, fill, server 400 mapping, cancel (no camera created)
    posts = []
    def fake400(route):
        posts.append(route.request.method)
        route.fulfill(status=400, content_type="application/json",
                      body=json.dumps({"detail": [{"loc": ["body", "motion", "heartbeat_s"], "msg": "heartbeat_s must be between 1 and 60"}]}))
    pg.route(re.compile(r".*/api/cameras$"), lambda r: fake400(r) if r.request.method == "POST" else patch_cams(r))
    go("/sources")
    pg.screenshot(path=str(S / "v19-sources.png"))
    pg.get_by_role("button", name="Add camera").click()
    pg.wait_for_timeout(500)
    pg.get_by_label("Enable motion detection").click()
    pg.get_by_role("tab", name="High").click()
    pg.screenshot(path=str(S / "v19-form-add.png"))
    # client validation
    pg.get_by_label("Camera id").fill("qa-v19-test")
    pg.get_by_label("Name").fill("QA v19")
    pg.get_by_label("Stream URL (camera)").fill("rtsp://10.0.0.9/main")
    pg.get_by_label("Analysis stream (substream) URL (optional)").fill("http://bad")
    pg.get_by_label("Heartbeat (seconds)").fill("99")
    pg.get_by_role("button", name="Add camera").last.click()
    pg.wait_for_timeout(400)
    pg.screenshot(path=str(S / "v19-form-errors.png"))
    assert pg.get_by_text("Between 1 and 60 seconds").is_visible()
    # fix client errors, server (faked) 400 maps onto heartbeat
    pg.get_by_label("Analysis stream (substream) URL (optional)").fill("rtsp://10.0.0.9/sub")
    pg.get_by_label("Heartbeat (seconds)").fill("5")
    pg.get_by_role("button", name="Add camera").last.click()
    pg.wait_for_timeout(800)
    pg.screenshot(path=str(S / "v19-form-server400.png"))
    assert posts == ["POST"], posts
    assert pg.get_by_text("heartbeat_s must be between 1 and 60").first.is_visible()
    pg.get_by_role("button", name="Cancel").click()
    pg.wait_for_timeout(400)

    # edit deep link
    go("/sources?edit=cam-003")
    pg.screenshot(path=str(S / "v19-form-edit.png"))
    pg.get_by_role("button", name="Cancel").click()

    go("/health")
    pg.screenshot(path=str(S / "v19-health.png"))
    go("/operations", 5500)
    pg.screenshot(path=str(S / "v19-ops.png"))
    for cam in ("cam-001", "cam-003"):
        pg.goto(B + "/operations", wait_until="domcontentloaded")
        pg.wait_for_timeout(5000)
        pg.get_by_role("button", name=re.compile(f"Camera Camera 00{cam[-1]},")).first.click()
        pg.wait_for_timeout(1500)
        pg.screenshot(path=str(S / f"v19-ops-drawer-{cam}.png"))
    go("/live/cam-001", 5000)
    pg.screenshot(path=str(S / "v19-focus-cam001.png"))
    go("/live/cam-003", 5000)
    pg.screenshot(path=str(S / "v19-focus-cam003.png"))
    br.close()
print("ERRORS:", errs)
