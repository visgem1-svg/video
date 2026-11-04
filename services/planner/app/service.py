"""Planner business logic: draft (LLM + validate + retry), activate, list/get, delete."""
from __future__ import annotations

import asyncio
import hashlib
import re
import json
import logging
import time
import uuid
from dataclasses import dataclass, field

from .llm import LLMClient, LLMError, build_messages, normalize_llm_output, retry_message
from .logging_setup import log
from .metrics import CACHE, REQUESTS, VALIDATION_ERRORS
from .ports import CameraRegistry, DraftCache, PlanPublisher, PlanRepository, RegistryError
from .validator import PlanValidator, ValidationIssue

logger = logging.getLogger("planner.service")

def normalize_query(q: str) -> str:
    """lowercase, trimmed, internal whitespace collapsed, trailing punctuation removed."""
    q = re.sub(r"\s+", " ", q.strip().lower())
    return re.sub(r"[\s.,;:!?]+$", "", q)


_CONTENT_KEYS = ("name", "query", "camera_ids", "steps")


class PlanInvalid(Exception):
    def __init__(self, errors: list[str]):
        super().__init__("; ".join(errors))
        self.errors = errors


@dataclass
class Activation:
    plan: dict
    warnings: list[str] = field(default_factory=list)


class PlannerService:
    def __init__(self, repo: PlanRepository, publisher: PlanPublisher, registry: CameraRegistry,
                 llm: LLMClient, validator: PlanValidator, tools: dict, max_retries: int = 2,
                 cache: DraftCache | None = None, model: str = "", llm_schema: dict | None = None):
        self.repo, self.publisher, self.registry = repo, publisher, registry
        self.llm, self.validator, self.tools = llm, validator, tools
        self.max_retries = max_retries
        self.cache, self.model = cache, model
        material = json.dumps(build_messages(tools, ""), sort_keys=True) + json.dumps(tools, sort_keys=True) \
            + json.dumps(llm_schema or {}, sort_keys=True)
        self._fingerprint = hashlib.sha256(material.encode()).hexdigest()
        self._lock = asyncio.Lock()  # one writer at a time (single replica)

    # ------------------------------------------------------------------ helpers
    def _count_issues(self, issues: list[ValidationIssue]) -> None:
        for i in issues:
            VALIDATION_ERRORS.labels(rule=i.rule).inc()

    def check(self, plan: dict) -> list[str]:
        issues = self.validator.validate(plan)
        self._count_issues(issues)
        return [str(i) for i in issues]

    # -------------------------------------------------------------------- draft
    def cache_key(self, query: str) -> tuple[str, str]:
        nq = normalize_query(query)
        return hashlib.sha256(f"{nq}|{self.model}|{self._fingerprint}".encode()).hexdigest(), nq

    def _from_draft(self, d: dict, query: str, camera_ids: list[str]) -> dict:
        return {"plan_id": str(uuid.uuid4()), "version": 1, "name": d["name"], "query": query[:1000],
                "camera_ids": list(camera_ids), "status": "draft", "created_at": int(time.time() * 1000),
                "steps": d["steps"]}

    async def recent_drafts(self, limit: int) -> list[dict]:
        return await self.cache.recent(limit) if self.cache else []

    async def draft(self, query: str, camera_ids: list[str], refresh: bool = False) -> dict:
        """Returns {plan, valid, errors, explanation, cached, cache_hits}. The cache lookup (one indexed key
        read) happens before any LLM work. Raises LLMError if the LLM is unreachable."""
        key = nq = None
        if self.cache:
            key, nq = self.cache_key(query)
            if refresh:
                CACHE.labels(result="refresh").inc()
            else:
                entry = await self.cache.get(key)
                if entry:
                    d = entry["draft"]
                    plan = self._from_draft(d, query, camera_ids)
                    if not self.validator.validate(plan):  # re-validate on hit
                        hits = await self.cache.touch(key)
                        CACHE.labels(result="hit").inc()
                        return {"plan": plan, "valid": True, "errors": [], "explanation": d.get("explanation", ""),
                                "cached": True, "cache_hits": hits}
                    await self.cache.evict(key)
                    CACHE.labels(result="evicted").inc()
                CACHE.labels(result="miss").inc()
        r = await self._draft_llm(query, camera_ids)
        r["cached"], r["cache_hits"] = False, 0
        if self.cache and r["valid"]:
            p = r["plan"]
            await self.cache.put(key, nq, query, {"name": p["name"], "explanation": r["explanation"],
                                                  "steps": p["steps"]})
        return r

    async def _draft_llm(self, query: str, camera_ids: list[str]) -> dict:
        """Returns {plan, valid, errors, explanation}. Raises LLMError if the LLM is unreachable."""
        messages = build_messages(self.tools, query)
        plan: dict | None = None
        explanation = ""
        errors: list[str] = []
        for attempt in range(1 + self.max_retries):
            try:
                raw = await self.llm.generate(messages)
            except LLMError:
                REQUESTS.labels(result="llm_error").inc()
                raise
            out = normalize_llm_output(raw, self.tools)
            explanation = out["explanation"]
            plan = {
                "plan_id": str(uuid.uuid4()),
                "version": 1,
                "name": out["name"] or query[:80],
                "query": query[:1000],
                "camera_ids": list(camera_ids),
                "status": "draft",
                "created_at": int(time.time() * 1000),
                "steps": out["steps"],
            }
            errors = self.check(plan)
            if not errors:
                REQUESTS.labels(result="ok").inc()
                log(logger, "draft ok", attempts=attempt + 1, steps=len(plan["steps"]))
                return {"plan": plan, "valid": True, "errors": [], "explanation": explanation}
            log(logger, "draft invalid", attempt=attempt + 1, errors=errors)
            messages = messages + [
                {"role": "assistant", "content": json.dumps({k: out[k] for k in ("name", "explanation", "steps")})},
                retry_message(errors),
            ]
        REQUESTS.labels(result="invalid").inc()
        return {"plan": plan, "valid": False, "errors": errors, "explanation": explanation}

    # ----------------------------------------------------------------- activate
    async def activate(self, plan: dict) -> Activation:
        errors = self.check(plan)
        if errors:
            REQUESTS.labels(result="invalid").inc()
            raise PlanInvalid(errors)
        async with self._lock:
            existing = await self.repo.get(plan["plan_id"])
            stored = dict(plan)
            stored["status"] = "active"
            if existing:
                stored["created_at"] = existing["created_at"]
                changed = any(existing.get(k) != stored.get(k) for k in _CONTENT_KEYS) or existing["status"] != "active"
                stored["version"] = existing["version"] + 1 if changed else existing["version"]
            # a camera belongs to exactly one active plan: move overlapping cameras out of other active plans
            moved = set(stored["camera_ids"])
            touched: list[dict] = []
            for other in await self.repo.list():
                if other["plan_id"] == stored["plan_id"] or other["status"] != "active":
                    continue
                overlap = moved & set(other["camera_ids"])
                if not overlap:
                    continue
                o = dict(other)
                o["camera_ids"] = [c for c in other["camera_ids"] if c not in overlap]
                o["version"] = other["version"] + 1
                if not o["camera_ids"]:
                    o["status"] = "disabled"
                touched.append(o)
            await self.repo.save_many([stored] + touched)  # one transaction
            for o in touched:
                await self.publisher.publish(o["plan_id"], o)
                log(logger, "plan lost cameras", plan_id=o["plan_id"], status=o["status"], version=o["version"])
            await self.publisher.publish(stored["plan_id"], stored)

            warnings: list[str] = []
            new_cams = set(stored["camera_ids"])
            old_cams = set(existing["camera_ids"]) if existing else set()
            for cam in sorted(new_cams):
                try:
                    await self.registry.set_plan(cam, stored["plan_id"])
                except RegistryError as e:
                    warnings.append(str(e))
            for cam in sorted(old_cams - new_cams):  # cameras dropped from the plan
                try:
                    await self.registry.set_plan(cam, None, only_if_plan=stored["plan_id"])
                except RegistryError as e:
                    warnings.append(str(e))
            if warnings:
                log(logger, "activation: registry warnings", level=logging.WARNING, plan_id=stored["plan_id"],
                    warnings=warnings)
            REQUESTS.labels(result="activated").inc()
            log(logger, "plan activated", plan_id=stored["plan_id"], version=stored["version"])
            return Activation(stored, warnings)

    async def republish_all(self) -> int:
        plans = await self.repo.list()
        for p in plans:
            await self.publisher.publish(p["plan_id"], p)
        log(logger, "republished plans on startup", count=len(plans))
        return len(plans)

    async def sync_camera(self, camera_id: str, plan_id: str | None) -> int:
        """Registry is the source of truth for camera->plan. Called for every camera.config event
        (plan_id=None for a tombstone or a camera without plan). Keeps plan.camera_ids in sync; persists and
        republishes (version bump) only the plans whose camera set actually changed. Returns #plans changed."""
        async with self._lock:
            touched: list[dict] = []
            for p in await self.repo.list():
                has = camera_id in p["camera_ids"]
                should = p["plan_id"] == plan_id
                if has == should:
                    continue
                q = dict(p)
                q["version"] = p["version"] + 1
                if should:
                    q["camera_ids"] = p["camera_ids"] + [camera_id]
                    q["status"] = "active"
                else:
                    q["camera_ids"] = [c for c in p["camera_ids"] if c != camera_id]
                    if not q["camera_ids"]:
                        q["status"] = "disabled"
                touched.append(q)
            if touched:
                await self.repo.save_many(touched)
                for q in touched:
                    await self.publisher.publish(q["plan_id"], q)
                    log(logger, "plan cameras synced from registry", plan_id=q["plan_id"], camera_id=camera_id,
                        version=q["version"], status=q["status"])
            return len(touched)

    # --------------------------------------------------------------- read/delete
    async def get(self, plan_id: str) -> dict | None:
        return await self.repo.get(plan_id)

    async def list(self) -> list[dict]:
        return await self.repo.list()

    async def delete(self, plan_id: str) -> list[str] | None:
        """Returns warnings list, or None if the plan does not exist."""
        async with self._lock:
            existing = await self.repo.get(plan_id)
            if not existing:
                REQUESTS.labels(result="not_found").inc()
                return None
            await self.publisher.publish(plan_id, None)  # tombstone first: a failed delete can simply be retried
            await self.repo.delete(plan_id)
            warnings: list[str] = []
            for cam in existing["camera_ids"]:
                try:
                    await self.registry.set_plan(cam, None, only_if_plan=plan_id)
                except RegistryError as e:
                    warnings.append(str(e))
            REQUESTS.labels(result="deleted").inc()
            log(logger, "plan deleted", plan_id=plan_id, warnings=warnings)
            return warnings
