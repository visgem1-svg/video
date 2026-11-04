import sys
from playwright.sync_api import sync_playwright
variants = [("localhost, default", "http://localhost:5173/", []),
            ("127.0.0.1, default", "http://127.0.0.1:5173/", []),
            ("127.0.0.1, --no-proxy-server", "http://127.0.0.1:5173/", ["--no-proxy-server"]),
            ("localhost, --no-proxy-server", "http://localhost:5173/", ["--no-proxy-server"])]
with sync_playwright() as p:
    for name, url, args in variants:
        b = p.chromium.launch(headless=True, args=args)
        pg = b.new_page()
        try:
            r = pg.goto(url, wait_until="commit", timeout=12000)
            print(f"{name:32s} -> HTTP {r.status if r else '?'}")
        except Exception as e:
            print(f"{name:32s} -> FAIL {str(e).splitlines()[0][:90]}")
        b.close()
