"""Image decoding helpers."""
from __future__ import annotations

import cv2
import numpy as np


class ImageError(Exception):
    def __init__(self, status: int, detail: str):
        super().__init__(detail)
        self.status = status
        self.detail = detail


def decode_image(data: bytes, max_pixels: int) -> np.ndarray:
    """Decode bytes to a BGR uint8 array. Raises ImageError(400/413)."""
    if not data:
        raise ImageError(400, "empty image")
    img = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
    if img is None:
        raise ImageError(400, "could not decode image")
    if img.shape[0] * img.shape[1] > max_pixels:
        raise ImageError(413, f"image exceeds {max_pixels} pixels")
    return img


def clamp_box(x1: float, y1: float, x2: float, y2: float, w: int, h: int) -> list[int]:
    return [
        int(max(0, min(w, round(x1)))), int(max(0, min(h, round(y1)))),
        int(max(0, min(w, round(x2)))), int(max(0, min(h, round(y2)))),
    ]
