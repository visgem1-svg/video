# frame-extractor: python slim + distro ffmpeg, non-root, 2 stages (wheels built once, no pip in final layer cache)
FROM python:3.13-slim AS build
WORKDIR /w
COPY requirements.txt .
RUN pip wheel --no-cache-dir -r requirements.txt -w /wheels

FROM python:3.13-slim
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 PORT=9101
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg tini \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10001 --no-create-home fx
COPY --from=build /wheels /wheels
RUN pip install --no-cache-dir --no-index --find-links=/wheels /wheels/* && rm -rf /wheels
WORKDIR /app
COPY extractor ./extractor
USER 10001
EXPOSE 9101
# tini reaps ffmpeg zombies and forwards SIGTERM
ENTRYPOINT ["tini", "--"]
CMD ["python", "-m", "extractor.main"]
