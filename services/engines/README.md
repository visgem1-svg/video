# AI engines (team ai)

Four single-responsibility FastAPI services (API: CONTRACTS 7.1) on CPU, sharing `common/vp_common`.

| Service | Port | Route(s) | Model |
|---|---|---|---|
| detector | 8101 | `POST /detect_objects` (`image`, `classes`, `min_confidence`) | YOLOX-S ONNX (COCO-80) |
| lpr | 8102 | `POST /read_plate` | fast-alpr: `yolo-v9-t-384-license-plate-end2end` + `cct-xs-v2-global-model` |
| ocr | 8103 | `POST /read_text` (boxes `[x1,y1,x2,y2]`) | RapidOCR (PP-OCRv3 det/cls/rec, bundled in wheel) |
| face | 8104 | `POST /detect_faces`, `POST /identify_faces` (`threshold`), `POST/GET /persons`, `DELETE /persons/{id}` | YuNet + SFace (OpenCV DNN) |

All: `GET /health` (503 while loading/warming, 200 only after warm-up), `GET /metrics`
(`vp_engine_requests_total{tool,status}`, `vp_engine_inference_seconds{tool}`, `vp_engine_inflight{tool}`,
plus process metrics), JSON logs on stdout, SIGTERM = graceful (uvicorn drains, executor joins).
Every success response carries `ms` (inference time, excluding queueing/decoding).

Behaviour notes: `detect_faces` returns `person_id/person_name/score` = null (CONTRACTS 9.1). Detector `classes`
is a case-insensitive comma list; unknown names -> 400. Unreadable plates (detected, no text) are dropped.
Errors: 400 undecodable image, 413 too large, 422 bad form, 429 overloaded (`Retry-After: 1`), 503 not ready.
Person ids are UUID v4 strings. Enrolment uses the largest face in the image (422 if none).

## Environment variables
| Var | Default | Meaning |
|---|---|---|
| `PORT` | 8101..8104 | listen port |
| `MODELS_DIR` | `/models` | read-only ONNX models (detector, face) |
| `XDG_CACHE_HOME` | `~/.cache` | fast-alpr model cache (lpr; patched in code so it works on any OS) |
| `FACE_DATA_DIR` | `/data` | face gallery dir (`gallery.json`; legacy alias `FACE_GALLERY_DIR`) |
| `MAX_CONCURRENCY` | 2 | inference threads = requests executing at once |
| `MAX_QUEUE` | 2 | extra requests allowed to wait; admitted > CONCURRENCY+QUEUE -> 429 |
| `ORT_THREADS` | 2 | ONNX Runtime / OpenCV intra-op threads per session (`OMP_NUM_THREADS=2` also set by compose) |
| `MAX_IMAGE_BYTES` / `MAX_IMAGE_PIXELS` | 10 MiB / 25 MP | upload limits |
| `LOG_LEVEL` | INFO | |

## Run / test (no Docker)
```
cd services/engines
python -m venv .venv && .venv/Scripts/pip install -r detector/requirements.txt -r lpr/requirements.txt -r ocr/requirements.txt pytest httpx psutil
.venv/Scripts/pip install --no-deps rapidocr-onnxruntime==1.2.3
export MODELS_DIR=../../models XDG_CACHE_HOME=$PWD/.cache
PYTHONPATH=common:detector python -m vp_common.serve detector_service:app 8101     # likewise lpr/ocr/face
python -m pytest detector/tests      # then lpr/tests, ocr/tests, face/tests (one service per process)
```
Docker (manager does this): `docker build -f <name>/Dockerfile services/engines`. Multi-stage, python:3.13-slim,
venv copied into the final stage, non-root uid 10001, healthcheck via `python -c urllib`. lpr downloads its models into
`/cache` on first start (needs internet once; use the `engines-cache` volume). Dockerfiles were not built here (Docker not allowed).

## Measured (this laptop, 8 cores, ORT_THREADS=2, MAX_CONCURRENCY=2, one process each, 20 sequential requests)
| Service | ready after | RSS after warm-up / after 20 req | p50 inference | budget |
|---|---|---|---|---|
| detector (street.jpg) | 2.8 s | 200 / 201 MB | 182 ms | 512 MB |
| lpr (car.jpg) | 3.9 s | 139 / 152 MB | 38 ms | 384 MB |
| ocr (text.png) | 5.5 s | 132 / 131 MB | 1065 ms (first request 1.5 s) | 512 MB |
| face (messi.jpg identify) | 1.9 s | 308 / 311 MB | 13 ms (empty gallery; detect + embed) | 384 MB |

face holds one YuNet+SFace pair per worker thread (YuNet/SFace objects are stateful), hence 308 MB; with
`MAX_CONCURRENCY=1` it is about half. Linux container RSS will differ somewhat (measured on Windows).

## Licenses (from package metadata where installed)
YOLOX-S (OpenCV Model Zoo export): Apache-2.0. fast-alpr, open-image-models, fast-plate-ocr: MIT (verified in METADATA);
the plate models are downloaded from the open-image-models / fast-plate-ocr GitHub releases (MIT projects). RapidOCR:
Apache-2.0 (verified), PaddleOCR PP-OCRv3 weights Apache-2.0. YuNet: MIT; SFace: Apache-2.0 (OpenCV Model Zoo READMEs; not verifiable offline here).

## Scaling to server / GPU
* Replicas: stateless except face (gallery on a local volume -> run ONE face replica, or move the gallery to a shared
  store such as pgvector). Scale detector/lpr/ocr horizontally; 1,200 fps needs GPU for the detector in practice.
* GPU: swap `onnxruntime` for `onnxruntime-gpu` and `providers=["CUDAExecutionProvider"]` (the only touched spots are the
  session creation in `detector_service.Yolox`, `lpr_service._load`, `ocr_service._load`); fast-alpr takes `*_providers`.
* Triton: export the same ONNX files to a Triton model repository (ONNX backend, dynamic batching with
  `max_queue_delay_microseconds` ~ 5-10 ms, preferred batch 8-32); keep the pre/post-processing (letterbox, YOLOX decode + NMS, plate crop)
  in these services as thin HTTP front-ends calling Triton gRPC. The YOLOX decode already works on a leading batch dim after a small change
  (stack letterboxed frames). Cross-request batching is the main lever: the executor can also send several frames per call later.
