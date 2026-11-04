"""Registry heartbeat client (CONTRACTS 7.2)."""
from __future__ import annotations

import logging
from dataclasses import dataclass, field
from typing import Any, Protocol

import aiohttp

log = logging.getLogger("registry")


class RegistryError(Exception):
    pass


@dataclass(slots=True)
class HeartbeatResponse:
    assigned: list[dict[str, Any]] = field(default_factory=list)
    capacity: int | None = None  # registry capacity override, if present


class RegistryApi(Protocol):
    async def heartbeat(self, capacity: int, running: list[str]) -> HeartbeatResponse: ...
    async def close(self) -> None: ...


class RegistryClient:
    def __init__(self, base_url: str, worker_id: str, timeout_s: float = 4.0) -> None:
        self._url = f"{base_url}/api/workers/{worker_id}/heartbeat"
        self._timeout = aiohttp.ClientTimeout(total=timeout_s)
        self._session: aiohttp.ClientSession | None = None

    async def heartbeat(self, capacity: int, running: list[str]) -> HeartbeatResponse:
        if self._session is None:
            self._session = aiohttp.ClientSession(timeout=self._timeout)
        try:
            async with self._session.post(self._url, json={"capacity": capacity, "running": running}) as r:
                if r.status != 200:
                    raise RegistryError(f"heartbeat HTTP {r.status}")
                body = await r.json()
        except (aiohttp.ClientError, TimeoutError, ValueError) as e:
            raise RegistryError(f"{type(e).__name__}: {e}") from e
        assigned = body.get("assigned") if isinstance(body, dict) else None
        if not isinstance(assigned, list):
            raise RegistryError("malformed heartbeat response")
        cap = body.get("capacity")
        if not isinstance(cap, int) or isinstance(cap, bool) or cap <= 0:
            cap = None
        return HeartbeatResponse(assigned, cap)

    async def close(self) -> None:
        if self._session is not None:
            await self._session.close()
