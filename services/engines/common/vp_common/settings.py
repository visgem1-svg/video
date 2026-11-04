"""Configuration from environment variables only."""
from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


def _int(name: str, default: int, minimum: int = 0) -> int:
    try:
        return max(minimum, int(os.environ.get(name, default)))
    except ValueError:
        return default


@dataclass(frozen=True)
class Settings:
    service: str
    max_concurrency: int      # inference threads == requests executing at once
    max_queue: int            # extra requests allowed to wait; beyond that -> 429
    ort_threads: int          # ONNX Runtime / OpenCV intra-op threads per session
    models_dir: Path
    cache_dir: Path
    data_dir: Path
    max_image_bytes: int
    max_pixels: int
    log_level: str
    port: int

    @staticmethod
    def from_env(service: str) -> "Settings":
        home_cache = Path.home() / ".cache"
        return Settings(
            service=service,
            max_concurrency=_int("MAX_CONCURRENCY", 2, 1),
            max_queue=_int("MAX_QUEUE", 2, 0),
            ort_threads=_int("ORT_THREADS", 2, 1),
            models_dir=Path(os.environ.get("MODELS_DIR", "/models")),
            cache_dir=Path(os.environ.get("XDG_CACHE_HOME") or home_cache),
            data_dir=Path(os.environ.get("FACE_DATA_DIR") or os.environ.get("FACE_GALLERY_DIR") or "/data"),
            max_image_bytes=_int("MAX_IMAGE_BYTES", 10 * 1024 * 1024, 1024),
            max_pixels=_int("MAX_IMAGE_PIXELS", 25_000_000, 10_000),
            log_level=os.environ.get("LOG_LEVEL", "INFO").upper(),
            port=_int("PORT", 0, 0),
        )
