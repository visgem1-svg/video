"""Interfaces (ports) the service depends on; real adapters live in pg.py / kafka_pub.py / registry.py."""
from __future__ import annotations

from typing import Protocol


class PlanRepository(Protocol):
    async def get(self, plan_id: str) -> dict | None: ...
    async def list(self) -> list[dict]: ...
    async def save(self, plan: dict) -> None: ...
    async def save_many(self, plans: list[dict]) -> None:
        """Atomically save several plans (single transaction)."""
        ...
    async def delete(self, plan_id: str) -> bool: ...


class PlanPublisher(Protocol):
    async def publish(self, plan_id: str, plan: dict | None) -> None:
        """plan=None publishes a tombstone."""
        ...


class RegistryError(Exception):
    pass


class CameraRegistry(Protocol):
    async def set_plan(self, camera_id: str, plan_id: str | None, only_if_plan: str | None = None) -> bool:
        """Set (or clear, plan_id=None) the camera's plan_id. If only_if_plan is given, only change the camera
        when its current plan_id equals it. Returns True if the camera was changed. Raises RegistryError."""
        ...


class DraftCache(Protocol):
    """Entry = {"query", "draft": {name, explanation, steps}, "hits"}. Lookups are by indexed primary key."""
    async def get(self, key: str) -> dict | None: ...
    async def touch(self, key: str) -> int:
        """Register a hit; returns the new hit count."""
        ...
    async def put(self, key: str, normalized_query: str, query: str, draft: dict) -> None:
        """Insert or replace (hits reset to 0)."""
        ...
    async def evict(self, key: str) -> None: ...
    async def recent(self, limit: int) -> list[dict]:
        """Distinct queries, most recently used first: [{query, hits, last_used_at}]."""
        ...
