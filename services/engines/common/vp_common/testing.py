"""Test helpers shared by the service test-suites (not used at runtime)."""
from __future__ import annotations

import asyncio
import os
import sys
import time
from pathlib import Path

import httpx

ENGINES = Path(__file__).resolve().parents[2]
VP_ROOT = ENGINES.parents[1]


def setup_env(service_dir: str) -> None:
    """Call from conftest BEFORE importing the service module."""
    sys.path[:0] = [str(ENGINES / "common"), str(ENGINES / service_dir)]
    os.environ.setdefault("MODELS_DIR", str(VP_ROOT / "models"))
    os.environ.setdefault("MAX_CONCURRENCY", "1")   # makes the overload test deterministic
    os.environ.setdefault("MAX_QUEUE", "0")
    os.environ.setdefault("LOG_LEVEL", "WARNING")


def image_bytes(name: str) -> bytes:
    return (VP_ROOT / "test-images" / name).read_bytes()


def wait_ready(client, timeout: float = 180) -> None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if client.get("/health").status_code == 200:
            return
        time.sleep(0.2)
    raise TimeoutError("service did not become ready")


def burst(app, path: str, files: dict, data: dict | None = None, n: int = 8) -> list[int]:
    """Fire n simultaneous requests through the ASGI app; return status codes."""
    async def go():
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(transport=transport, base_url="http://t", timeout=120) as c:
            rs = await asyncio.gather(*[c.post(path, files=files, data=data or {}) for _ in range(n)])
            return [r.status_code for r in rs]
    return asyncio.run(go())
