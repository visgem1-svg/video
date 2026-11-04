# syntax=docker/dockerfile:1
FROM python:3.13-slim AS build
WORKDIR /w
COPY requirements.txt .
RUN python -m venv /opt/venv && /opt/venv/bin/pip install --no-cache-dir -r requirements.txt

FROM python:3.13-slim
ENV PATH=/opt/venv/bin:$PATH PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
RUN useradd --system --uid 10001 --no-create-home vpctl && mkdir -p /sd && chown vpctl /sd
COPY --from=build /opt/venv /opt/venv
WORKDIR /app
COPY controller ./controller
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 5007
# Starts as root ONLY to map the docker socket's GID to a group, then drops to vpctl (setpriv).
ENTRYPOINT ["/entrypoint.sh"]
CMD ["python", "-m", "controller.main"]
