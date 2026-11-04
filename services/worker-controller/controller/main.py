import asyncio
import contextlib
import json
import logging
import sys
import time

from fastapi import FastAPI, HTTPException, Request, Response
from prometheus_client import CONTENT_TYPE_LATEST, generate_latest

from .config import Settings
from .core import Controller


class JsonFormatter(logging.Formatter):
    def format(self, r):
        return json.dumps({"ts": int(time.time() * 1000), "level": r.levelname, "logger": r.name, "msg": r.getMessage()})


def setup_logging(level):
    h = logging.StreamHandler(sys.stdout)
    h.setFormatter(JsonFormatter())
    root = logging.getLogger()
    root.handlers[:] = [h]
    root.setLevel(level)


def create_app(controller: Controller | None = None, run_loop: bool = True) -> FastAPI:
    owned = controller is None
    holder = {"c": controller}

    @contextlib.asynccontextmanager
    async def lifespan(app):
        task = None
        if holder["c"] is None:
            from .registry import RegistryClient
            from .runtime import DockerRuntime
            s = Settings()
            setup_logging(s.log_level)
            holder["c"] = Controller(s, RegistryClient(s.registry_url), DockerRuntime(s))
        if run_loop:
            task = asyncio.create_task(holder["c"].run())
        yield
        if task:
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await task
        if owned:
            await holder["c"].registry.close()

    app = FastAPI(title="worker-controller", lifespan=lifespan)

    def ctl() -> Controller:
        return holder["c"]

    @app.get("/health")
    async def health():
        return {"status": "ok", "ticks": ctl().ticks}

    @app.get("/metrics")
    async def metrics():
        return Response(generate_latest(ctl().m), media_type=CONTENT_TYPE_LATEST)

    @app.get("/api/autoscaler")
    async def get_state():
        return ctl().snapshot()

    async def body(request: Request):
        try:
            return await request.json()
        except Exception:
            raise HTTPException(400, "invalid JSON body")

    @app.put("/api/autoscaler/policy")
    async def put_policy(request: Request):
        try:
            return ctl().set_policy(await body(request))
        except ValueError as e:
            raise HTTPException(422, e.args[0])

    @app.put("/api/autoscaler/mode")
    async def put_mode(request: Request):
        b = await body(request)
        try:
            ctl().set_mode(b.get("mode") if isinstance(b, dict) else None)
        except ValueError as e:
            raise HTTPException(422, e.args[0])
        return {"mode": ctl().mode}

    @app.post("/api/autoscaler/scale")
    async def post_scale(request: Request):
        b = await body(request)
        try:
            ctl().scale(b.get("workers") if isinstance(b, dict) else None)
        except PermissionError as e:
            raise HTTPException(409, str(e))
        except ValueError as e:
            raise HTTPException(422, e.args[0])
        return {"mode": ctl().mode, "target": ctl().manual_target}

    return app


def app_factory():
    return create_app()


if __name__ == "__main__":
    import uvicorn
    from .config import env
    uvicorn.run("controller.main:app_factory", factory=True, host="0.0.0.0", port=int(env("PORT", "5007")),
                log_config=None, timeout_graceful_shutdown=5)
