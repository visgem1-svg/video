"""Local AI engines served over HTTP.

Every route takes a multipart image upload (field "image") and returns JSON.
All models are open source and run on CPU:
  /detect_objects  YOLOX-S (Apache 2.0, OpenCV Model Zoo)
  /read_plate      fast-alpr: YOLOv9 plate detector + CCT plate OCR (MIT)
  /read_text       RapidOCR (PaddleOCR models in ONNX, Apache 2.0)
  /detect_faces    YuNet (MIT, OpenCV Model Zoo)
  /face_match      YuNet + SFace (Apache 2.0) cosine similarity of two faces
"""
import time
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort
from fastapi import FastAPI, File, HTTPException, UploadFile

MODELS = Path(__file__).resolve().parent.parent / "models"

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

# SFace cosine threshold recommended by the OpenCV Model Zoo.
SFACE_COSINE_THRESHOLD = 0.363

app = FastAPI(title="Local AI engines")


class Yolox:
    def __init__(self, path: Path, size: int = 640):
        self.size = size
        self.session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        self.input_name = self.session.get_inputs()[0].name
        grids, strides = [], []
        for stride in (8, 16, 32):
            n = size // stride
            xv, yv = np.meshgrid(np.arange(n), np.arange(n))
            grids.append(np.stack((xv, yv), 2).reshape(-1, 2))
            strides.append(np.full((n * n, 1), stride))
        self.grids = np.concatenate(grids).astype(np.float32)
        self.strides = np.concatenate(strides).astype(np.float32)

    def __call__(self, img: np.ndarray, conf: float = 0.4, nms: float = 0.45):
        h, w = img.shape[:2]
        ratio = min(self.size / h, self.size / w)
        padded = np.full((self.size, self.size, 3), 114, dtype=np.uint8)
        resized = cv2.resize(img, (int(w * ratio), int(h * ratio)))
        padded[: resized.shape[0], : resized.shape[1]] = resized
        blob = padded.transpose(2, 0, 1)[None].astype(np.float32)

        out = self.session.run(None, {self.input_name: blob})[0][0]
        xy = (out[:, :2] + self.grids) * self.strides
        wh = np.exp(out[:, 2:4]) * self.strides
        scores = out[:, 4:5] * out[:, 5:]
        cls = scores.argmax(1)
        score = scores.max(1)
        keep = score > conf
        xy, wh, cls, score = xy[keep], wh[keep], cls[keep], score[keep]
        boxes = np.concatenate([xy - wh / 2, wh], 1) / ratio  # x, y, w, h

        idx = cv2.dnn.NMSBoxesBatched(boxes.tolist(), score.tolist(), cls.tolist(), conf, nms)
        results = []
        for i in np.array(idx).flatten():
            x, y, bw, bh = boxes[i]
            results.append({
                "class": COCO[int(cls[i])],
                "confidence": round(float(score[i]), 3),
                "box": [int(x), int(y), int(x + bw), int(y + bh)],
            })
        return results


yolox = Yolox(MODELS / "object_detection_yolox_2022nov.onnx")

from fast_alpr import ALPR  # noqa: E402  (heavy import, kept next to its use)
from rapidocr_onnxruntime import RapidOCR  # noqa: E402

alpr = ALPR(
    detector_model="yolo-v9-t-384-license-plate-end2end",
    ocr_model="cct-xs-v2-global-model",
)
ocr = RapidOCR()
face_detector = cv2.FaceDetectorYN.create(
    str(MODELS / "face_detection_yunet_2023mar.onnx"), "", (320, 320), 0.8, 0.3, 5000
)
face_recognizer = cv2.FaceRecognizerSF.create(str(MODELS / "face_recognition_sface_2021dec.onnx"), "")


async def read_image(upload: UploadFile) -> np.ndarray:
    data = np.frombuffer(await upload.read(), np.uint8)
    img = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if img is None:
        raise HTTPException(400, "could not decode image")
    return img


def find_faces(img: np.ndarray) -> np.ndarray:
    h, w = img.shape[:2]
    face_detector.setInputSize((w, h))
    _, faces = face_detector.detect(img)
    return faces if faces is not None else np.empty((0, 15))


def timed(fn):
    start = time.perf_counter()
    result = fn()
    return result, round((time.perf_counter() - start) * 1000, 1)


@app.get("/health")
def health():
    return {"status": "ok", "tools": ["detect_objects", "read_plate", "read_text", "detect_faces", "face_match"]}


@app.post("/detect_objects")
async def detect_objects(image: UploadFile = File(...)):
    img = await read_image(image)
    objects, ms = timed(lambda: yolox(img))
    return {"objects": objects, "count": len(objects), "ms": ms}


@app.post("/read_plate")
async def read_plate(image: UploadFile = File(...)):
    img = await read_image(image)
    raw, ms = timed(lambda: alpr.predict(img))
    plates = []
    for r in raw:
        text = r.ocr.text if r.ocr else None
        conf = r.ocr.confidence if r.ocr else None
        if isinstance(conf, (list, tuple, np.ndarray)):
            conf = float(np.mean(conf)) if len(conf) else None
        box = r.detection.bounding_box
        plates.append({
            "plate": text,
            "confidence": round(float(conf), 3) if conf is not None else None,
            "detection_confidence": round(float(r.detection.confidence), 3),
            "box": [box.x1, box.y1, box.x2, box.y2],
        })
    return {"plates": plates, "count": len(plates), "ms": ms}


@app.post("/read_text")
async def read_text(image: UploadFile = File(...)):
    img = await read_image(image)
    (raw, _), ms = timed(lambda: ocr(img))
    lines = [
        {"text": text, "confidence": round(float(score), 3), "box": np.array(box).astype(int).tolist()}
        for box, text, score in (raw or [])
    ]
    return {"lines": lines, "count": len(lines), "ms": ms}


@app.post("/detect_faces")
async def detect_faces(image: UploadFile = File(...)):
    img = await read_image(image)
    faces, ms = timed(lambda: find_faces(img))
    result = [
        {"box": [int(f[0]), int(f[1]), int(f[0] + f[2]), int(f[1] + f[3])], "confidence": round(float(f[14]), 3)}
        for f in faces
    ]
    return {"faces": result, "count": len(result), "ms": ms}


@app.post("/face_match")
async def face_match(image: UploadFile = File(...), reference: UploadFile = File(...)):
    img, ref = await read_image(image), await read_image(reference)

    def run():
        f1, f2 = find_faces(img), find_faces(ref)
        if len(f1) == 0 or len(f2) == 0:
            return None
        e1 = face_recognizer.feature(face_recognizer.alignCrop(img, f1[0]))
        e2 = face_recognizer.feature(face_recognizer.alignCrop(ref, f2[0]))
        return float(face_recognizer.match(e1, e2, cv2.FaceRecognizerSF_FR_COSINE))

    score, ms = timed(run)
    if score is None:
        return {"match": False, "score": None, "reason": "no face found in one of the images", "ms": ms}
    return {"match": score >= SFACE_COSINE_THRESHOLD, "score": round(score, 3),
            "threshold": SFACE_COSINE_THRESHOLD, "ms": ms}
