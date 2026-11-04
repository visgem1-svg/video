# syntax=docker/dockerfile:1
# Build context: services/engines   (docker build -f lpr/Dockerfile services/engines)
# fast-alpr downloads its two ONNX models into $XDG_CACHE_HOME on first start (needs internet once;
# keep /cache on a volume so restarts are offline).
FROM python:3.13-slim AS build
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
RUN python -m venv /opt/venv
COPY lpr/requirements.txt /tmp/requirements.txt
RUN /opt/venv/bin/pip install -r /tmp/requirements.txt

FROM python:3.13-slim
RUN useradd --system --uid 10001 --no-create-home app && mkdir -p /cache && chown app /cache
COPY --from=build /opt/venv /opt/venv
COPY common/vp_common /app/vp_common
COPY lpr/*.py /app/
ENV PATH=/opt/venv/bin:$PATH PYTHONPATH=/app PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
ENV PORT=8102 MODELS_DIR=/models XDG_CACHE_HOME=/cache
ENV OMP_NUM_THREADS=2 ORT_THREADS=2 MAX_CONCURRENCY=2 MAX_QUEUE=2
WORKDIR /app
USER app
EXPOSE 8102
HEALTHCHECK --interval=10s --timeout=3s --start-period=90s --retries=3 CMD ["python","-c","import os,urllib.request as u;u.urlopen('http://127.0.0.1:%s/health'%os.environ['PORT'],timeout=2)"]
CMD ["python","-m","vp_common.serve","lpr_service:app","8102"]
