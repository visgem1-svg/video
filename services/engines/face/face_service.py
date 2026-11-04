"""Face detection / identification service (YuNet + SFace) with a persistent gallery. Port 8104.

NOTE: the gallery lives in a local volume (FACE_GALLERY_DIR). Run exactly ONE replica of this
service (or move the gallery to a shared store) - replicas would not see each other's enrolments.
"""
from __future__ import annotations

import base64
import logging
import queue
from contextlib import contextmanager

import cv2
import numpy as np
from fastapi import File, Form, HTTPException, Request, UploadFile
from fastapi.responses import Response

from gallery import Gallery
from vp_common.imaging import clamp_box
from vp_common.runtime import create_app, read_upload

log = logging.getLogger("vp.face")

DETECTOR_FILE = "face_detection_yunet_2023mar.onnx"
RECOGNIZER_FILE = "face_recognition_sface_2021dec.onnx"
DEFAULT_THRESHOLD = 0.363  # SFace cosine threshold recommended by the OpenCV Model Zoo
DETECT_MIN_CONF = 0.8
THUMB_SIZE = 96


class Models:
    """YuNet/SFace wrappers are stateful (input size, internal net) -> one pair per worker thread."""

    def __init__(self, settings):
        d, r = settings.models_dir / DETECTOR_FILE, settings.models_dir / RECOGNIZER_FILE
        self._pool: queue.Queue = queue.Queue()
        for _ in range(settings.max_concurrency):
            det = cv2.FaceDetectorYN.create(str(d), "", (320, 320), DETECT_MIN_CONF, 0.3, 5000)
            rec = cv2.FaceRecognizerSF.create(str(r), "")
            self._pool.put((det, rec))
        self.gallery = Gallery(settings.data_dir)
        self.gallery.load()

    @contextmanager
    def acquire(self):
        pair = self._pool.get()
        try:
            yield pair
        finally:
            self._pool.put(pair)


def _find(det, img: np.ndarray) -> np.ndarray:
    h, w = img.shape[:2]
    det.setInputSize((w, h))
    _, faces = det.detect(img)
    return faces if faces is not None else np.empty((0, 15), np.float32)


def _box(f, w, h) -> list[int]:
    return clamp_box(f[0], f[1], f[0] + f[2], f[1] + f[3], w, h)


def _embed(rec, img, face) -> np.ndarray:
    return rec.feature(rec.alignCrop(img, face)).reshape(-1)


def _load(settings):
    return Models(settings)


def _warmup(m: Models):
    img = np.full((480, 640, 3), 127, np.uint8)
    blank = np.full((112, 112, 3), 127, np.uint8)
    for _ in range(m._pool.qsize()):  # touch every pair
        with m.acquire() as (det, rec):
            for _ in range(2):
                _find(det, img)
                rec.feature(blank)


app = create_app("face", ["detect_faces", "identify_faces", "enroll_person"], _load, _warmup,
                 description="Face detection + identification (YuNet + SFace)")


@app.post("/detect_faces")
async def detect_faces(image: UploadFile = File(...)):
    data = await read_upload(image, app.state.settings)

    def run(img):
        h, w = img.shape[:2]
        with app.state.models.acquire() as (det, _):
            faces = _find(det, img)
        return [{"box": _box(f, w, h), "confidence": round(float(f[14]), 3),
                 "person_id": None, "person_name": None, "score": None} for f in faces]

    faces, ms = await app.state.runtime.run("detect_faces", data, run)
    return {"faces": faces, "count": len(faces), "ms": ms}


@app.post("/identify_faces")
async def identify_faces(image: UploadFile = File(...),
                         threshold: float = Form(DEFAULT_THRESHOLD, ge=-1.0, le=1.0)):
    data = await read_upload(image, app.state.settings)

    def run(img):
        m: Models = app.state.models
        h, w = img.shape[:2]
        out = []
        with m.acquire() as (det, rec):
            for f in _find(det, img):
                pid, name, score = m.gallery.search(_embed(rec, img, f)) if len(m.gallery) else (None, None, None)
                known = score is not None and score >= threshold
                out.append({
                    "box": _box(f, w, h), "confidence": round(float(f[14]), 3),
                    "person_id": pid if known else None,
                    "person_name": name if known else None,
                    "score": round(score, 3) if score is not None else None,
                })
        return out

    faces, ms = await app.state.runtime.run("identify_faces", data, run)
    return {"faces": faces, "count": len(faces), "ms": ms}


def _single_face(det, rec, img: np.ndarray):
    """Exactly one face required for the library; returns (embedding, aligned 112x112 thumbnail jpeg)."""
    faces = _find(det, img)
    if len(faces) == 0:
        raise HTTPException(400, "no face found in the image: use a clear photo of one person")
    if len(faces) > 1:
        raise HTTPException(400, f"{len(faces)} faces found in the image: use a photo with exactly one face")
    f = faces[0]
    ok, jpg = cv2.imencode(".jpg", rec.alignCrop(img, f), [cv2.IMWRITE_JPEG_QUALITY, 85])
    return _embed(rec, img, f), (jpg.tobytes() if ok else b"")


def _view(p) -> dict:
    return {"person_id": p.person_id, "name": p.name, "photos": p.photos, "created_at": p.created_at,
            "photo_url": f"/api/persons/{p.person_id}/photo"}


def _gallery() -> Gallery:
    if not app.state.runtime.ready:
        raise HTTPException(503, "engine not ready")
    return app.state.models.gallery


def _clean_name(name) -> str:
    name = (name or "").strip()
    if not name or len(name) > 100:
        raise HTTPException(422, "name must be 1-100 characters")
    return name


@app.post("/persons")
async def enroll_person(name: str = Form(..., min_length=1, max_length=100), image: UploadFile = File(...)):
    name = _clean_name(name)
    data = await read_upload(image, app.state.settings)

    def run(img):
        with app.state.models.acquire() as (det, rec):
            emb, thumb = _single_face(det, rec, img)
        return app.state.models.gallery.add(name, emb, thumb)

    person, _ = await app.state.runtime.run("enroll_person", data, run)
    log.info("person enrolled", extra={"person_id": person.person_id})
    return {"person_id": person.person_id, "name": person.name, "photos": 1}


@app.post("/persons/{person_id}/photos")
async def add_photo(person_id: str, image: UploadFile = File(...)):
    if _gallery().get(person_id) is None:
        raise HTTPException(404, "person not found")
    data = await read_upload(image, app.state.settings)

    def run(img):
        with app.state.models.acquire() as (det, rec):
            emb, _ = _single_face(det, rec, img)
        return app.state.models.gallery.add_photo(person_id, emb)

    person, _ = await app.state.runtime.run("enroll_person", data, run)
    if person is None:
        raise HTTPException(404, "person not found")
    return _view(person)


@app.get("/persons")
def list_persons():
    persons = [_view(p) for p in _gallery().list()]
    return {"persons": persons, "count": len(persons)}


@app.put("/persons/{person_id}")
async def rename_person(person_id: str, request: Request):
    ctype = request.headers.get("content-type", "")
    try:
        body = await request.json() if "json" in ctype else dict(await request.form())
    except Exception:
        raise HTTPException(422, "expected JSON body {\"name\": ...}")
    person = _gallery().rename(person_id, _clean_name(body.get("name") if isinstance(body, dict) else None))
    if person is None:
        raise HTTPException(404, "person not found")
    return _view(person)


@app.get("/persons/{person_id}/photo")
def person_photo(person_id: str):
    p = _gallery().get(person_id)
    if p is None or not p.thumbnail_b64:
        raise HTTPException(404, "photo not found")
    return Response(base64.b64decode(p.thumbnail_b64), media_type="image/jpeg",
                    headers={"Cache-Control": "private, max-age=60"})


@app.delete("/persons/{person_id}")
def delete_person(person_id: str):
    if not _gallery().delete(person_id):
        raise HTTPException(404, "person not found")
    return {"deleted": person_id}
