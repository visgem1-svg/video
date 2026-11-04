from tests.conftest import make_plan

PID = "11111111-1111-4111-8111-111111111111"
PID_B = "bbbbbbbb-1111-4111-8111-111111111111"
STEPS = [{"id": "s1", "tool": "detect_objects", "mode": "inline", "params": {"classes": "truck"}}]


async def test_camera_added_to_plan_elsewhere(service, parts):
    await service.activate(make_plan(STEPS, camera_ids=["cam-001"]))
    n = len(parts["pub"].sent)
    assert await service.sync_camera("cam-002", PID) == 1
    p = await parts["repo"].get(PID)
    assert p["camera_ids"] == ["cam-001", "cam-002"] and p["version"] == 2
    assert parts["pub"].sent[-1] == (PID, p) and len(parts["pub"].sent) == n + 1


async def test_no_change_no_publish(service, parts):
    await service.activate(make_plan(STEPS, camera_ids=["cam-001"]))
    n = len(parts["pub"].sent)
    assert await service.sync_camera("cam-001", PID) == 0  # already in sync (e.g. our own activation echo)
    assert await service.sync_camera("cam-009", None) == 0
    assert await service.sync_camera("cam-009", "default") == 0
    assert len(parts["pub"].sent) == n and (await parts["repo"].get(PID))["version"] == 1


async def test_camera_moved_to_other_plan_or_cleared(service, parts):
    await service.activate(make_plan(STEPS, camera_ids=["cam-001", "cam-002"]))
    await service.activate(make_plan(STEPS, plan_id=PID_B, camera_ids=["cam-003"]))
    assert await service.sync_camera("cam-001", PID_B) == 2  # leaves A, joins B
    a, b = await parts["repo"].get(PID), await parts["repo"].get(PID_B)
    assert a["camera_ids"] == ["cam-002"] and a["version"] == 2
    assert b["camera_ids"] == ["cam-003", "cam-001"] and b["version"] == 2
    assert await service.sync_camera("cam-002", None) == 1  # plan_id cleared in registry
    assert (await parts["repo"].get(PID))["status"] == "disabled"


async def test_deleted_camera_removed_and_empty_plan_disabled(service, parts):
    await service.activate(make_plan(STEPS, camera_ids=["cam-001"]))
    assert await service.sync_camera("cam-001", None) == 1  # tombstone
    p = await parts["repo"].get(PID)
    assert p["camera_ids"] == [] and p["status"] == "disabled" and p["version"] == 2
    assert parts["pub"].sent[-1][1] is not None  # published, not a tombstone


async def test_camera_pointing_to_disabled_plan_reactivates_it(service, parts):
    await service.activate(make_plan(STEPS, camera_ids=["cam-001"]))
    await service.sync_camera("cam-001", None)
    await service.sync_camera("cam-002", PID)
    p = await parts["repo"].get(PID)
    assert p["status"] == "active" and p["camera_ids"] == ["cam-002"] and p["version"] == 3
