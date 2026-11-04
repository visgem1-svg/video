# Build context: services/planner  (run ./sync_contracts.ps1 first so ./contracts is current)
FROM python:3.13-slim AS build
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
WORKDIR /w
COPY requirements.txt .
RUN python -m venv /opt/venv && /opt/venv/bin/pip install -r requirements.txt

FROM python:3.13-slim
ENV PATH=/opt/venv/bin:$PATH PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 \
    CONTRACTS_DIR=/app/contracts PORT=8010
RUN useradd --system --uid 10001 --no-create-home planner
COPY --from=build /opt/venv /opt/venv
WORKDIR /app
COPY app ./app
COPY contracts ./contracts
USER 10001
EXPOSE 8010
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s \
  CMD python -c "import os,urllib.request as u; u.urlopen('http://127.0.0.1:'+os.getenv('PORT','8010')+'/health',timeout=2)"
CMD ["python", "-m", "app"]
