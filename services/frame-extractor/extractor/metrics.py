"""Prometheus metrics (instance-scoped registry, no module globals)."""
from __future__ import annotations

from prometheus_client import CollectorRegistry, Counter, Gauge, generate_latest

from .state import CamState


MODES = ("full", "idle")


class Metrics:
    def __init__(self) -> None:
        self.registry = CollectorRegistry()
        r = self.registry
        self.frames = Counter("vp_extractor_frames", "Frames extracted and published", ["camera_id"], registry=r)
        self.dropped = Counter("vp_extractor_frames_dropped", "Frames dropped (producer queue full / delivery failure)", ["camera_id"], registry=r)
        self.motion_skipped = Counter("vp_extractor_frames_motion_skipped", "Frames dropped by the motion gate (mpdecimate)", ["camera_id"], registry=r)
        self.restarts = Counter("vp_extractor_ffmpeg_restarts", "ffmpeg restarts", ["camera_id"], registry=r)
        self.state = Gauge("vp_extractor_camera_state", "1 for the current camera state", ["camera_id", "state"], registry=r)
        self.mode = Gauge("vp_extractor_camera_mode", "1 for the current extraction mode", ["camera_id", "mode"], registry=r)
        self.mode_switches = Counter("vp_extractor_mode_switches", "Extraction mode/fps changes applied (ffmpeg restarted)", ["camera_id"], registry=r)
        self.cameras_running = Gauge("vp_extractor_cameras_running", "Cameras managed by this worker", registry=r)
        self.errors = Counter("vp_extractor_publish_errors", "Produce/delivery failures", ["stage"], registry=r)
        self.bad_frames = Counter("vp_extractor_bad_frames", "Unparseable JPEG frames / resyncs", registry=r)

    def set_state(self, camera_id: str, state: CamState) -> None:
        for s in CamState:
            self.state.labels(camera_id, s.value).set(1 if s is state else 0)

    def set_mode(self, camera_id: str, mode: str) -> None:
        for m in MODES:
            self.mode.labels(camera_id, m).set(1 if m == mode else 0)

    def forget(self, camera_id: str) -> None:
        for s in CamState:
            try:
                self.state.remove(camera_id, s.value)
            except KeyError:
                pass
        for m in MODES:
            try:
                self.mode.remove(camera_id, m)
            except KeyError:
                pass
        for c in (self.frames, self.dropped, self.motion_skipped, self.restarts, self.mode_switches):
            try:
                c.remove(camera_id)
            except KeyError:
                pass

    def render(self) -> bytes:
        return generate_latest(self.registry)
