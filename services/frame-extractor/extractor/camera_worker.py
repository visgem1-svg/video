"""One camera: ffmpeg lifecycle, watchdog, state machine, status."""
from __future__ import annotations

import asyncio
import contextlib
import logging
import time
import uuid
from dataclasses import replace
from typing import Callable

from . import errors
from .config import Settings
from .ffmpeg_reader import FrameSource, SourceFactory
from .jpeg_parser import JpegStreamParser, jpeg_dimensions
from .metrics import Metrics
from .models import CameraSpec, Frame, MotionConfig, status_message
from .publisher import FramePublisher
from .state import Backoff, CamState, FpsMeter, SkipMeter, StateMachine

log = logging.getLogger("camera")

ERROR_AFTER_FAILURES = 5  # consecutive sessions without a single frame -> state "error"
STABLE_AFTER_S = 30.0  # online this long -> backoff resets
READ_CHUNK = 64 * 1024
GATE_STALL_MARGIN_S = 3.0  # a gated static scene legitimately emits one frame per heartbeat_s
TERMINAL_BACKOFF = (30.0, 300.0)  # contract 9.9: terminal error classes retry 30 s -> 5 min


class CameraWorker:
    def __init__(
        self,
        spec: CameraSpec,
        settings: Settings,
        source_factory: SourceFactory,
        publisher: FramePublisher,
        metrics: Metrics,
        backoff: Backoff | None = None,
        initial_delay: float = 0.0,
        terminal_backoff: Backoff | None = None,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self.spec = spec
        self._s = settings
        self._factory = source_factory
        self._pub = publisher
        self._m = metrics
        self._backoff = backoff or Backoff()
        self._term_backoff = terminal_backoff or Backoff(*TERMINAL_BACKOFF)
        self._wake = asyncio.Event()  # cuts a (long) retry sleep short when the camera config changes
        self._initial_delay = initial_delay
        self._clock = clock
        self._fps = FpsMeter(10.0, clock)
        self.sm = StateMachine(self._on_state_change)
        self.reconnects = 0
        self.error: str | None = None
        self.error_class: str | None = None
        self.last_frame_ts: int | None = None
        self._seq = 0
        self._failures = 0
        self._online_since: float | None = None
        self._src: FrameSource | None = None
        self._tasks: list[asyncio.Task] = []
        self._stopping = False
        # contract 9.8: wanted extraction (mode, fps) vs the one the running ffmpeg was started with
        self._want: tuple[str, float] = spec.extract
        self._want_feed = spec.feed  # contract 9.9: (read url, motion)
        self._skip = SkipMeter(60.0, clock)
        self._gate_seen = 0  # src.gated_frames already added to the counter
        self._gate_total = 0
        self._last_switch: float | None = None
        self._switching = False
        self._deferred: asyncio.Task | None = None

    # ---- lifecycle -------------------------------------------------------
    @property
    def camera_id(self) -> str:
        return self.spec.camera_id

    @property
    def state(self) -> CamState:
        return self.sm.state

    def start(self) -> None:
        loop = asyncio.get_running_loop()
        self._m.set_state(self.camera_id, self.sm.state)
        self._m.set_mode(self.camera_id, self.spec.mode)
        self._publish_status()
        self._tasks = [
            loop.create_task(self._run(), name=f"run-{self.camera_id}"),
            loop.create_task(self._status_loop(), name=f"sts-{self.camera_id}"),
        ]

    async def stop(self) -> None:
        self._stopping = True
        if self._deferred is not None:
            self._deferred.cancel()
        for t in self._tasks:
            t.cancel()
        await asyncio.gather(*self._tasks, return_exceptions=True)
        self._kill_source()
        self.sm.to(CamState.OFFLINE)  # publishes final status
        self._m.forget(self.camera_id)

    def snapshot(self) -> dict:
        return {
            "camera_id": self.camera_id, "state": self.state.value,
            "fps_actual": round(self._fps.fps(), 2), "reconnects": self.reconnects,
            "last_frame_ts": self.last_frame_ts, "error": self.error,
            "mode": self.spec.mode, "fps_target": self.spec.target_fps,
            "motion_skipped_pct": self.motion_skipped_pct, "error_class": self.error_class,
        }

    @property
    def motion_skipped_pct(self) -> float | None:
        """Rolling 60 s share of frames the gate dropped; None when the gate is not in this ffmpeg's chain."""
        return self._skip.pct() if self.spec.gated else None

    # ---- extraction mode (contract 9.8) ---------------------------------
    def set_extract(self, mode: str, fps: float) -> None:
        """Latest wins; applied (ffmpeg restarted) at most once per mode_switch_min_interval_s."""
        self._want = (mode, fps)
        self._try_switch()

    def set_feed(self, analysis_url: str | None, motion: MotionConfig | None) -> None:
        """Record the wanted analysis URL / motion config. Applied together with set_extract (one coalesced restart)."""
        self._want_feed = (analysis_url or self.spec.source_url, motion)

    def _try_switch(self) -> None:
        if self._want == self.spec.extract and self._want_feed == self.spec.feed:
            return
        if self._deferred is not None:
            return  # a timer is already pending; it applies whatever is wanted then
        wait = 0.0
        if self._last_switch is not None:
            wait = self._last_switch + self._s.mode_switch_min_interval_s - self._clock()
        if wait > 0:
            self._deferred = asyncio.get_running_loop().create_task(self._apply_later(wait))
            return
        self._apply_switch()

    async def _apply_later(self, wait: float) -> None:
        try:
            await asyncio.sleep(wait)
        finally:
            self._deferred = None
        self._try_switch()

    def _apply_switch(self) -> None:
        mode, fps = self._want
        old = self.spec.mode
        url, motion = self._want_feed
        feed_changed = self._want_feed != self.spec.feed
        self.spec = replace(self.spec, mode=mode, extract_fps=fps, motion=motion,
                            analysis_url=None if url == self.spec.source_url else url)
        if feed_changed:  # a real config change: whatever made us give up may be fixed now
            self._term_backoff.reset()
            self._backoff.reset()
            self._wake.set()
        self._last_switch = self._clock()
        self._m.mode_switches.labels(self.camera_id).inc()
        self._m.set_mode(self.camera_id, mode)
        log.info("extract %s -> %s @ %g fps (feed changed: %s): restarting ffmpeg", old, mode, fps, feed_changed,
                 extra={"ctx": {"camera_id": self.camera_id}})
        if self._src is not None:
            self._switching = True
            self._src.kill()
        self._publish_status()

    # ---- state / status --------------------------------------------------
    def _on_state_change(self, old: CamState, new: CamState) -> None:
        log.info("state %s -> %s", old.value, new.value, extra={"ctx": {"camera_id": self.camera_id}})
        self._m.set_state(self.camera_id, new)
        self._publish_status()

    def _publish_status(self) -> None:
        self._pub.publish_status(status_message(
            self.camera_id, self._s.worker_id, self.sm.state.value, self._fps.fps(),
            self.last_frame_ts, self.reconnects, self.error,
            mode=self.spec.mode, fps_target=self.spec.target_fps,
            motion_skipped_pct=self.motion_skipped_pct, error_class=self.error_class))

    def _poll_gate(self) -> None:
        """Move the running ffmpeg's gate counter into the metric and the rolling window."""
        src = self._src
        if src is None or not self.spec.gated:
            return
        d = src.gated_frames - self._gate_seen
        if d > 0:
            self._gate_seen += d
            self._gate_total += d
            self._m.motion_skipped.labels(self.camera_id).inc(d)
        self._skip.sample(self._gate_total, self._seq)

    async def _status_loop(self) -> None:
        while True:
            await asyncio.sleep(self._s.status_interval_s)
            self._poll_gate()
            self._publish_status()

    # ---- ffmpeg supervision ---------------------------------------------
    def _kill_source(self) -> None:
        if self._src is not None:
            self._src.kill()

    async def _run(self) -> None:
        if self._initial_delay:
            await asyncio.sleep(self._initial_delay)
        first = True
        while True:
            if not first:
                self._begin_reconnect()
                self._m.restarts.labels(self.camera_id).inc()
                self.reconnects += 1
                await self._sleep_before_retry()
            first = False
            reason = await self._session()
            if reason == "mode_switch":
                first = True  # deliberate restart: no backoff, not a reconnect
                continue
            log.warning("session ended: %s", reason, extra={"ctx": {"camera_id": self.camera_id, "error": self.error}})

    async def _sleep_before_retry(self) -> None:
        terminal = errors.is_terminal(self.error_class)
        delay = (self._term_backoff if terminal else self._backoff).next_delay()
        self._wake.clear()
        with contextlib.suppress(asyncio.TimeoutError):
            await asyncio.wait_for(self._wake.wait(), timeout=delay)

    def _begin_reconnect(self) -> None:
        if self._failures >= ERROR_AFTER_FAILURES or errors.is_terminal(self.error_class):
            self.sm.to(CamState.ERROR)  # stays in error while retrying
        else:
            self.sm.to(CamState.RECONNECTING)

    async def _session(self) -> str:
        """Run one ffmpeg process until it stalls or exits. Returns the reason."""
        src: FrameSource | None = None
        got_frame = False
        self._switching = False
        started_with = (self.spec.extract, self.spec.feed)
        self._gate_seen = 0
        self._skip.reset()
        self._skip.sample(self._gate_total, self._seq)  # baseline: frames emitted from now on are counted
        try:
            try:
                src = await self._factory(self.spec)
            except Exception as e:  # noqa: BLE001 - e.g. ffmpeg binary missing
                self._failures = max(self._failures + 1, ERROR_AFTER_FAILURES)
                self.error = f"cannot start ffmpeg: {e}"[:300]
                self.error_class = None
                self.sm.to(CamState.ERROR)
                return "spawn_failed"
            self._src = src
            if (self.spec.extract, self.spec.feed) != started_with:  # switched while ffmpeg was spawning
                self._switching = True
                src.kill()
            parser = JpegStreamParser()
            last_frame = self._clock()
            idle_floor = self._s.idle_stall_timeout_s if self.spec.mode == "idle" else 0.0
            if self.spec.gated:  # a static scene sends one frame per heartbeat; that is not a stall
                idle_floor = self.spec.motion.heartbeat_s + GATE_STALL_MARGIN_S
            while True:
                limit = max(self._s.stall_timeout_s if got_frame else self._s.startup_timeout_s, idle_floor)
                remaining = limit - (self._clock() - last_frame)
                try:
                    chunk = await asyncio.wait_for(src.read(READ_CHUNK), timeout=max(0.01, remaining))
                except asyncio.TimeoutError:
                    if self._switching:
                        return "mode_switch"
                    if self._clock() - last_frame >= limit:
                        tail = src.stderr_tail()
                        self.error = (f"no frames for >{limit:g}s" + (f": {tail}" if tail else ""))[:600]
                        cls = errors.classify(tail, None) if tail else errors.STALLED
                        # unrecognised text while the process still runs = plain stall
                        self.error_class = errors.STALLED if cls in (errors.EOF_, errors.DECODE_ERROR) else cls
                        self._failures += 0 if got_frame else 1
                        self.sm.to(CamState.STALLED)
                        return "stalled"
                    continue
                if not chunk:
                    if self._switching:
                        return "mode_switch"
                    rc = await src.wait()
                    self._failures += 0 if got_frame else 1
                    tail = src.stderr_tail()
                    self.error = (tail or f"ffmpeg exited rc={rc}")[:600]
                    self.error_class = errors.classify(tail, rc)
                    return f"eof rc={rc}"
                for data in parser.feed(chunk):
                    if self._on_jpeg(data):
                        got_frame = True
                        last_frame = self._clock()
                if parser.resyncs:
                    self._m.bad_frames.inc(parser.resyncs)
                    parser.resyncs = 0
        finally:
            self._poll_gate()
            if src is not None:
                src.kill()
                with contextlib.suppress(Exception):
                    await asyncio.shield(src.wait())
            self._src = None

    def _on_jpeg(self, data: bytes) -> bool:
        dims = jpeg_dimensions(data)
        if dims is None:
            self._m.bad_frames.inc()
            return False
        now = self._clock()
        ts = int(time.time() * 1000)
        self._seq += 1
        fid = str(uuid.uuid4())
        frame = Frame(fid, self.camera_id, ts, self._seq, dims[0], dims[1], data)
        # never blocks: produce() is a non-blocking enqueue; False = producer queue full -> drop
        if self._pub.publish_frame(frame):
            self._m.frames.labels(self.camera_id).inc()
        else:
            self._m.dropped.labels(self.camera_id).inc()
        self._fps.record()
        self.last_frame_ts = ts
        if self.sm.state is not CamState.ONLINE:
            self._failures = 0
            self.error = None
            self.error_class = None
            self._term_backoff.reset()
            self._online_since = now
            self.sm.to(CamState.ONLINE)
        elif self._online_since is not None and now - self._online_since >= STABLE_AFTER_S:
            self._backoff.reset()
            self._online_since = None
        return True
