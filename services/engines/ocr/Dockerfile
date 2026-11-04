# syntax=docker/dockerfile:1
# Build context: services/engines   (docker build -f ocr/Dockerfile services/engines)
# RapidOCR bundles its ONNX models in the wheel: no download at runtime.
FROM python:3.13-slim AS build
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
RUN python -m venv /opt/venv
COPY ocr/requirements.txt /tmp/requirements.txt
RUN /opt/venv/bin/pip install -r /tmp/requirements.txt
# --no-deps: rapidocr declares the non-headless opencv-python (its other deps are in requirements.txt)
RUN /opt/venv/bin/pip install --no-deps rapidocr-onnxruntime==1.2.3

FROM python:3.13-slim
RUN useradd --system --uid 10001 --no-create-home app && mkdir -p /cache && chown app /cache
COPY --from=build /opt/venv /opt/venv
COPY common/vp_common /app/vp_common
COPY ocr/*.py /app/
ENV PATH=/opt/venv/bin:$PATH PYTHONPATH=/app PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
ENV PORT=8103 MODELS_DIR=/models XDG_CACHE_HOME=/cache
ENV OMP_NUM_THREADS=2 ORT_THREADS=2 MAX_CONCURRENCY=2 MAX_QUEUE=2
WORKDIR /app
USER app
EXPOSE 8103
HEALTHCHECK --interval=10s --timeout=3s --start-period=60s --retries=3 CMD ["python","-c","import os,urllib.request as u;u.urlopen('http://127.0.0.1:%s/health'%os.environ['PORT'],timeout=2)"]
CMD ["python","-m","vp_common.serve","ocr_service:app","8103"]
