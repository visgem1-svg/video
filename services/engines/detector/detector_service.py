"""Object detector service (YOLOX, COCO 80 classes). Port 8101.

The model file is chosen with DETECTOR_MODEL (file name in MODELS_DIR). Any YOLOX ONNX export with
raw head output works (YOLOX-S 640 default; YOLOX-Tiny/Nano 416 for CPU-bound deployments): the
input size is read from the model.
"""
from __future__ import annotations

import os

import cv2
import numpy as np
import onnxruntime as ort
from fastapi import File, Form, HTTPException, UploadFile

from vp_common.imaging import clamp_box
from vp_common.runtime import create_app, ort_session_options, read_upload

MODEL_FILE = os.environ.get("DETECTOR_MODEL", "object_detection_yolox_2022nov.onnx")

COCO = [
    "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
    "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
    "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
    "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
    "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
    "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
    "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
    "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
    "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink",
    "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier",
    "toothbrush",
]
COCO_INDEX = {n: i for i, n in enumerate(COCO)}


class Yolox:
    """YOLOX ONNX (raw head output, decode + NMS done here). Square input size taken from the model."""

    def __init__(self, path, threads: int):
        self.session = ort.InferenceSession(str(path), sess_options=ort_session_options(threads),
                                            providers=["CPUExecutionProvider"])
        model_input = self.session.get_inputs()[0]
        self.input_name = model_input.name
        size = model_input.shape[-1]
        self.size = size if isinstance(size, int) else 640  # dynamic axis -> YOLOX default
        size = self.size
        grids, strides = [], []
        for stride in (8, 16, 32):
            n = size // stride
            xv, yv = np.meshgrid(np.arange(n), np.arange(n))
            grids.append(np.stack((xv, yv), 2).reshape(-1, 2))
            strides.append(np.full((n * n, 1), stride))
        self.grids = np.concatenate(grids).astype(np.float32)
        self.strides = np.concatenate(strides).astype(np.float32)

    def __call__(self, img: np.ndarray, conf: float = 0.4, nms: float = 0.45,
                 class_ids: set[int] | None = None) -> list[dict]:
        h, w = img.shape[:2]
        ratio = min(self.size / h, self.size / w)
        padded = np.full((self.size, self.size, 3), 114, dtype=np.uint8)
        resized = cv2.resize(img, (max(1, int(w * ratio)), max(1, int(h * ratio))))
        padded[: resized.shape[0], : resized.shape[1]] = resized
        blob = padded.transpose(2, 0, 1)[None].astype(np.float32)

        out = self.session.run(None, {self.input_name: blob})[0][0]
        scores = out[:, 4:5] * out[:, 5:]
        if class_ids is not None:  # filter before NMS: cheaper and avoids suppression by other classes
            mask = np.zeros(scores.shape[1], dtype=bool)
            mask[list(class_ids)] = True
            scores = np.where(mask, scores, 0.0)
        cls = scores.argmax(1)
        score = scores.max(1)
        keep = score > conf
        if not keep.any():
            return []
        xy = (out[keep, :2] + self.grids[keep]) * self.strides[keep]
        wh = np.exp(out[keep, 2:4]) * self.strides[keep]
        cls, score = cls[keep], score[keep]
        boxes = np.concatenate([xy - wh / 2, wh], 1) / ratio  # x, y, w, h

        idx = cv2.dnn.NMSBoxesBatched(boxes.tolist(), score.tolist(), cls.tolist(), conf, nms)
        results = []
        for i in np.array(idx).flatten():
            x, y, bw, bh = boxes[i]
            results.append({
                "class": COCO[int(cls[i])],
                "confidence": round(float(score[i]), 3),
                "box": clamp_box(x, y, x + bw, y + bh, w, h),
            })
        results.sort(key=lambda r: -r["confidence"])
        return results


def _load(settings):
    return Yolox(settings.models_dir / MODEL_FILE, settings.ort_threads)


def _warmup(model: Yolox):
    img = np.full((480, 640, 3), 127, np.uint8)
    for _ in range(3):
        model(img)


app = create_app("detector", ["detect_objects"], _load, _warmup,
                 description="YOLOX-S object detection (COCO)")


def parse_classes(classes: str | None) -> set[int] | None:
    if classes is None or not classes.strip():
        return None
    names = [c.strip().lower() for c in classes.split(",") if c.strip()]
    unknown = [n for n in names if n not in COCO_INDEX]
    if unknown:
        raise HTTPException(400, f"unknown classes: {', '.join(unknown)}")
    return {COCO_INDEX[n] for n in names}


@app.post("/detect_objects")
async def detect_objects(image: UploadFile = File(...), classes: str | None = Form(None),
                         min_confidence: float = Form(0.4, ge=0.0, le=1.0)):
    ids = parse_classes(classes)  # validate before spending a slot
    data = await read_upload(image, app.state.settings)
    objects, ms = await app.state.runtime.run(
        "detect_objects", data,
        lambda img: app.state.models(img, conf=max(min_confidence, 0.01), class_ids=ids))
    return {"objects": objects, "count": len(objects), "ms": ms}
