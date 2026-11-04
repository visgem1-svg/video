"""Configuration, read only from environment variables."""
from __future__ import annotations

import os
import socket
from dataclasses import dataclass
from typing import Mapping


def _bool(v: str | None, default: bool = False) -> bool:
    if v is None or v == "":
        return default
    return v.strip().lower() in ("1", "true", "yes", "on")


@dataclass(frozen=True, slots=True)
class Settings:
    worker_id: str = "fx-1"
    worker_capacity: int = 10
    registry_url: str = "http://registry:5001"
    kafka_bootstrap: str = "redpanda:9092"
    ffmpeg_path: str = "ffmpeg"
    ffmpeg_hwaccel: str = ""
    ffmpeg_threads: int = 2
    ffmpeg_skip_loop_filter: bool = False  # contract 9.9: decoder option -skip_loop_filter all (default off)
    jpeg_max_width: int = 960
    log_level: str = "INFO"
    port: int = 9101
    # optional tuning
    heartbeat_interval_s: float = 5.0
    stall_timeout_s: float = 5.0
    startup_timeout_s: float = 15.0
    status_interval_s: float = 2.0
    idle_stall_timeout_s: float = 30.0  # idle = keyframes only (GOP up to 10 s)
    mode_switch_min_interval_s: float = 10.0  # coalesce (mode, fps) restarts per camera

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None) -> "Settings":
        e = os.environ if env is None else env
        d = cls()  # slots dataclass: class attributes are descriptors, read defaults from an instance

        def s(k: str, d: str) -> str:
            v = e.get(k)
            return d if v is None or v == "" else v

        return cls(
            worker_id=s("WORKER_ID", socket.gethostname()),
            worker_capacity=int(s("WORKER_CAPACITY", "10")),
            registry_url=s("REGISTRY_URL", d.registry_url).rstrip("/"),
            kafka_bootstrap=s("KAFKA_BOOTSTRAP", d.kafka_bootstrap),
            ffmpeg_path=s("FFMPEG_PATH", "ffmpeg"),
            ffmpeg_hwaccel=s("FFMPEG_HWACCEL", ""),
            ffmpeg_threads=int(s("FFMPEG_THREADS", "2")),
            ffmpeg_skip_loop_filter=_bool(e.get("FFMPEG_SKIP_LOOP_FILTER")),
            jpeg_max_width=int(s("JPEG_MAX_WIDTH", "960")),
            log_level=s("LOG_LEVEL", "INFO").upper(),
            port=int(s("PORT", "9101")),
            heartbeat_interval_s=float(s("HEARTBEAT_INTERVAL_S", "5")),
            stall_timeout_s=float(s("STALL_TIMEOUT_S", "5")),
            startup_timeout_s=float(s("STARTUP_TIMEOUT_S", "15")),
            status_interval_s=float(s("STATUS_INTERVAL_S", "2")),
            idle_stall_timeout_s=float(s("IDLE_STALL_TIMEOUT_S", "30")),
            mode_switch_min_interval_s=float(s("MODE_SWITCH_MIN_INTERVAL_S", "10")),
        )
