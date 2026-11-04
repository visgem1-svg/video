"""Domain objects and message builders (CONTRACTS 5.1, 5.2 `meta` header per 9.7, 5.5)."""
from __future__ import annotations

import math
import time
from dataclasses import dataclass, field
from typing import Any, Mapping

from .motion_presets import DEFAULT_SENSITIVITY, PRESETS


IDLE_FPS = 0.2  # contract 9.8: one frame / 5 s


MOTION_HEARTBEAT_DEFAULT_S = 5.0
MOTION_HEARTBEAT_RANGE_S = (1.0, 60.0)


@dataclass(frozen=True, slots=True)
class MotionConfig:
    """Contract 9.9 `motion` (only ever instantiated when enabled; disabled = None on the spec)."""
    sensitivity: str = DEFAULT_SENSITIVITY
    heartbeat_s: float = MOTION_HEARTBEAT_DEFAULT_S

    def max_drops(self, fps: float) -> int:
        """mpdecimate `max`: consecutive drops allowed = frames per heartbeat - 1 (0 = nothing may be dropped)."""
        return max(0, math.ceil(self.heartbeat_s * fps - 1e-9) - 1)

    @classmethod
    def parse(cls, d: object) -> "MotionConfig | None":
        if not isinstance(d, Mapping) or d.get("enabled") is not True:
            return None  # missing / disabled / malformed = today's behaviour
        sens = d.get("sensitivity")
        sens = sens if sens in PRESETS else DEFAULT_SENSITIVITY
        try:
            hb = float(d.get("heartbeat_s", MOTION_HEARTBEAT_DEFAULT_S))
        except (TypeError, ValueError):
            hb = MOTION_HEARTBEAT_DEFAULT_S
        lo, hi = MOTION_HEARTBEAT_RANGE_S
        if not lo <= hb <= hi:  # also rejects nan
            hb = MOTION_HEARTBEAT_DEFAULT_S
        return cls(sens, hb)


@dataclass(frozen=True, slots=True)
class CameraSpec:
    camera_id: str
    source_url: str
    analysis_fps: float = 3.0
    generation: int = 0  # bumped by registry restart; a change restarts ffmpeg
    # contract 9.8: extraction mode from the heartbeat. compare=False: a (mode, fps) change is NOT a
    # new spec (the worker is kept and restarts its ffmpeg itself, coalesced), see CameraWorker.set_extract.
    mode: str = field(default="full", compare=False)
    extract_fps: float | None = field(default=None, compare=False)  # None = analysis_fps (no `extract`)
    # contract 9.9: substream + motion gate. Same story as mode/fps: a change restarts ffmpeg in place, coalesced.
    analysis_url: str | None = field(default=None, compare=False)
    motion: MotionConfig | None = field(default=None, compare=False)

    @property
    def read_url(self) -> str:
        return self.analysis_url or self.source_url

    @property
    def feed(self) -> tuple[str, MotionConfig | None]:
        return (self.read_url, self.motion)

    @property
    def gated(self) -> bool:
        """The motion gate is in the ffmpeg chain (full mode only, never idle)."""
        return self.mode == "full" and self.motion is not None and self.motion.max_drops(self.target_fps) > 0

    @property
    def target_fps(self) -> float:
        if self.extract_fps is not None:
            return self.extract_fps
        return IDLE_FPS if self.mode == "idle" else self.analysis_fps

    @property
    def extract(self) -> tuple[str, float]:
        return (self.mode, self.target_fps)

    @classmethod
    def from_config(cls, d: Mapping[str, Any]) -> "CameraSpec | None":
        """Parse a camera.config object from the registry. None if unusable/disabled."""
        cid = d.get("camera_id")
        url = d.get("source_url")
        if not cid or not url or d.get("enabled", True) is False:
            return None
        try:
            fps = float(d.get("analysis_fps") or 3)
        except (TypeError, ValueError):
            fps = 3.0
        try:
            gen = int(d.get("generation") or 0)
        except (TypeError, ValueError):
            gen = 0
        mode, efps = "full", None
        ex = d.get("extract")
        if isinstance(ex, Mapping):  # absent -> exactly the pre-9.8 behaviour
            mode = "idle" if ex.get("mode") == "idle" else "full"
            try:
                v = float(ex.get("fps"))
                efps = v if v > 0 else None
            except (TypeError, ValueError):
                efps = None
        aurl = d.get("analysis_source_url")
        aurl = str(aurl) if isinstance(aurl, str) and aurl else None
        return cls(str(cid), str(url), fps if fps > 0 else 3.0, gen, mode, efps, aurl, MotionConfig.parse(d.get("motion")))


@dataclass(slots=True)
class Frame:
    frame_id: str
    camera_id: str
    ts: int  # epoch ms UTC
    seq: int
    width: int
    height: int
    data: bytes


def frame_meta(f: Frame, worker_id: str) -> dict[str, Any]:
    return {
        "frame_id": f.frame_id,
        "camera_id": f.camera_id,
        "ts": f.ts,
        "seq": f.seq,
        "width": f.width,
        "height": f.height,
        "worker_id": worker_id,
    }


def status_message(
    camera_id: str,
    worker_id: str,
    state: str,
    fps_actual: float,
    last_frame_ts: int | None,
    reconnects: int,
    error: str | None,
    ts: int | None = None,
    mode: str = "full",
    fps_target: float = 0.0,
    motion_skipped_pct: float | None = None,
    error_class: str | None = None,
) -> dict[str, Any]:
    return {
        "camera_id": camera_id,
        "worker_id": worker_id,
        "state": state,
        "fps_actual": round(fps_actual, 2),
        "last_frame_ts": last_frame_ts,
        "reconnects": reconnects,
        "error": error,
        "mode": mode,
        "fps_target": round(fps_target, 3),
        "motion_skipped_pct": None if motion_skipped_pct is None else round(motion_skipped_pct, 1),
        "error_class": error_class,
        "ts": int(time.time() * 1000) if ts is None else ts,
    }
