import json, collections, time
from playwright.sync_api import sync_playwright
msgs = collections.Counter(); det_cams = collections.Counter(); sent = []
def on_ws(ws):
    def rx(payload):
        if isinstance(payload, bytes): payload = payload.decode('utf-8', 'ignore')
        for part in payload.split('\x1e'):
            if not part.strip(): continue
            try: m = json.loads(part)
            except Exception: continue
            if m.get('type') == 1:
                msgs[m.get('target')] += 1
                if m.get('target') == 'detections':
                    a = m['arguments'][0]; det_cams[a.get('camera_id')] += 1
    def tx(payload):
        if isinstance(payload, bytes): payload = payload.decode('utf-8', 'ignore')
        if 'Subscribe' in payload: sent.append(payload[:160])
    ws.on('framereceived', rx); ws.on('framesent', tx)
with sync_playwright() as p:
    b = p.chromium.launch(headless=True, args=["--autoplay-policy=no-user-gesture-required"])
    pg = b.new_page(viewport={"width": 1440, "height": 900}); pg.on('websocket', on_ws)
    pg.goto("http://localhost:5173/live", wait_until="domcontentloaded"); pg.wait_for_timeout(3000)
    painted = 0; samples = 0
    for _ in range(20):
        r = pg.evaluate("""() => [...document.querySelectorAll('canvas')].map(c => { try { const d = c.getContext('2d').getImageData(0,0,c.width,c.height).data; let n=0; for (let i=3;i<d.length;i+=16) if (d[i]) n++; return [c.width,c.height,n]; } catch(e) { return [c.width,c.height,-1] } })""")
        samples += 1; painted += sum(1 for w,h,n in r if n > 0)
        if _ == 0: print("canvases (w,h,painted-px):", r[:9])
        pg.wait_for_timeout(500)
    print("hub messages in ~13 s:", dict(msgs)); print("detections per camera:", dict(det_cams))
    print("subscribe calls:", sent[:3]); print(f"tiles with painted overlay: {painted} over {samples} samples")
    b.close()
