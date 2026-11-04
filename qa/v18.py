from pathlib import Path
from playwright.sync_api import sync_playwright
B="http://localhost:5173"; S=Path(__file__).resolve().parent/"screenshots"
errs=[]
with sync_playwright() as p:
    br=p.chromium.launch(headless=True); pg=br.new_page(viewport={"width":1440,"height":900})
    pg.on("console",lambda m: errs.append(m.text) if m.type=="error" else None)
    pg.on("pageerror",lambda e: errs.append(f"pageerror {e}"))
    def shot(path,name,wait=4000):
        pg.goto(B+path,wait_until="domcontentloaded"); pg.wait_for_timeout(wait); pg.screenshot(path=str(S/f"v18-{name}.png"))
    shot("/frames?camera=cam-025","frames-idle",6000)
    shot("/frames?mode=grid","frames-grid",6000)
    shot("/health","health")
    shot("/operations","ops",6000)
    shot("/live/cam-025","focus-cam025",6000)
    br.close()
print("ERRORS:",errs)
