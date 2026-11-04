import pytest

from app.llm import LLMError
from app.service import PlanInvalid, PlannerService
from tests.conftest import make_plan
from tests.fakes import FakeLLM, bad_llm_output, good_llm_output

PID = "11111111-1111-4111-8111-111111111111"
STEPS = [{"id": "s1", "tool": "detect_objects", "mode": "inline", "params": {"classes": "truck"}}]


def svc_with(parts, validator, tools, llm):
    return PlannerService(parts["repo"], parts["pub"], parts["reg"], llm, validator, tools, max_retries=2)


async def test_draft_valid_not_stored(service, parts):
    r = await service.draft("read truck plates", ["cam-001"])
    assert r["valid"] and r["errors"] == []
    assert r["plan"]["status"] == "draft" and r["plan"]["camera_ids"] == ["cam-001"]
    assert r["plan"]["query"] == "read truck plates" and r["plan"]["version"] == 1
    assert r["explanation"]
    assert parts["repo"].data == {} and parts["pub"].sent == []
    assert all(v is None for v in parts["reg"].cams.values())


async def test_draft_retry_feeds_errors_back(parts, validator, tools):
    llm = FakeLLM(bad_llm_output(), good_llm_output())
    r = await svc_with(parts, validator, tools, llm).draft("q", [])
    assert r["valid"] and len(llm.calls) == 2
    last = llm.calls[1][-1]["content"]
    assert "plate_gating" in last and "step s2" in last
    assert llm.calls[1][-2]["role"] == "assistant"


async def test_draft_gives_up_after_two_retries(parts, validator, tools):
    llm = FakeLLM(bad_llm_output())
    r = await svc_with(parts, validator, tools, llm).draft("q", [])
    assert not r["valid"] and len(llm.calls) == 3
    assert any("plate_gating" in e for e in r["errors"])
    assert parts["repo"].data == {}


async def test_draft_llm_error_propagates(parts, validator, tools):
    with pytest.raises(LLMError):
        await svc_with(parts, validator, tools, FakeLLM(LLMError("down"))).draft("q", [])


async def test_activate_stores_publishes_sets_cameras(service, parts):
    act = await service.activate(make_plan(STEPS, camera_ids=["cam-001", "cam-002"]))
    assert act.plan["status"] == "active" and act.plan["version"] == 1 and not act.warnings
    assert (await parts["repo"].get(PID))["status"] == "active"
    assert parts["pub"].sent == [(PID, act.plan)]
    assert parts["reg"].cams == {"cam-001": PID, "cam-002": PID, "cam-003": None}


async def test_activate_invalid_rejected_nothing_stored(service, parts):
    bad = make_plan([{"id": "s1", "tool": "read_plate", "mode": "inline"},
                     {"id": "s2", "tool": "read_text", "mode": "queue"},
                     {"id": "s3", "tool": "detect_faces", "mode": "inline"}])
    with pytest.raises(PlanInvalid) as e:
        await service.activate(bad)
    assert any("face_gating" in x for x in e.value.errors)
    assert parts["repo"].data == {} and parts["pub"].sent == []


async def test_version_bump_only_on_change(service, parts):
    a1 = await service.activate(make_plan(STEPS))
    a2 = await service.activate(make_plan(STEPS))
    assert a1.plan["version"] == 1 and a2.plan["version"] == 1
    changed = [{**STEPS[0], "params": {"classes": "car"}}]
    a3 = await service.activate(make_plan(changed))
    assert a3.plan["version"] == 2
    a4 = await service.activate(make_plan(changed, camera_ids=["cam-001", "cam-002"]))
    assert a4.plan["version"] == 3
    assert a4.plan["created_at"] == a1.plan["created_at"]
    assert [p["version"] for _, p in parts["pub"].sent] == [1, 1, 2, 3]


async def test_camera_removed_from_plan_is_cleared(service, parts):
    await service.activate(make_plan(STEPS, camera_ids=["cam-001", "cam-002"]))
    parts["reg"].cams["cam-002"] = "other-plan"  # someone else took cam-002 meanwhile
    await service.activate(make_plan(STEPS, camera_ids=["cam-003"]))
    assert parts["reg"].cams == {"cam-001": None, "cam-002": "other-plan", "cam-003": PID}


async def test_unknown_camera_is_warning_not_failure(service, parts):
    act = await service.activate(make_plan(STEPS, camera_ids=["cam-001", "ghost"]))
    assert len(act.warnings) == 1 and "ghost" in act.warnings[0]
    assert parts["reg"].cams["cam-001"] == PID


async def test_delete_tombstones_and_clears_cameras(service, parts):
    await service.activate(make_plan(STEPS, camera_ids=["cam-001", "cam-002"]))
    parts["reg"].cams["cam-002"] = "other-plan"
    assert await service.delete(PID) == []
    assert parts["pub"].sent[-1] == (PID, None)
    assert parts["repo"].data == {}
    assert parts["reg"].cams["cam-001"] is None and parts["reg"].cams["cam-002"] == "other-plan"
    assert await service.delete(PID) is None


PID_A = "aaaaaaaa-1111-4111-8111-111111111111"


async def test_move_cameras_between_plans(service, parts):
    await service.activate(make_plan(STEPS, plan_id=PID_A, camera_ids=["cam-001", "cam-002"]))
    await service.activate(make_plan(STEPS, camera_ids=["cam-002", "cam-003"]))
    a = await parts["repo"].get(PID_A)
    assert a["camera_ids"] == ["cam-001"] and a["version"] == 2 and a["status"] == "active"
    assert parts["pub"].sent[-2][0] == PID_A and parts["pub"].sent[-2][1]["version"] == 2
    assert parts["pub"].sent[-1][0] == PID
    assert parts["reg"].cams["cam-002"] == PID


async def test_empty_plan_becomes_disabled_not_tombstone(service, parts):
    await service.activate(make_plan(STEPS, plan_id=PID_A, camera_ids=["cam-001"]))
    await service.activate(make_plan(STEPS, camera_ids=["cam-001"]))
    a = await parts["repo"].get(PID_A)
    assert a["status"] == "disabled" and a["camera_ids"] == [] and a["version"] == 2
    assert (PID_A, a) in parts["pub"].sent and all(p is not None for _, p in parts["pub"].sent)
    # a disabled plan is not touched by later activations
    await service.activate(make_plan(STEPS, plan_id="bbbbbbbb-1111-4111-8111-111111111111", camera_ids=["cam-001"]))
    assert (await parts["repo"].get(PID_A))["version"] == 2


async def test_no_overlap_leaves_other_plan_unchanged(service, parts):
    await service.activate(make_plan(STEPS, plan_id=PID_A, camera_ids=["cam-001"]))
    n = len(parts["pub"].sent)
    await service.activate(make_plan(STEPS, camera_ids=["cam-002"]))
    a = await parts["repo"].get(PID_A)
    assert a["version"] == 1 and a["camera_ids"] == ["cam-001"]
    assert len(parts["pub"].sent) == n + 1


async def test_startup_republish_all(service, parts):
    await service.activate(make_plan(STEPS, plan_id=PID_A, camera_ids=["cam-001"]))
    await service.activate(make_plan(STEPS, camera_ids=["cam-002"]))
    parts["pub"].sent.clear()
    assert await service.republish_all() == 2
    assert {pid for pid, p in parts["pub"].sent if p is not None} == {PID, PID_A}
