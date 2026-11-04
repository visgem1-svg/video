"""Bounded, non-blocking inference runtime + FastAPI app factory.

* inference runs in a dedicated ThreadPoolExecutor (MAX_CONCURRENCY threads)
* at most MAX_CONCURRENCY + MAX_QUEUE requests are admitted; more -> 429
* not ready (models loading / warming) -> 503; /health is 200 only when ready
"""
from __future__ import annotations

import asyncio
import logging
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
from typing import Any, Callable, Iterable, TypeVar

import cv2
from fastapi import FastAPI, HTTPException, Request, UploadFile
from fastapi.responses import JSONResponse, Response
from prometheus_client import (CONTENT_TYPE_LATEST, CollectorRegistry, Counter, Gauge, Histogram,
                               ProcessCollector, generate_latest)

from .imaging import ImageError, decode_image
from .logging_setup import setup_logging
from .settings import Settings

T = TypeVar("T")
log = logging.getLogger("vp.engine")

_BUCKETS = (0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30)


class Runtime:
    def __init__(self, settings: Settings, tools: Iterable[str]):
        self.settings = settings
        self.tools = list(tools)
        self.ready = False
        self.load_error: str | None = None
        self.executor = ThreadPoolExecutor(max_workers=settings.max_concurrency,
                                           thread_name_prefix=settings.service)
        self._admitted = 0  # only touched from the event-loop thread
        self.registry = CollectorRegistry()
        ProcessCollector(registry=self.registry)
        self.requests = Counter("vp_engine_requests_total", "Engine requests",
                                ["tool", "status"], registry=self.registry)
        self.seconds = Histogram("vp_engine_inference_seconds", "Inference time (excl. queueing)",
                                 ["tool"], buckets=_BUCKETS, registry=self.registry)
        self.inflight = Gauge("vp_engine_inflight", "Admitted requests (running + queued)",
                              ["tool"], registry=self.registry)
        for t in self.tools:
            self.inflight.labels(t).set(0)
            self.seconds.labels(t)

    async def run(self, tool: str, data: bytes | None, fn: Callable[..., T]) -> tuple[T, float]:
        """Decode `data` (if given) and call fn(img) (or fn()) in the pool. Returns (result, ms)."""
        if not self.ready:
            self.requests.labels(tool, "unavailable").inc()
            raise HTTPException(503, "engine not ready", headers={"Retry-After": "2"})
        if self._admitted >= self.settings.max_concurrency + self.settings.max_queue:
            self.requests.labels(tool, "overloaded").inc()
            raise HTTPException(429, "engine overloaded", headers={"Retry-After": "1"})
        self._admitted += 1
        self.inflight.labels(tool).inc()
        s = self.settings

        def work():
            img = decode_image(data, s.max_pixels) if data is not None else None
            t0 = time.perf_counter()
            res = fn(img) if data is not None else fn()
            return res, time.perf_counter() - t0

        status = "ok"
        try:
            res, secs = await asyncio.get_running_loop().run_in_executor(self.executor, work)
            self.seconds.labels(tool).observe(secs)
            return res, round(secs * 1000, 1)
        except ImageError as e:
            status = "bad_request"
            raise HTTPException(e.status, e.detail)
        except HTTPException:
            status = "bad_request"
            raise
        except Exception:
            status = "error"
            log.exception("inference failed", extra={"tool": tool})
            raise HTTPException(500, "inference failed")
        finally:
            self._admitted -= 1
            self.inflight.labels(tool).dec()
            self.requests.labels(tool, status).inc()


async def read_upload(upload: UploadFile, settings: Settings) -> bytes:
    data = await upload.read(settings.max_image_bytes + 1)
    if len(data) > settings.max_image_bytes:
        raise HTTPException(413, f"image exceeds {settings.max_image_bytes} bytes")
    return data


def create_app(service: str, tools: list[str], load: Callable[[Settings], Any],
               warmup: Callable[[Any], None] | None = None, description: str = "") -> FastAPI:
    """`load(settings)` builds the models object (app.state.models); `warmup(models)` primes it."""
    settings = Settings.from_env(service)
    setup_logging(service, settings.log_level)
    cv2.setNumThreads(settings.ort_threads)
    rt = Runtime(settings, tools)

    def _boot(app: FastAPI):
        try:
            t0 = time.perf_counter()
            app.state.models = load(settings)
            t1 = time.perf_counter()
            if warmup:
                warmup(app.state.models)
            rt.ready = True
            log.info("ready", extra={"load_s": round(t1 - t0, 2),
                                     "warmup_s": round(time.perf_counter() - t1, 2)})
        except Exception as e:  # stay unready; the orchestrator restarts us
            rt.load_error = repr(e)
            log.exception("startup failed")

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        log.info("starting", extra={"max_concurrency": settings.max_concurrency,
                                    "max_queue": settings.max_queue, "ort_threads": settings.ort_threads})
        threading.Thread(target=_boot, args=(app,), daemon=True, name="boot").start()
        yield
        rt.ready = False
        rt.executor.shutdown(wait=True, cancel_futures=True)
        log.info("stopped")

    app = FastAPI(title=f"vp-{service}", description=description, lifespan=lifespan)
    app.state.runtime = rt
    app.state.settings = settings

    @app.middleware("http")
    async def access_log(request: Request, call_next):
        t0 = time.perf_counter()
        resp = await call_next(request)
        noisy = request.url.path in ("/health", "/metrics")
        log.log(logging.DEBUG if noisy else logging.INFO, "request", extra={
            "method": request.method, "path": request.url.path, "status": resp.status_code,
            "ms": round((time.perf_counter() - t0) * 1000, 1)})
        return resp

    @app.get("/health")
    def health():
        if rt.ready:
            return {"status": "ok", "service": service, "tools": tools}
        return JSONResponse({"status": "error" if rt.load_error else "loading",
                             "error": rt.load_error}, status_code=503)

    @app.get("/metrics")
    def metrics():
        return Response(generate_latest(rt.registry), media_type=CONTENT_TYPE_LATEST)

    return app


def ort_session_options(threads: int):
    import onnxruntime as ort
    so = ort.SessionOptions()
    so.intra_op_num_threads = threads
    so.inter_op_num_threads = 1
    so.log_severity_level = 3
    return so
