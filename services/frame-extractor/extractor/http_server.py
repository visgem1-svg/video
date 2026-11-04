"""/health and /metrics (aiohttp)."""
from __future__ import annotations

from aiohttp import web

from .manager import CameraManager
from .metrics import Metrics


def build_app(manager: CameraManager, metrics: Metrics, worker_id: str) -> web.Application:
    async def health(_: web.Request) -> web.Response:
        cams = [w.snapshot() for w in manager.cameras.values()]
        return web.json_response({
            "status": "ok", "worker_id": worker_id, "cameras": len(cams),
            "registry_ok": manager.registry_ok, "capacity": manager.capacity,
            "by_state": {s: sum(1 for c in cams if c["state"] == s) for s in {c["state"] for c in cams}},
        })

    async def prom(_: web.Request) -> web.Response:
        return web.Response(body=metrics.render(), content_type="text/plain", charset="utf-8",
                            headers={"X-Content-Type-Options": "nosniff"})

    app = web.Application()
    app.router.add_get("/health", health)
    app.router.add_get("/metrics", prom)
    return app
