from pathlib import Path
from playwright.sync_api import sync_playwright
B="http://localhost:5173"; errs=[]
with sync_playwright() as p:
    br=p.chromium.launch(headless=True); pg=br.new_page(viewport={"width":1440,"height":900})
    pg.on("console",lambda m: errs.append(m.text) if m.type=="error" else None)
    pg.on("pageerror",lambda e: errs.append(f"pageerror {e}"))
    for path in ["/sources","/health","/operations","/live/cam-002","/sources?edit=cam-002"]:
        pg.goto(B+path,wait_until="domcontentloaded"); pg.wait_for_timeout(4500)
    pg.screenshot(path=str(Path("screenshots")/"v19-clean-edit.png"))
    br.close()
print("CLEAN ERRORS:",errs)
