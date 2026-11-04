"""Bug 1/4: detection boxes drawn on canvas + latency (hub message ts vs browser receive time).
Hooks window.WebSocket to record every SignalR 'detections' message with Date.now() on arrival."""
import sys, time, json, statistics
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
HOOK = """
(() => {
  window.__det = []; window.__other = {};
  const W = window.WebSocket;
  window.WebSocket = function(u, p) {
    const ws = p ? new W(u, p) : new W(u);
    ws.addEventListener('message', ev => {
      const now = Date.now();
      if (typeof ev.data !== 'string') return;
      for (const part of ev.data.split('\x1e')) {
        if (!part) continue;
        try { const m = JSON.parse(part);
          if (m.type === 1) {
            if (m.target === 'detections') { const d = m.arguments[0];
              window.__det.push({cam: d.camera_id, ts: d.ts, recv: now, partial: d.partial, nobj: (d.objects||[]).length, nplate:(d.plates||[]).length, nface:(d.faces||[]).length, w:d.width, h:d.height}); }
            else window.__other[m.target] = (window.__other[m.target]||0)+1;
          } } catch(e) {}
      }
    });
    return ws;
  };
  window.WebSocket.prototype = W.prototype;
  Object.assign(window.WebSocket, {CONNECTING:0,OPEN:1,CLOSING:2,CLOSED:3});
  // sample canvas for drawn pixels
  window.__boxFrames = 0; window.__samples = 0;
  setInterval(() => {
    document.querySelectorAll('canvas').forEach(c => {
      try { const d = c.getContext('2d').getImageData(0,0,c.width,c.height).data;
        window.__samples++; for (let i=3;i<d.length;i+=4*7) if (d[i]>0) { window.__boxFrames++; break; } } catch(e){}
    });
  }, 250);
})();
"""
layout = sys.argv[1] if len(sys.argv) > 1 else "/"
dur = int(sys.argv[2]) if len(sys.argv) > 2 else 40
s = Session()
s.page.add_init_script(HOOK)
s.goto(layout)
time.sleep(dur)
s.shot("t02-live-boxes")
det = s.page.evaluate("window.__det"); other = s.page.evaluate("window.__other")
print("hub msgs other:", other)
print("detections msgs:", len(det), "boxframes/samples:", s.page.evaluate("[window.__boxFrames, window.__samples]"))
lat = [d["recv"] - d["ts"] for d in det]
if lat:
    lat.sort()
    print("latency ms  min %d  median %d  p90 %d  max %d" % (lat[0], statistics.median(lat), lat[int(len(lat)*.9)], lat[-1]))
    cams = {}
    for d in det: cams.setdefault(d["cam"], []).append(d)
    for c, l in sorted(cams.items()):
        ls = sorted(x["recv"]-x["ts"] for x in l)
        print(c, "n=%d rate=%.1f/s median_lat=%d" % (len(l), len(l)/dur, statistics.median(ls)), "partials:", sum(1 for x in l if x["partial"]), "sample", {k: l[0][k] for k in ("nobj","nplate","nface","w","h")})
s.report(); s.close()
