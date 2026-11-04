import sys, subprocess, time
from playwright.sync_api import sync_playwright
out = sys.argv[1]
pages = {"live": "/", "focus": "/live/cam-001#pipeline", "recog": "/recognitions", "people": "/people",
         "pipelines": "/pipelines", "sources": "/sources", "health": "/health"}
with sync_playwright() as p:
    b = p.chromium.launch()
    for theme in ("dark", "light"):
        ctx = b.new_context(viewport={"width": 1440, "height": 900})
        pg = ctx.new_page()
        pg.add_init_script(f"localStorage.setItem('vp-theme','{theme}')")
        errs = []
        pg.on("pageerror", lambda e: errs.append(str(e)))
        for name, path in pages.items():
            if theme == "light" and name not in ("live", "health"): continue
            sep = "&" if "?" in path else "?"
            url = "http://localhost:4173" + (path.split("#")[0] + "?mock=1" + ("#" + path.split("#")[1] if "#" in path else ""))
            pg.goto(url, wait_until="domcontentloaded")
            pg.wait_for_timeout(4500 if name in ("live", "focus") else 2500)
            pg.screenshot(path=f"{out}/{name}-{theme}.png")
        print(theme, "errors:", errs)
    b.close()
