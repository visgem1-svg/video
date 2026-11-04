"""Entry point: python -m extractor.main"""
from __future__ import annotations

import asyncio
import logging
import signal
import sys

from aiohttp import web

from .camera_worker import CameraWorker
from .config import Settings
from .ffmpeg_reader import FfmpegSource
from .http_server import build_app
from .logging_setup import setup_logging
from .manager import CameraManager
from .metrics import Metrics
from .models import CameraSpec
from .procguard import kill_children_on_exit
from .publisher import FramePublisher, KafkaBus
from .registry_client import RegistryClient

log = logging.getLogger("main")


def _install_signals(loop: asyncio.AbstractEventLoop, stop: asyncio.Event) -> None:
    def trigger(*_: object) -> None:
        loop.call_soon_threadsafe(stop.set)

    for name in ("SIGTERM", "SIGINT", "SIGBREAK"):
        sig = getattr(signal, name, None)
        if sig is None:
            continue
        try:
            loop.add_signal_handler(sig, stop.set)
        except (NotImplementedError, RuntimeError):  # Windows proactor loop
            signal.signal(sig, trigger)


async def amain(s: Settings) -> None:
    loop = asyncio.get_running_loop()
    stop = asyncio.Event()
    _install_signals(loop, stop)

    metrics = Metrics()
    publisher = FramePublisher(s, KafkaBus(s, metrics), metrics)
    registry = RegistryClient(s.registry_url, s.worker_id)

    def make_worker(spec: CameraSpec, delay: float) -> CameraWorker:
        return CameraWorker(spec, s, lambda sp: FfmpegSource.start(s, sp), publisher, metrics, initial_delay=delay)

    manager = CameraManager(s, registry, make_worker, metrics)
    runner = web.AppRunner(build_app(manager, metrics, s.worker_id), access_log=None)
    await runner.setup()
    await web.TCPSite(runner, "0.0.0.0", s.port).start()
    log.info("started worker=%s capacity=%d port=%d", s.worker_id, s.worker_capacity, s.port)

    control = asyncio.create_task(manager.run(stop))
    await stop.wait()
    log.info("shutting down")
    await control
    await manager.stop_all()  # kills ffmpeg children, publishes final "offline" status
    await loop.run_in_executor(None, publisher.close)  # flush kafka
    await registry.close()
    await runner.cleanup()
    log.info("bye")


def main() -> None:
    s = Settings.from_env()
    setup_logging(s.log_level)
    kill_children_on_exit()
    try:
        asyncio.run(amain(s))
    except KeyboardInterrupt:
        pass
    sys.exit(0)


if __name__ == "__main__":
    main()
