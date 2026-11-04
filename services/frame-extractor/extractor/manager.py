"""Control loop: heartbeat the registry and reconcile running cameras with `assigned`."""
from __future__ import annotations

import asyncio
import logging
from typing import Any, Callable, Protocol

from .config import Settings
from .metrics import Metrics
from .models import CameraSpec
from .registry_client import RegistryApi, RegistryError

log = logging.getLogger("manager")

START_STAGGER_S = 0.1  # spread ffmpeg launches (RTSP handshakes / CPU spike)
START_STAGGER_MAX_S = 3.0


class Worker(Protocol):
    spec: CameraSpec

    def start(self) -> None: ...
    def set_extract(self, mode: str, fps: float) -> None: ...
    def set_feed(self, analysis_url: str | None, motion: Any) -> None: ...
    async def stop(self) -> None: ...
    def snapshot(self) -> dict: ...


WorkerFactory = Callable[[CameraSpec, float], Worker]


class CameraManager:
    def __init__(self, settings: Settings, registry: RegistryApi, factory: WorkerFactory, metrics: Metrics) -> None:
        self._s = settings
        self._registry = registry
        self._factory = factory
        self._m = metrics
        self.cameras: dict[str, Worker] = {}
        self.registry_ok = False
        self.capacity = settings.worker_capacity  # env value, overridden by registry

    async def reconcile(self, assigned: list[dict[str, Any]]) -> None:
        desired: dict[str, CameraSpec] = {}
        for item in assigned:
            spec = CameraSpec.from_config(item) if isinstance(item, dict) else None
            if spec is None:
                log.warning("ignoring unusable/disabled camera config: %r", item)
                continue
            desired[spec.camera_id] = spec
        if len(desired) > self.capacity:
            log.warning("assigned %d cameras > capacity %d", len(desired), self.capacity)

        to_stop = [cid for cid, w in self.cameras.items() if cid not in desired or w.spec != desired[cid]]
        if to_stop:
            await asyncio.gather(*(self.cameras.pop(cid).stop() for cid in to_stop), return_exceptions=True)
            log.info("stopped cameras: %s", to_stop)
        for cid, spec in desired.items():  # (mode, fps) is not part of spec equality: coalesced in-place restart
            w = self.cameras.get(cid)
            if w is None or (w.spec.extract == spec.extract and w.spec.feed == spec.feed):
                continue
            if w.spec.feed != spec.feed:  # contract 9.9; recorded first so ONE coalesced restart applies both
                w.set_feed(spec.analysis_url, spec.motion)
            w.set_extract(spec.mode, spec.target_fps)
        new = [spec for cid, spec in desired.items() if cid not in self.cameras]
        for i, spec in enumerate(new):
            w = self._factory(spec, min(i * START_STAGGER_S, START_STAGGER_MAX_S))
            self.cameras[spec.camera_id] = w
            w.start()
        if new:
            log.info("started cameras: %s", [s.camera_id for s in new])
        self._m.cameras_running.set(len(self.cameras))

    async def heartbeat_once(self) -> bool:
        try:
            resp = await self._registry.heartbeat(self._s.worker_capacity, sorted(self.cameras))
        except RegistryError as e:
            if self.registry_ok:
                log.warning("registry unreachable, keeping %d cameras running: %s", len(self.cameras), e)
            self.registry_ok = False
            return False
        if not self.registry_ok:
            log.info("registry heartbeat ok")
        self.registry_ok = True
        self.capacity = resp.capacity or self._s.worker_capacity
        await self.reconcile(resp.assigned)
        return True

    async def run(self, stop: asyncio.Event) -> None:
        while not stop.is_set():
            try:
                await self.heartbeat_once()
            except Exception:  # noqa: BLE001 - the control loop must never die
                log.exception("control loop iteration failed")
            try:
                await asyncio.wait_for(stop.wait(), timeout=self._s.heartbeat_interval_s)
            except asyncio.TimeoutError:
                pass

    async def stop_all(self) -> None:
        workers = list(self.cameras.values())
        self.cameras.clear()
        await asyncio.gather(*(w.stop() for w in workers), return_exceptions=True)
        self._m.cameras_running.set(0)
