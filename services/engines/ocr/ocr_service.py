"""General text OCR service (RapidOCR, PaddleOCR PP-OCRv3 ONNX models bundled in the wheel). Port 8103."""
from __future__ import annotations

import cv2
import numpy as np
from fastapi import File, UploadFile

from vp_common.imaging import clamp_box
from vp_common.runtime import create_app, read_upload


def _load(settings):
    # RapidOCR builds its own ORT sessions with default (all-cores) threading; inject our thread limit.
    import onnxruntime as ort
    import rapidocr_onnxruntime.utils as rutils

    def limited_options():
        so = ort.SessionOptions()
        so.intra_op_num_threads = settings.ort_threads
        so.inter_op_num_threads = 1
        return so

    rutils.SessionOptions = limited_options
    from rapidocr_onnxruntime import RapidOCR
    return RapidOCR()


def _warmup(engine):
    img = np.full((200, 600, 3), 255, np.uint8)
    cv2.putText(img, "WARMUP 123", (20, 120), cv2.FONT_HERSHEY_SIMPLEX, 2, (0, 0, 0), 4)
    for _ in range(2):
        engine(img)


app = create_app("ocr", ["read_text"], _load, _warmup, description="Text OCR (RapidOCR)")


def _read(engine, img: np.ndarray) -> list[dict]:
    h, w = img.shape[:2]
    raw, _ = engine(img)
    lines = []
    for quad, text, score in raw or []:
        pts = np.array(quad, dtype=float)
        x1, y1 = pts.min(axis=0)
        x2, y2 = pts.max(axis=0)
        lines.append({"text": text, "confidence": round(float(score), 3),
                      "box": clamp_box(x1, y1, x2, y2, w, h)})
    return lines


@app.post("/read_text")
async def read_text(image: UploadFile = File(...)):
    data = await read_upload(image, app.state.settings)
    lines, ms = await app.state.runtime.run("read_text", data, lambda img: _read(app.state.models, img))
    return {"lines": lines, "count": len(lines), "ms": ms}
