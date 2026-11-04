"""Real adapters: Postgres (asyncpg), Kafka (aiokafka), Registry HTTP (httpx)."""
from __future__ import annotations

import json
import logging

import asyncpg
import httpx

from .ports import RegistryError

logger = logging.getLogger("planner.adapters")

MIGRATIONS = [
    # version 1
    """
    CREATE TABLE IF NOT EXISTS plans (
        plan_id     TEXT PRIMARY KEY,
        version     INTEGER NOT NULL,
        status      TEXT NOT NULL,
        name        TEXT NOT NULL,
        plan        JSONB NOT NULL,
        created_at  BIGINT NOT NULL,
        updated_at  BIGINT NOT NULL
    );
    CREATE INDEX IF NOT EXISTS plans_status_idx ON plans (status);
    """,
    # version 2: draft cache
    """
    CREATE TABLE IF NOT EXISTS draft_cache (
        cache_key         TEXT PRIMARY KEY,
        normalized_query  TEXT NOT NULL,
        query             TEXT NOT NULL,
        draft             JSONB NOT NULL,
        hits              INTEGER NOT NULL DEFAULT 0,
        created_at        BIGINT NOT NULL,
        last_hit_at       BIGINT NOT NULL
    );
    CREATE INDEX IF NOT EXISTS draft_cache_last_idx ON draft_cache (last_hit_at DESC);
    """,
]


class PgPlanRepository:
    def __init__(self, pool: asyncpg.Pool):
        self.pool = pool

    @classmethod
    async def connect(cls, dsn: str) -> "PgPlanRepository":
        pool = await asyncpg.create_pool(dsn, min_size=1, max_size=4)
        repo = cls(pool)
        await repo.migrate()
        return repo

    async def migrate(self) -> None:
        async with self.pool.acquire() as c:
            async with c.transaction():
                await c.execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)")
                await c.execute("LOCK TABLE schema_version IN EXCLUSIVE MODE")
                cur = await c.fetchval("SELECT COALESCE(MAX(version), 0) FROM schema_version")
                for i, sql in enumerate(MIGRATIONS, start=1):
                    if i > cur:
                        await c.execute(sql)
                        await c.execute("INSERT INTO schema_version VALUES ($1)", i)
                        logger.info("applied migration %d", i)

    async def ping(self) -> bool:
        try:
            async with self.pool.acquire() as c:
                await c.fetchval("SELECT 1")
            return True
        except Exception:
            return False

    async def get(self, plan_id: str) -> dict | None:
        row = await self.pool.fetchrow("SELECT plan FROM plans WHERE plan_id=$1", plan_id)
        return json.loads(row["plan"]) if row else None

    async def list(self) -> list[dict]:
        rows = await self.pool.fetch("SELECT plan FROM plans ORDER BY created_at DESC")
        return [json.loads(r["plan"]) for r in rows]

    async def save(self, plan: dict) -> None:
        await self.save_many([plan])

    async def save_many(self, plans: list[dict]) -> None:
        import time
        async with self.pool.acquire() as c:
            async with c.transaction():
                for plan in plans:
                    await self._upsert(c, plan, int(time.time() * 1000))

    @staticmethod
    async def _upsert(c, plan: dict, now: int) -> None:
        await c.execute(
            """INSERT INTO plans (plan_id, version, status, name, plan, created_at, updated_at)
               VALUES ($1,$2,$3,$4,$5::jsonb,$6,$7)
               ON CONFLICT (plan_id) DO UPDATE SET version=EXCLUDED.version, status=EXCLUDED.status,
                 name=EXCLUDED.name, plan=EXCLUDED.plan, updated_at=EXCLUDED.updated_at""",
            plan["plan_id"], plan["version"], plan["status"], plan["name"], json.dumps(plan),
            plan["created_at"], now,
        )

    async def delete(self, plan_id: str) -> bool:
        res = await self.pool.execute("DELETE FROM plans WHERE plan_id=$1", plan_id)
        return res.endswith("1")

    async def close(self) -> None:
        await self.pool.close()


class PgDraftCache:
    def __init__(self, pool: asyncpg.Pool):
        self.pool = pool

    async def get(self, key: str) -> dict | None:
        r = await self.pool.fetchrow("SELECT query, draft, hits FROM draft_cache WHERE cache_key=$1", key)
        return {"query": r["query"], "draft": json.loads(r["draft"]), "hits": r["hits"]} if r else None

    async def touch(self, key: str) -> int:
        import time
        return await self.pool.fetchval(
            "UPDATE draft_cache SET hits=hits+1, last_hit_at=$2 WHERE cache_key=$1 RETURNING hits",
            key, int(time.time() * 1000)) or 0

    async def put(self, key: str, normalized_query: str, query: str, draft: dict) -> None:
        import time
        now = int(time.time() * 1000)
        await self.pool.execute(
            """INSERT INTO draft_cache (cache_key, normalized_query, query, draft, hits, created_at, last_hit_at)
               VALUES ($1,$2,$3,$4::jsonb,0,$5,$5)
               ON CONFLICT (cache_key) DO UPDATE SET query=EXCLUDED.query, draft=EXCLUDED.draft, hits=0,
                 created_at=EXCLUDED.created_at, last_hit_at=EXCLUDED.last_hit_at""",
            key, normalized_query, query, json.dumps(draft), now)

    async def evict(self, key: str) -> None:
        await self.pool.execute("DELETE FROM draft_cache WHERE cache_key=$1", key)

    async def recent(self, limit: int) -> list[dict]:
        rows = await self.pool.fetch(
            """SELECT query, hits, last_hit_at FROM (
                 SELECT DISTINCT ON (normalized_query) query, hits, last_hit_at FROM draft_cache
                 ORDER BY normalized_query, last_hit_at DESC) t
               ORDER BY last_hit_at DESC LIMIT $1""", limit)
        return [{"query": r["query"], "hits": r["hits"], "last_used_at": r["last_hit_at"]} for r in rows]


class CameraConfigConsumer:
    """Consumes compacted `camera.config` from the beginning (own group `planner`) and feeds
    PlannerService.sync_camera. Offsets are committed after each message is handled."""

    def __init__(self, bootstrap: str, handler, topic: str = "camera.config", group: str = "planner"):
        self.bootstrap, self.handler, self.topic, self.group = bootstrap, handler, topic, group
        self._task = None

    def start(self) -> None:
        import asyncio
        self._task = asyncio.create_task(self._run())

    async def stop(self) -> None:
        if self._task:
            self._task.cancel()

    async def _run(self) -> None:
        import asyncio
        from aiokafka import AIOKafkaConsumer
        while True:
            c = AIOKafkaConsumer(self.topic, bootstrap_servers=self.bootstrap, group_id=self.group,
                                 auto_offset_reset="earliest", enable_auto_commit=False)
            try:
                await c.start()
                async for msg in c:
                    try:
                        cam = json.loads(msg.value) if msg.value else None
                        cid = msg.key.decode() if msg.key else (cam or {}).get("camera_id")
                        if cid:
                            await self.handler(cid, (cam or {}).get("plan_id") if cam else None)
                    except Exception:  # noqa: BLE001  (bad message must not stall the stream)
                        logger.exception("camera.config message failed")
                    await c.commit()
            except asyncio.CancelledError:
                await c.stop()
                raise
            except Exception:  # noqa: BLE001
                logger.exception("camera.config consumer error; retrying in 5s")
                try:
                    await c.stop()
                except Exception:  # noqa: BLE001
                    pass
                await asyncio.sleep(5)


class KafkaPlanPublisher:
    def __init__(self, bootstrap: str, topic: str):
        from aiokafka import AIOKafkaProducer
        self.topic = topic
        self._p = AIOKafkaProducer(bootstrap_servers=bootstrap, acks="all", enable_idempotence=True,
                                   request_timeout_ms=10000)
        self.started = False

    async def start(self) -> None:
        await self._p.start()
        self.started = True

    async def stop(self) -> None:
        if self.started:
            await self._p.stop()
            self.started = False

    async def publish(self, plan_id: str, plan: dict | None) -> None:
        value = None if plan is None else json.dumps(plan).encode()
        await self._p.send_and_wait(self.topic, key=plan_id.encode(), value=value)


class HttpCameraRegistry:
    def __init__(self, base_url: str, timeout_s: float = 5.0, transport: httpx.AsyncBaseTransport | None = None):
        self.base = base_url.rstrip("/")
        self._http = httpx.AsyncClient(timeout=timeout_s, transport=transport)

    async def aclose(self) -> None:
        await self._http.aclose()

    async def set_plan(self, camera_id: str, plan_id: str | None, only_if_plan: str | None = None) -> bool:
        url = f"{self.base}/api/cameras/{camera_id}"
        try:
            r = await self._http.get(url)
            if r.status_code == 404:
                raise RegistryError(f"camera '{camera_id}' not found in registry")
            r.raise_for_status()
            cam = r.json()
            if only_if_plan is not None and cam.get("plan_id") != only_if_plan:
                return False
            if cam.get("plan_id") == plan_id:
                return False
            cam.pop("updated_at", None)
            cam["plan_id"] = plan_id
            r = await self._http.put(url, json=cam)
            r.raise_for_status()
            return True
        except httpx.HTTPError as e:
            raise RegistryError(f"registry call for camera '{camera_id}' failed: {type(e).__name__}: {e}") from e
