# syntax=docker/dockerfile:1
# Build context: services/engines   (docker build -f face/Dockerfile services/engines)
# YuNet + SFace run through OpenCV's DNN module: no onnxruntime needed.
FROM python:3.13-slim AS build
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
RUN python -m venv /opt/venv
COPY face/requirements.txt /tmp/requirements.txt
RUN /opt/venv/bin/pip install -r /tmp/requirements.txt

FROM python:3.13-slim
RUN useradd --system --uid 10001 --no-create-home app && mkdir -p /cache /data && chown app /cache /data
COPY --from=build /opt/venv /opt/venv
COPY common/vp_common /app/vp_common
COPY face/*.py /app/
ENV PATH=/opt/venv/bin:$PATH PYTHONPATH=/app PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
ENV PORT=8104 MODELS_DIR=/models XDG_CACHE_HOME=/cache FACE_DATA_DIR=/data
ENV OMP_NUM_THREADS=2 ORT_THREADS=2 MAX_CONCURRENCY=2 MAX_QUEUE=2
WORKDIR /app
USER app
EXPOSE 8104
VOLUME /data
HEALTHCHECK --interval=10s --timeout=3s --start-period=60s --retries=3 CMD ["python","-c","import os,urllib.request as u;u.urlopen('http://127.0.0.1:%s/health'%os.environ['PORT'],timeout=2)"]
CMD ["python","-m","vp_common.serve","face_service:app","8104"]
