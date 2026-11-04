#!/bin/sh
# Drop root before running the app. The docker socket group is taken from DOCKER_GID, or
# auto-detected from the mounted socket. Falls back to running as root only if neither works.
set -e
GID="${DOCKER_GID:-}"
if [ -z "$GID" ] && [ -S /var/run/docker.sock ]; then GID="$(stat -c %g /var/run/docker.sock)"; fi
mkdir -p "$(dirname "${FILE_SD_PATH:-/sd/extractors.json}")"
chown vpctl "$(dirname "${FILE_SD_PATH:-/sd/extractors.json}")" 2>/dev/null || chmod 777 "$(dirname "${FILE_SD_PATH:-/sd/extractors.json}")" 2>/dev/null || true
if [ -n "$GID" ] && [ "$GID" != "0" ]; then
  getent group "$GID" >/dev/null || groupadd -g "$GID" dockersock
  exec setpriv --reuid=vpctl --regid=vpctl --groups="$GID" "$@"
fi
echo '{"level":"WARNING","msg":"docker socket GID is 0 or unknown: running as root (dev only)"}'
exec "$@"
