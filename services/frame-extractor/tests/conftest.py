from __future__ import annotations

import asyncio
import os
import struct
import sys
import json
from dataclasses import replace
from typing import Any

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from extractor.config import Settings  # noqa: E402


def make_jpeg(w: int = 960, h: int = 540, payload: bytes = b"\x01\x02\x03") -> bytes:
    """Minimal structurally-valid JPEG: SOI, SOF0, SOS + stuffed data, EOI."""
    sof = b"\xff\xc0" + struct.pack(">HBHHB", 11, 8, h, w, 1) + b"\x01\x11\x00"
    sos = b"\xff\xda" + struct.pack(">H", 8) + b"\x01\x01\x00\x00\x3f\x00"
    return b"\xff\xd8" + sof + sos + payload.replace(b"\xff", b"\xff\x00") + b"\xff\xd9"


def fast_settings(**kw: Any) -> Settings:
    base = Settings(worker_id="fx-test", stall_timeout_s=0.3, startup_timeout_s=0.3,
                    status_interval_s=0.2, heartbeat_interval_s=0.05)
    return replace(base, **kw)


class FakeBus:
    """Records produced messages. `capacity` = max frames accepted (None = unbounded); beyond it
    send() returns False exactly like KafkaBus on a full producer queue."""

    def __init__(self, capacity: int | None = None) -> None:
        self.messages: list[tuple[str, str, bytes, list | None]] = []
        self.capacity = capacity
        self.flushed = False

    def send(self, topic: str, key: str, value: bytes, headers: list | None = None) -> bool:
        if topic == "frames.raw" and self.capacity is not None and len(self.frames()) >= self.capacity:
            return False
        self.messages.append((topic, key, value, headers))
        return True

    def flush(self, timeout: float) -> None:
        self.flushed = True

    def close(self) -> None:
        self.flush(1)

    def frames(self) -> list[tuple[str, bytes, dict]]:
        return [(k, v, dict(h)) for t, k, v, h in self.messages if t == "frames.raw"]

    def of(self, topic: str) -> list[dict]:
        """JSON values (camera.status) or the meta header (frames.raw)."""
        if topic == "frames.raw":
            return [json.loads(h["meta"]) for _, _, h in self.frames()]
        return [json.loads(v) for t, _, v, _ in self.messages if t == topic]


class FakeSource:
    """Scripted ffmpeg: items are bytes (emitted), float (sleep s), 'hang' (block forever), 'eof'."""

    def __init__(self, script: list[Any], stderr: str = "") -> None:
        self.script = list(script)
        self.killed = False
        self._stderr = stderr
        self._pending: bytes = b""
        self.gated_frames = 0

    async def read(self, n: int) -> bytes:
        while True:
            if self.killed:
                return b""
            if not self.script:
                await asyncio.sleep(3600)
            item = self.script.pop(0)
            if isinstance(item, (bytes, bytearray)):
                return bytes(item)
            if isinstance(item, float):
                await asyncio.sleep(item)
                continue
            if item == "hang":  # like a real pipe: kill() makes the read return EOF
                while not self.killed:
                    await asyncio.sleep(0.01)
                return b""
            if item == "eof":
                return b""

    def kill(self) -> None:
        self.killed = True

    async def wait(self) -> int | None:
        return 1

    def stderr_tail(self) -> str:
        return self._stderr


async def wait_for(cond, timeout: float = 3.0) -> None:
    end = asyncio.get_running_loop().time() + timeout
    while not cond():
        if asyncio.get_running_loop().time() > end:
            raise AssertionError("condition not met in time")
        await asyncio.sleep(0.01)
