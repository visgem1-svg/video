"""ffmpeg subprocess wrapper: one process per camera, MJPEG on stdout."""
from __future__ import annotations

import asyncio
import logging
import sys
from collections import deque
from typing import Awaitable, Callable, Protocol

from .config import Settings
from .models import CameraSpec
from .motion_presets import PRESETS

log = logging.getLogger("ffmpeg")


class FrameSource(Protocol):
    """What CameraWorker needs from a decoder process (faked in tests)."""

    async def read(self, n: int) -> bytes: ...  # b"" on EOF
    def kill(self) -> None: ...
    async def wait(self) -> int | None: ...
    def stderr_tail(self) -> str: ...

    gated_frames: int  # frames the motion gate has dropped so far in this process (0 when not gated)


SourceFactory = Callable[[CameraSpec], Awaitable[FrameSource]]


def build_command(settings: Settings, spec: CameraSpec) -> list[str]:
    cmd = [settings.ffmpeg_path, "-hide_banner", "-nostdin", "-loglevel", "warning"]
    if settings.ffmpeg_hwaccel:
        cmd += ["-hwaccel", settings.ffmpeg_hwaccel]
    cmd += ["-threads", str(settings.ffmpeg_threads), "-fflags", "nobuffer", "-flags", "low_delay"]
    url = spec.read_url
    if url.lower().startswith(("rtsp://", "rtsps://")):
        # socket I/O timeout 10 s (microseconds): a dead peer errors out instead of hanging
        cmd += ["-rtsp_transport", "tcp", "-timeout", "10000000"]  # RTSP demuxer has no -rw_timeout
    # small probe: less memory per process (~5 MB default buffers) and faster stream start
    cmd += ["-probesize", "1000000", "-analyzeduration", "1000000"]
    fps = f"{spec.target_fps:g}"
    w = settings.jpeg_max_width
    if spec.mode == "idle":
        cmd += ["-skip_frame", "nokey"]  # decoder option (input side): only keyframes are decoded
    if settings.ffmpeg_skip_loop_filter:
        cmd += ["-skip_loop_filter", "all"]  # decoder option (must precede -i): skips the H.264 deblocking filter
    vf = f"fps={fps},scale='min({w},iw)':-2"
    out_opts: list[str] = []
    if spec.gated:  # contract 9.9: fps BEFORE mpdecimate (it compares consecutive kept frames)
        p = PRESETS[spec.motion.sensitivity]
        vf = (f"fps={fps},mpdecimate=hi={p.hi}:lo={p.lo}:frac={p.frac:g}:max={spec.motion.max_drops(spec.target_fps)},"
              f"scale='min({w},iw)':-2")
        # vfr: emit only the frames that survive (no duplicate refill). -progress: frame/out_time counters, see FfmpegSource
        out_opts = ["-fps_mode", "vfr", "-progress", "pipe:2", "-stats_period", "1"]
    cmd += [
        "-i", url,
        "-an", "-sn", "-dn",
        "-vf", vf, *out_opts,
        # -threads 1 here is the ENCODER's (the one before -i is the decoder's). The mjpeg encoder
        # defaults to one thread per core with its own frame buffers: measured 86 MB vs 22 MB anon
        # per camera at 540p. One thread encodes 3 fps easily.
        "-c:v", "mjpeg", "-q:v", "5", "-threads", "1",
        "-f", "image2pipe", "pipe:1",
    ]
    return cmd


_PROGRESS_KEYS = frozenset({
    "frame", "fps", "bitrate", "total_size", "out_time_us", "out_time_ms", "out_time", "dup_frames",
    "drop_frames", "speed", "progress"})


class FfmpegSource:
    """Gate accounting (`gated_frames`): the `fps` filter emits exactly one frame per 1/fps of stream time, so between
    two points of the output timeline (`out_time_us` = pts of the last written frame) ffmpeg should have produced
    round(dt*fps) frames; it wrote the `frame=` delta; the difference is what mpdecimate dropped. Stream-time based (no wall clock, immune to CPU
    starvation), costs ~12 short stderr lines per second per gated camera, none when the gate is off."""

    STDERR_LINES = 20

    def __init__(self, proc: asyncio.subprocess.Process, fps: float = 0.0) -> None:
        self._proc = proc
        self._fps = fps
        self.gated_frames = 0
        self._frame = 0
        self._out_us = 0
        self._base: tuple[int, int] | None = None  # (out_time_us, frame) of the first report with a frame
        self._stderr: deque[str] = deque(maxlen=self.STDERR_LINES)
        self._drain = asyncio.get_running_loop().create_task(self._drain_stderr())

    @classmethod
    async def start(cls, settings: Settings, spec: CameraSpec) -> "FfmpegSource":
        kwargs: dict = {}
        if sys.platform == "win32":
            import subprocess
            kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
        proc = await asyncio.create_subprocess_exec(
            *build_command(settings, spec),
            stdin=asyncio.subprocess.DEVNULL,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            **kwargs,
        )
        return cls(proc, spec.target_fps if spec.gated else 0.0)

    async def _drain_stderr(self) -> None:
        assert self._proc.stderr is not None
        try:
            while True:
                line = await self._proc.stderr.readline()
                if not line:
                    return
                text = line.decode("utf-8", "replace").rstrip()[:300]
                if self._fps and self._progress(text):
                    continue
                self._stderr.append(text)
        except Exception:  # noqa: BLE001 - best effort diagnostics
            return

    def _progress(self, text: str) -> bool:
        """Consume a `-progress` key=value line (kept out of the diagnostics tail). True if it was one."""
        key, sep, val = text.partition("=")
        if not sep or not (key in _PROGRESS_KEYS or key.startswith("stream_")):
            return False
        try:
            if key == "frame":
                self._frame = int(val)
            elif key == "out_time_us":
                self._out_us = int(val)
            elif key == "progress":  # end of one report block: counters are consistent now
                if self._frame > 0:
                    if self._base is None:  # first written frame's pts is not 0 on live RTSP: measure from here
                        self._base = (self._out_us, self._frame)
                    out0, frame0 = self._base
                    expected = round((self._out_us - out0) / 1e6 * self._fps)
                    self.gated_frames = max(self.gated_frames, expected - (self._frame - frame0))
        except ValueError:
            pass  # "N/A"
        return True

    async def read(self, n: int) -> bytes:
        assert self._proc.stdout is not None
        return await self._proc.stdout.read(n)

    def kill(self) -> None:
        if self._proc.returncode is None:
            try:
                self._proc.kill()
            except ProcessLookupError:
                pass

    async def wait(self) -> int | None:
        try:
            rc = await asyncio.wait_for(self._proc.wait(), timeout=5)
        except asyncio.TimeoutError:
            return None
        try:
            await asyncio.wait_for(self._drain, timeout=1)
        except (asyncio.TimeoutError, asyncio.CancelledError):
            self._drain.cancel()
        return rc

    def stderr_tail(self) -> str:
        return " | ".join(list(self._stderr)[-5:])[-600:]
