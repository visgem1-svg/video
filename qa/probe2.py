from playwright.sync_api import sync_playwright
targets = [("data: URL (no network)", "data:text/html,<h1>ok</h1>"),
           ("host python server :5999", "http://127.0.0.1:5999/"),
           ("docker grafana :3000", "http://127.0.0.1:3000/login"),
           ("docker gateway :8000", "http://127.0.0.1:8000/health"),
           ("internet example.com", "https://example.com/")]
with sync_playwright() as p:
    for chan in ("chromium-headless-shell", "chromium (new headless)"):
        kw = {} if chan.startswith("chromium-headless") else {"args": ["--headless=new"], "headless": False}
        try:
            b = p.chromium.launch(headless=True) if not kw else p.chromium.launch(**kw)
        except Exception as e:
            print(chan, "launch FAIL", str(e)[:120]); continue
        pg = b.new_page()
        for name, url in targets:
            try:
                r = pg.goto(url, wait_until="commit", timeout=8000); print(f"[{chan}] {name:26s} -> OK {r.status if r else ''}")
            except Exception as e:
                print(f"[{chan}] {name:26s} -> FAIL {str(e).splitlines()[0][:70]}")
        b.close()
