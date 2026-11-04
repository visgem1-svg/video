"""FastAPI entry point (port 8010)."""
from __future__ import annotations

import asyncio
import json
import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel, Field

from .config import Settings
from .llm import LLMError, OllamaClient, build_llm_schema
from .logging_setup import log, setup_logging
from .metrics import CONTENT_TYPE_LATEST, REGISTRY, REQUESTS, generate_latest
from .service import PlanInvalid, PlannerService
from .validator import PlanValidator

logger = logging.getLogger("planner")


class DraftRequest(BaseModel):
    query: str = Field(min_length=1, max_length=1000)
    camera_ids: list[str] = Field(default_factory=list)


def create_app(service: PlannerService | None = None, tools: dict | None = None,
               settings: Settings | None = None) -> FastAPI:
    """With `service` given (tests) no infrastructure is started; otherwise lifespan wires real adapters."""
    state: dict = {"service": service, "tools": tools, "ready": service is not None, "closers": []}

    async def _init_real() -> None:
        from .adapters import HttpCameraRegistry, KafkaPlanPublisher, CameraConfigConsumer, PgDraftCache, PgPlanRepository
        st = settings or Settings.from_env()
        validator = PlanValidator.from_dir(st.contracts_dir)
        tools_json = json.loads((st.contracts_dir / "tools.json").read_text(encoding="utf-8"))
        state["tools"] = tools_json
        llm = OllamaClient(st.ollama_url, st.ollama_model, build_llm_schema(validator.schema, tools_json),
                           timeout_s=st.llm_timeout_s)
        registry = HttpCameraRegistry(st.registry_url, st.registry_timeout_s)
        state["closers"] += [llm.aclose, registry.aclose]
        repo = pub = None
        for attempt in range(60):  # infra may still be coming up
            try:
                if repo is None:
                    repo = await PgPlanRepository.connect(st.database_url)
                    state["closers"].append(repo.close)
                if pub is None:
                    pub = KafkaPlanPublisher(st.kafka_bootstrap, st.plans_topic)
                    await pub.start()
                    state["closers"].append(pub.stop)
                break
            except Exception as e:  # noqa: BLE001
                log(logger, "dependency not ready, retrying", level=logging.WARNING, attempt=attempt, error=str(e))
                if pub is not None and not pub.started:
                    pub = None
                await asyncio.sleep(3)
        else:
            log(logger, "giving up on dependencies", level=logging.ERROR)
            return
        state["service"] = PlannerService(repo, pub, registry, llm, validator, tools_json, st.llm_max_retries,
                                          cache=PgDraftCache(repo.pool), model=st.ollama_model,
                                          llm_schema=llm.schema)
        try:
            await state["service"].republish_all()
        except Exception as e:  # noqa: BLE001
            log(logger, "startup republish failed", level=logging.ERROR, error=str(e))
        sync = CameraConfigConsumer(st.kafka_bootstrap, state["service"].sync_camera)
        sync.start()
        state["closers"].append(sync.stop)
        state["ready"] = True
        log(logger, "planner ready")

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        task = None
        if service is None:
            setup_logging((settings or Settings.from_env()).log_level)
            task = asyncio.create_task(_init_real())
        yield
        if task:
            task.cancel()
        for c in reversed(state["closers"]):
            try:
                await c()
            except Exception:  # noqa: BLE001
                pass

    app = FastAPI(title="planner", lifespan=lifespan)

    def svc() -> PlannerService | None:
        return state["service"] if state["ready"] else None

    def not_ready() -> JSONResponse:
        return JSONResponse({"error": "planner not ready (database/kafka starting)"}, status_code=503)

    @app.get("/health")
    async def health():
        if not state["ready"]:
            return JSONResponse({"status": "starting"}, status_code=503)
        return {"status": "ok"}

    @app.get("/metrics")
    async def metrics():
        return Response(generate_latest(REGISTRY), media_type=CONTENT_TYPE_LATEST)

    @app.get("/api/tools")
    async def get_tools():
        return state["tools"] or json.loads((Settings.from_env().contracts_dir / "tools.json").read_text("utf-8"))

    @app.post("/api/plans/draft")
    async def draft(req: DraftRequest, refresh: bool = False):
        s = svc()
        if not s:
            return not_ready()
        try:
            return await s.draft(req.query, req.camera_ids, refresh=refresh)
        except LLMError as e:
            log(logger, "llm failure", level=logging.ERROR, error=str(e))
            return JSONResponse({"plan": None, "valid": False, "errors": [str(e)], "explanation": ""},
                                status_code=503)

    @app.get("/api/plans/drafts/recent")
    async def recent_drafts(limit: int = 10):
        s = svc()
        if not s:
            return not_ready()
        return await s.recent_drafts(max(1, min(limit, 100)))

    @app.post("/api/plans")
    async def create_plan(request: Request):
        s = svc()
        if not s:
            return not_ready()
        try:
            body = await request.json()
        except ValueError:
            return JSONResponse({"valid": False, "errors": ["body is not valid JSON"]}, status_code=400)
        try:
            act = await s.activate(body)
        except PlanInvalid as e:
            return JSONResponse({"valid": False, "errors": e.errors}, status_code=422)
        except Exception as e:  # kafka / db failure
            REQUESTS.labels(result="error").inc()
            log(logger, "activation failed", level=logging.ERROR, error=str(e))
            return JSONResponse({"error": f"activation failed: {type(e).__name__}: {e}"}, status_code=503)
        headers = {"X-Planner-Warnings": json.dumps(act.warnings)} if act.warnings else None
        return JSONResponse(act.plan, headers=headers)

    @app.get("/api/plans")
    async def list_plans():
        s = svc()
        return await s.list() if s else not_ready()

    @app.get("/api/plans/{plan_id}")
    async def get_plan(plan_id: str):
        s = svc()
        if not s:
            return not_ready()
        p = await s.get(plan_id)
        return p if p else JSONResponse({"error": "plan not found"}, status_code=404)

    @app.delete("/api/plans/{plan_id}")
    async def delete_plan(plan_id: str):
        s = svc()
        if not s:
            return not_ready()
        try:
            warnings = await s.delete(plan_id)
        except Exception as e:  # noqa: BLE001
            REQUESTS.labels(result="error").inc()
            return JSONResponse({"error": f"delete failed: {type(e).__name__}: {e}"}, status_code=503)
        if warnings is None:
            return JSONResponse({"error": "plan not found"}, status_code=404)
        return JSONResponse(status_code=200, content={"deleted": plan_id, "warnings": warnings})

    return app


def app_factory() -> FastAPI:
    return create_app()
