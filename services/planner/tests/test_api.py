import httpx
import pytest

from app.llm import LLMError
from app.main import create_app
from app.service import PlannerService
from tests.conftest import make_plan
from tests.fakes import FakeLLM, bad_llm_output


def http(app):
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://t")


@pytest.fixture
async def client(service, tools):
    async with http(create_app(service=service, tools=tools)) as c:
        yield c


async def test_health_tools_metrics(client, tools):
    assert (await client.get("/health")).status_code == 200
    assert (await client.get("/api/tools")).json() == tools
    m = (await client.get("/metrics")).text
    assert "vp_planner_requests_total" in m and "vp_planner_llm_seconds" in m


async def test_draft_then_activate_flow(client, parts):
    d = (await client.post("/api/plans/draft", json={"query": "plates of trucks", "camera_ids": ["cam-001"]})).json()
    assert d["valid"] and d["plan"]["status"] == "draft"
    assert (await client.get("/api/plans")).json() == []  # draft is not stored
    r = await client.post("/api/plans", json=d["plan"])
    assert r.status_code == 200 and r.json()["status"] == "active"
    pid = r.json()["plan_id"]
    assert parts["reg"].cams["cam-001"] == pid
    assert (await client.get(f"/api/plans/{pid}")).json()["status"] == "active"
    assert len((await client.get("/api/plans")).json()) == 1
    assert (await client.delete(f"/api/plans/{pid}")).status_code == 200
    assert parts["reg"].cams["cam-001"] is None
    assert (await client.get(f"/api/plans/{pid}")).status_code == 404
    assert (await client.delete(f"/api/plans/{pid}")).status_code == 404


async def test_activate_invalid_422(client):
    r = await client.post("/api/plans", json=make_plan([{"id": "s1", "tool": "nope", "mode": "inline"}]))
    assert r.status_code == 422 and r.json()["valid"] is False and r.json()["errors"]


async def test_draft_invalid_after_retries(parts, validator, tools):
    svc = PlannerService(parts["repo"], parts["pub"], parts["reg"], FakeLLM(bad_llm_output()), validator, tools)
    async with http(create_app(service=svc, tools=tools)) as c:
        d = (await c.post("/api/plans/draft", json={"query": "x"})).json()
    assert d["valid"] is False and d["errors"]


async def test_draft_llm_down_503(parts, validator, tools):
    svc = PlannerService(parts["repo"], parts["pub"], parts["reg"], FakeLLM(LLMError("down")), validator, tools)
    async with http(create_app(service=svc, tools=tools)) as c:
        r = await c.post("/api/plans/draft", json={"query": "x"})
    assert r.status_code == 503 and r.json()["valid"] is False


async def test_bad_bodies(client):
    assert (await client.post("/api/plans/draft", json={"query": ""})).status_code == 422
    assert (await client.post("/api/plans", content=b"{not json")).status_code == 400


async def test_handwritten_unsatisfiable_when_still_rejected(client):
    plan = make_plan([{"id": "s1", "tool": "detect_objects", "mode": "inline", "params": {"classes": "car"},
                       "when": {"type": "found", "class": "car"}}])
    r = await client.post("/api/plans", json=plan)
    assert r.status_code == 422 and any("condition_source" in e for e in r.json()["errors"])
