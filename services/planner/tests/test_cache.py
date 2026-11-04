import httpx

from app.main import create_app
from app.service import PlannerService, normalize_query
from tests.fakes import FakeLLM, MemCache, bad_llm_output, good_llm_output


def make(parts, validator, tools, cache=None, model="m", llm=None, schema=None):
    return PlannerService(parts["repo"], parts["pub"], parts["reg"], llm or parts["llm"], validator, tools,
                          cache=cache if cache is not None else MemCache(), model=model, llm_schema=schema or {})


def test_normalize():
    assert normalize_query("  Watch   for CARS\t and trucks?! ") == "watch for cars and trucks"


async def test_variants_hit_same_entry(parts, validator, tools):
    s = make(parts, validator, tools)
    r1 = await s.draft("Watch for cars.", ["cam-001"])
    assert r1["valid"] and r1["cached"] is False and r1["cache_hits"] == 0
    r2 = await s.draft("  watch   FOR cars?? ", ["cam-002"])
    assert r2["cached"] is True and r2["cache_hits"] == 1
    assert len(parts["llm"].calls) == 1
    assert r2["plan"]["camera_ids"] == ["cam-002"] and r2["plan"]["plan_id"] != r1["plan"]["plan_id"]
    assert r2["plan"]["steps"] == r1["plan"]["steps"] and r2["explanation"] == r1["explanation"]
    assert (await s.draft("watch for cars", []))["cache_hits"] == 2


async def test_model_prompt_tools_change_misses(parts, validator, tools):
    cache = MemCache()
    base = make(parts, validator, tools, cache)
    await base.draft("q", [])
    assert (await make(parts, validator, tools, cache, model="other").draft("q", []))["cached"] is False
    assert (await make(parts, validator, tools, cache, schema={"x": 1}).draft("q", []))["cached"] is False
    tools2 = {**tools, "tools": [{**tools["tools"][0], "description": "changed"}] + tools["tools"][1:]}
    assert (await make(parts, validator, tools2, cache).draft("q", []))["cached"] is False
    assert (await base.draft("q", []))["cached"] is True


async def test_invalid_drafts_not_stored(parts, validator, tools):
    cache = MemCache()
    s = make(parts, validator, tools, cache, llm=FakeLLM(bad_llm_output()))
    assert not (await s.draft("q", []))["valid"]
    assert cache.d == {}


async def test_refresh_replaces(parts, validator, tools):
    cache = MemCache()
    s = make(parts, validator, tools, cache)
    await s.draft("q", [])
    await s.draft("q", [])
    new = good_llm_output()
    new["name"] = "Replaced"
    parts["llm"].outputs = [new]
    r = await s.draft("q", [], refresh=True)
    assert r["cached"] is False and r["plan"]["name"] == "Replaced"
    r2 = await s.draft("q", [])
    assert r2["cached"] is True and r2["plan"]["name"] == "Replaced" and r2["cache_hits"] == 1


async def test_invalid_cached_entry_evicted(parts, validator, tools):
    cache = MemCache()
    s = make(parts, validator, tools, cache)
    await s.draft("q", [])
    key, _ = s.cache_key("q")
    cache.d[key]["draft"]["steps"] = [{"id": "s1", "tool": "read_plate", "mode": "inline"},
                                      {"id": "s2", "tool": "read_text", "mode": "queue"}]  # ungated plate
    r = await s.draft("q", [])
    assert r["cached"] is False and r["valid"] and len(parts["llm"].calls) == 2
    assert cache.d[key]["draft"]["steps"][0]["tool"] == "detect_objects"  # re-stored fresh


async def test_recent_distinct_and_ordered(parts, validator, tools):
    s = make(parts, validator, tools)
    for q in ("one", "two", "three"):
        await s.draft(q, [])
    await s.draft("ONE!", [])  # hit moves "one" to the front
    rec = await s.recent_drafts(10)
    assert [r["query"] for r in rec] == ["one", "three", "two"]
    assert rec[0]["hits"] == 1
    assert len(await s.recent_drafts(2)) == 2


async def test_api_cached_fields_refresh_and_recent(parts, validator, tools):
    s = make(parts, validator, tools)
    app = create_app(service=s, tools=tools)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://t") as c:
        a = (await c.post("/api/plans/draft", json={"query": "hello cars"})).json()
        b = (await c.post("/api/plans/draft", json={"query": "Hello cars."})).json()
        d = (await c.post("/api/plans/draft?refresh=true", json={"query": "hello cars"})).json()
        rec = (await c.get("/api/plans/drafts/recent?limit=5")).json()
        m = (await c.get("/metrics")).text
    assert (a["cached"], a["cache_hits"]) == (False, 0) and (b["cached"], b["cache_hits"]) == (True, 1)
    assert d["cached"] is False
    assert [r["query"] for r in rec] == ["hello cars"]
    assert 'vp_planner_cache_total{result="hit"}' in m and 'vp_planner_cache_total{result="refresh"}' in m
