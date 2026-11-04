"""License plate recognition service (fast-alpr: YOLOv9 plate detector + CCT plate OCR). Port 8102."""
from __future__ import annotations

import os

import numpy as np
from prometheus_client import Counter
from fastapi import File, UploadFile

from vp_common.imaging import clamp_box
from vp_common.runtime import create_app, ort_session_options, read_upload

DETECTOR_MODEL = "yolo-v9-t-384-license-plate-end2end"
OCR_MODEL = "cct-xs-v2-global-model"


def _load(settings):
    # fast-alpr libs cache under ~/.cache; point them at XDG_CACHE_HOME (/cache) on every OS.
    import fast_plate_ocr.inference.hub as ocr_hub
    import open_image_models.detection.core.hub as det_hub
    ocr_hub.MODEL_CACHE_DIR = settings.cache_dir / "fast-plate-ocr"
    det_hub.MODEL_CACHE_DIR = settings.cache_dir / "open-image-models"

    from fast_alpr import ALPR
    return ALPR(
        detector_model=DETECTOR_MODEL,
        ocr_model=OCR_MODEL,
        detector_providers=["CPUExecutionProvider"],
        detector_sess_options=ort_session_options(settings.ort_threads),
        ocr_providers=["CPUExecutionProvider"],
        ocr_sess_options=ort_session_options(settings.ort_threads),
    )


def _warmup(alpr):
    alpr.predict(np.full((480, 640, 3), 127, np.uint8))        # plate detector
    plate = np.full((70, 140, 3), 255, np.uint8)
    for _ in range(3):
        alpr.ocr.predict(plate)                                   # plate OCR


app = create_app("lpr", ["read_plate"], _load, _warmup,
                 description="Plate detection + recognition (fast-alpr)")


PLATE_MIN_CONFIDENCE = float(os.environ.get("PLATE_MIN_CONFIDENCE", "0.7"))
PLATE_MIN_CHARS = int(os.environ.get("PLATE_MIN_CHARS", "4"))
# A plate box touching the frame border is cut off by the frame: its (confident!) read is incomplete.
PLATE_EDGE_MARGIN = int(os.environ.get("PLATE_EDGE_MARGIN", "4"))

rejected = Counter("vp_engine_plate_rejected_total", "Plate reads dropped by the quality filter",
                   ["reason"], registry=app.state.runtime.registry)
for _r in ("low_confidence", "too_short", "cropped_at_edge"):
    rejected.labels(_r)


def _read(alpr, img: np.ndarray, min_conf: float | None = None, min_chars: int | None = None) -> list[dict]:
    """Quality filter: too short / low min-char-confidence / cut by the frame edge are dropped. Confidence = MIN over its character confidences (a truncated/cropped read has a weak char)."""
    min_conf = PLATE_MIN_CONFIDENCE if min_conf is None else min_conf
    min_chars = PLATE_MIN_CHARS if min_chars is None else min_chars
    h, w = img.shape[:2]
    plates = []
    for r in alpr.predict(img):
        raw = (r.ocr.text if r.ocr else "") or ""
        conf = r.ocr.confidence if r.ocr else None
        confs = [float(c) for c in np.atleast_1d(conf)] if conf is not None else []
        if len(confs) == len(raw):  # drop the '_' padding slots together with their confidences
            pairs = [(ch, c) for ch, c in zip(raw, confs) if ch != "_"]
        else:
            pairs = [(ch, confs[0] if confs else 0.0) for ch in raw if ch != "_"]
        text = "".join(ch for ch, _ in pairs).strip()
        if not text:
            continue  # detected a plate region but nothing readable
        char_confs = [round(c, 3) for _, c in pairs]
        plate_conf = min(char_confs) if char_confs else 0.0
        if len(text) < min_chars:
            rejected.labels("too_short").inc()
            continue
        if plate_conf < min_conf:
            rejected.labels("low_confidence").inc()
            continue
        b = r.detection.bounding_box
        if PLATE_EDGE_MARGIN > 0 and min(b.x1, b.y1, w - b.x2, h - b.y2) < PLATE_EDGE_MARGIN:
            rejected.labels("cropped_at_edge").inc()
            continue
        plates.append({
            "plate": text,
            "confidence": plate_conf,
            "char_confidences": char_confs,
            "detection_confidence": round(float(r.detection.confidence), 3),
            "box": clamp_box(b.x1, b.y1, b.x2, b.y2, w, h),
        })
    plates.sort(key=lambda p: -p["detection_confidence"])
    return plates


@app.post("/read_plate")
async def read_plate(image: UploadFile = File(...)):
    data = await read_upload(image, app.state.settings)
    plates, ms = await app.state.runtime.run("read_plate", data, lambda img: _read(app.state.models, img))
    return {"plates": plates, "count": len(plates), "ms": ms}
