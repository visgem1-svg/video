import json, time, os
from playwright.sync_api import sync_playwright
BASE = "http://localhost:5173"
API = "http://localhost:8000"
SHOTS = os.path.join(os.path.dirname(__file__), "..", "screenshots")

class Session:
    """One headless chromium + page with DevTools-style logging."""
    def __init__(self, url_suffix="", w=1600, h=900):
        self.pw = sync_playwright().start()
        self.browser = self.pw.chromium.launch(headless=True, args=["--autoplay-policy=no-user-gesture-required"])
        self.ctx = self.browser.new_context(viewport={"width": w, "height": h})
        self.page = self.ctx.new_page()
        self.console, self.failed, self.badresp, self.ws = [], [], [], []
        p = self.page
        p.on("console", lambda m: self.console.append((m.type, m.text[:300])) if m.type in ("error", "warning") else None)
        p.on("pageerror", lambda e: self.console.append(("pageerror", str(e)[:300])))
        p.on("requestfailed", lambda r: self.failed.append((r.method, r.url[:200], r.failure)))
        p.on("response", lambda r: self.badresp.append((r.status, r.request.method, r.url[:200])) if r.status >= 400 else None)
        p.on("websocket", lambda w: self.ws.append(w.url) or w.on("close", lambda *_: self.ws.append("CLOSED " + w.url)))
        self.cdp = self.ctx.new_cdp_session(p)
        self.cdp.send("Performance.enable")
    def goto(self, path="/"):
        self.page.goto(BASE + path, wait_until="domcontentloaded")
    def shot(self, name):
        self.page.screenshot(path=os.path.join(SHOTS, name + ".png"))
    def metrics(self):
        return {m["name"]: m["value"] for m in self.cdp.send("Performance.getMetrics")["metrics"]}
    def close(self):
        self.browser.close(); self.pw.stop()
    def report(self):
        import collections; print("CONSOLE", collections.Counter(self.console).most_common(8))
        print("FAILED", json.dumps(self.failed[:30], default=str))
        print("HTTP>=400", json.dumps(self.badresp[:6]), len(self.badresp))
        print("WS", self.ws)

VIDEO_JS = """() => [...document.querySelectorAll('video')].map(v => ({rs: v.readyState, w: v.videoWidth, h: v.videoHeight, t: v.currentTime, paused: v.paused, hasSrc: !!v.srcObject}))"""
