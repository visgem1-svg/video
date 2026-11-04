from playwright.sync_api import sync_playwright
with sync_playwright() as p:
    for headless in (True,):
        b = p.chromium.launch(channel="chrome", headless=headless, args=["--use-fake-ui-for-media-stream", "--autoplay-policy=no-user-gesture-required"])
        pg = b.new_page(); errs = []; pg.on("console", lambda m: errs.append(m.text) if m.type == "error" else None)
        for url in ("http://localhost:5173/", "http://localhost:8000/health", "http://localhost:3000/login"):
            try: r = pg.goto(url, wait_until="commit", timeout=15000); print(f"chrome headless={headless} {url:34s} -> OK {r.status}")
            except Exception as e: print(f"chrome headless={headless} {url:34s} -> FAIL {str(e).splitlines()[0][:70]}")
        pg.goto("http://localhost:5173/live", wait_until="domcontentloaded", timeout=20000); pg.wait_for_timeout(8000)
        vids = pg.evaluate("() => [...document.querySelectorAll('video')].map(v => ({rs: v.readyState, w: v.videoWidth, t: +v.currentTime.toFixed(1)}))")
        print("version:", b.version, "| title:", pg.title(), "| videos:", vids[:4], "| console errors:", len(errs), errs[:2])
        pg.screenshot(path="screenshots/probe-live.png"); b.close()
