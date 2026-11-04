from playwright.sync_api import sync_playwright
with sync_playwright() as p:
    for chan in (None, "chrome"):
        b = p.chromium.launch(channel=chan, headless=True, args=["--use-fake-ui-for-media-stream", "--autoplay-policy=no-user-gesture-required"]) if chan else p.chromium.launch(headless=True, args=["--use-fake-ui-for-media-stream", "--autoplay-policy=no-user-gesture-required"])
        pg = b.new_page(viewport={"width": 1440, "height": 900}); errs = []
        pg.on("console", lambda m: errs.append(m.text) if m.type == "error" else None)
        try:
            pg.goto("http://localhost:5173/live", wait_until="domcontentloaded", timeout=20000); pg.wait_for_timeout(10000)
            vids = pg.evaluate("() => [...document.querySelectorAll('video')].map(v => [v.readyState, v.videoWidth, +v.currentTime.toFixed(1)])")
            banner = pg.locator("text=/mock/i").count()
            print(f"[{chan or 'bundled chromium'}] {b.version}: videos(readyState,width,time)={vids[:4]} mock-banner={banner} console-errors={len(errs)} {errs[:2]}")
            pg.screenshot(path=f"screenshots/probe-live-{chan or 'chromium'}.png")
        except Exception as e:
            print(f"[{chan or 'bundled chromium'}] FAIL {str(e).splitlines()[0][:100]}")
        b.close()
