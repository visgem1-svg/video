import asyncio

import pytest
from fastapi.testclient import TestClient

from controller.main import create_app
from controller.policy import compute_desired, validate_policy
from conftest import read_sd

P = dict(worker_capacity=12, target_utilization=0.8, min_workers=2, max_workers=6)


def run(coro):
    return asyncio.run(coro)


def cams(n, disabled=0):
    return [{"camera_id": f"c{i}", "enabled": i >= disabled} for i in range(n)]


# ---------------- desired
def test_desired_formula():
    assert compute_desired(cams(0), [], P)[0] == 2                    # min
    w = [{"worker_id": "a", "assigned": [f"c{i}" for i in range(40)]}]
    assert compute_desired(cams(40), w, P)[0] == 5                    # ceil(40/9.6)
    assert compute_desired(cams(400), [], P)[0] == 6                  # max
    assert compute_desired(cams(20, disabled=10), [{"worker_id": "a", "assigned": [f"c{i}" for i in range(10, 20)]}], P)[0] == 2


def test_desired_uses_weighted_load():
    ids = [f"c{i}" for i in range(60)]
    w = [{"worker_id": "a", "assigned": ids[:30], "load": 12.5}, {"worker_id": "b", "assigned": ids[30:], "load": 6.0},
         {"worker_id": "x", "assigned": [], "load": 99, "state": "dead"}]    # dead workers ignored
    d, info = compute_desired(cams(60), w, P)
    assert d == 2 and info["load"] == 18.5 and info["base"] == 2           # ceil(18.5/9.6), not ceil(60/9.6)=7->6
    w[0]["load"] = 9.6
    w[1]["load"] = 0.0
    assert compute_desired(cams(60), w, P)[1]["base"] == 1                 # exactly one worker's worth: no float +1
    w[0]["load"], w[1]["load"] = 9.7, 0
    assert compute_desired(cams(60), w, P)[1]["base"] == 2


def test_desired_falls_back_to_camera_count_without_load():
    ids = [f"c{i}" for i in range(40)]
    assert compute_desired(cams(40), [{"worker_id": "a", "assigned": ids}], P)[0] == 5
    w = [{"worker_id": "a", "assigned": ids, "load": None}, {"worker_id": "b", "assigned": [], "load": "n/a"}]
    assert compute_desired(cams(40), w, P)[0] == 5
    # one worker reporting is enough to switch to load
    w = [{"worker_id": "a", "assigned": ids, "load": 4.0}]
    assert compute_desired(cams(40), w, P)[0] == 2


def test_desired_unassigned_plus_one():
    w = [{"worker_id": "a", "assigned": ["c0", "c1"]}]
    d, info = compute_desired(cams(3), w, P)
    assert info["unassigned"] == 1 and d == 2                         # base 1 + 1 -> 2
    d2, _ = compute_desired(cams(20), [{"worker_id": "a", "assigned": [f"c{i}" for i in range(19)]}], P)
    assert d2 == 4                                                    # ceil(20/9.6)=3, +1


# ---------------- scale up / down sequence
def setup_load(reg, n=30, workers=("fx-1", "fx-2")):
    reg.set_cams(n)
    ids = [c["camera_id"] for c in reg.cams]
    for i, w in enumerate(workers):
        reg.assign(w, ids[i::len(workers)], managed_by="static")


def test_scale_up_creates_one_container_and_cooldown(env):
    c, reg, rt, clock, s = env
    setup_load(reg, 40)                      # desired 5, current 2
    run(c.tick())
    assert rt.created == ["vp-fx-auto-1"]    # one action per tick
    run(c.tick())
    assert rt.created == ["vp-fx-auto-1"]    # cooldown
    clock.advance(61)
    run(c.tick())
    assert rt.created == ["vp-fx-auto-1", "vp-fx-auto-2"]
    assert c.snapshot()["events"][0]["action"] == "scale_up"


def test_max_not_exceeded(env):
    c, reg, rt, clock, s = env
    c.set_policy({"cooldown_s": 0, "max_workers": 3})
    setup_load(reg, 400)
    for _ in range(6):
        run(c.tick())
    assert len(rt.created) == 1              # 2 static + 1 = max 3


def test_starting_containers_not_duplicated(env):
    c, reg, rt, clock, s = env
    c.set_policy({"cooldown_s": 0})
    setup_load(reg, 30)                      # desired 4 (ceil(30/9.6)=4), current 2
    run(c.tick()); run(c.tick()); run(c.tick())
    assert len(rt.created) == 2              # container counted although not yet heartbeating


def test_scale_down_drain_wait_remove(env):
    c, reg, rt, clock, s = env
    c.set_policy({"cooldown_s": 0})
    reg.set_cams(10)
    ids = [x["camera_id"] for x in reg.cams]
    reg.assign("fx-1", ids[:5], managed_by="static")
    reg.assign("fx-2", ids[5:], managed_by="static")
    rt.add("vp-fx-auto-1", "fx-auto-1"); rt.add("vp-fx-auto-2", "fx-auto-2")
    reg.assign("fx-auto-1", ["x1", "x2"], managed_by="controller")
    reg.assign("fx-auto-2", ["x3"], managed_by="controller")
    reg.cams += [{"camera_id": n, "enabled": True} for n in ("x1", "x2", "x3")]
    # 13 cams -> desired 2; current 4 -> drain least-loaded managed (fx-auto-2)
    run(c.tick())
    assert reg.calls == [("drain", "fx-auto-2")] and rt.removed == []
    run(c.tick())                            # still owns cameras -> wait, no new drain
    assert reg.calls == [("drain", "fx-auto-2")] and rt.removed == []
    reg.wmap["fx-auto-2"]["assigned"] = []   # registry moved the cameras away
    reg.wmap["fx-auto-1"]["assigned"] += ["x3"]
    run(c.tick())
    assert rt.removed == ["vp-fx-auto-2"]
    run(c.tick())                            # now 3 active (fx-1, fx-2, auto-1) > 2, but cooldown...
    c.set_policy({"cooldown_s": 0})
    run(c.tick())
    assert ("drain", "fx-auto-1") in reg.calls
    acts = [e["action"] for e in c.snapshot()["events"]]
    assert acts.index("drain_start") < acts.index("scale_down")


def test_never_below_min_and_static_untouched(env):
    c, reg, rt, clock, s = env
    c.set_policy({"cooldown_s": 0})
    reg.set_cams(2)
    reg.assign("fx-1", ["cam-000"], managed_by="static")
    reg.assign("fx-2", ["cam-001"], managed_by="static")
    for _ in range(3):
        run(c.tick())
    assert reg.calls == [] and rt.removed == []
    # one managed worker + one static = min 2: managed must not be drained
    rt.add("vp-fx-auto-1", "fx-auto-1")
    reg.wmap.pop("fx-2")
    reg.assign("fx-auto-1", [], managed_by="controller")
    run(c.tick())
    assert reg.calls == []


def test_drain_timeout_cancels(env):
    c, reg, rt, clock, s = env
    c.set_policy({"cooldown_s": 0, "drain_timeout_s": 120})
    reg.set_cams(4)
    reg.assign("fx-1", ["cam-000", "cam-001"], managed_by="static")
    reg.assign("fx-2", ["cam-002"], managed_by="static")
    rt.add("vp-fx-auto-1", "fx-auto-1")
    reg.assign("fx-auto-1", ["cam-003"], managed_by="controller")
    run(c.tick())
    assert reg.calls == [("drain", "fx-auto-1")]
    clock.advance(121)
    run(c.tick())
    assert reg.calls[-1] == ("undrain", "fx-auto-1") and rt.removed == []
    assert c.snapshot()["events"][-1]["action"] == "drain_timeout"


def test_drain_cancelled_when_load_returns(env):
    c, reg, rt, clock, s = env
    c.set_policy({"cooldown_s": 0})
    reg.set_cams(4)
    reg.assign("fx-1", ["cam-000", "cam-001"], managed_by="static")
    reg.assign("fx-2", ["cam-002"], managed_by="static")
    rt.add("vp-fx-auto-1", "fx-auto-1")
    reg.assign("fx-auto-1", ["cam-003"], managed_by="controller")
    run(c.tick())                            # drain started
    reg.set_cams(60)                         # load spike
    run(c.tick())
    assert reg.calls[-1] == ("undrain", "fx-auto-1")


# ---------------- adoption / dead
def test_adoption_no_duplicates_after_restart(env):
    c, reg, rt, clock, s = env
    setup_load(reg, 30)                      # desired 4
    run(c.tick())
    assert len(rt.created) == 1
    from controller.core import Controller
    c2 = Controller(s, reg, rt, clock=clock)  # simulated restart, same docker state
    run(c2.tick())                           # no cooldown memory, but must see container: 3 of 4 -> create 1 more
    assert rt.created == ["vp-fx-auto-1", "vp-fx-auto-2"]
    assert len(set(rt.containers)) == 2      # no name clash / duplicate


def test_adopt_resumes_drain(env):
    c, reg, rt, clock, s = env
    reg.set_cams(2)
    reg.assign("fx-1", ["cam-000"], managed_by="static"); reg.assign("fx-2", ["cam-001"], managed_by="static")
    rt.add("vp-fx-auto-1", "fx-auto-1")
    reg.assign("fx-auto-1", [], managed_by="controller", draining=True, state="draining")
    run(c.tick())
    assert rt.removed == ["vp-fx-auto-1"]


def test_dead_container_removed_and_replaced(env):
    c, reg, rt, clock, s = env
    c.set_policy({"cooldown_s": 0})
    setup_load(reg, 30)
    rt.add("vp-fx-auto-1", "fx-auto-1", state="exited")
    run(c.tick())
    assert rt.removed == ["vp-fx-auto-1"]
    assert rt.created == ["vp-fx-auto-1"]    # replaced in the same tick (desired 4 > current 2)
    assert rt.containers["vp-fx-auto-1"]["state"] == "running"


# ---------------- manual mode
def test_manual_mode_scale(env):
    c, reg, rt, clock, s = env
    c.set_policy({"cooldown_s": 0})
    setup_load(reg, 2)
    c.set_mode("manual")
    with pytest.raises(ValueError):
        c.scale(99)
    c.scale(4)
    run(c.tick()); run(c.tick()); run(c.tick())
    assert len(rt.created) == 2              # auto would say 2
    c.scale(2)
    run(c.tick())
    assert reg.calls and reg.calls[0][0] == "drain"
    c.set_mode("auto")
    with pytest.raises(PermissionError):
        c.scale(3)


# ---------------- file_sd
def test_file_sd_content_and_atomic(env):
    c, reg, rt, clock, s = env
    setup_load(reg, 40)
    run(c.tick())
    sd = read_sd(s)
    assert sd == [{"targets": ["10.0.1.1:9101"], "labels": {"worker_id": "fx-auto-1", "managed_by": "worker-controller"}}]
    rt.add("vp-fx-auto-9", "fx-auto-9", ip="")
    run(c.tick())
    assert {t["targets"][0] for t in read_sd(s)} == {"10.0.1.1:9101", "vp-fx-auto-9:9101"}
    import os
    assert not os.path.exists(s.file_sd_path + ".tmp")


# ---------------- policy + API
def test_policy_validation():
    cur = dict(P, cooldown_s=60, drain_timeout_s=120)
    assert validate_policy(cur, {"max_workers": 8})["max_workers"] == 8
    for bad in ({"min_workers": 9}, {"target_utilization": 1.5}, {"foo": 1}, {"worker_capacity": "x"}, {}, {"min_workers": 1.5}):
        with pytest.raises(ValueError):
            validate_policy(cur, bad)


def test_http_api(env):
    c, reg, rt, clock, s = env
    setup_load(reg, 40)
    run(c.tick())
    with TestClient(create_app(c, run_loop=False)) as t:
        assert t.get("/health").status_code == 200
        j = t.get("/api/autoscaler").json()
        assert j["mode"] == "auto" and j["desired"] == 5 and j["current"] == 3
        assert any(w["container"] and w["container"]["ip"] for w in j["workers"])
        assert t.put("/api/autoscaler/policy", json={"max_workers": 0}).status_code == 422
        assert t.put("/api/autoscaler/policy", json={"max_workers": 8}).json()["max_workers"] == 8
        assert t.post("/api/autoscaler/scale", json={"workers": 3}).status_code == 409
        assert t.put("/api/autoscaler/mode", json={"mode": "bogus"}).status_code == 422
        assert t.put("/api/autoscaler/mode", json={"mode": "manual"}).json() == {"mode": "manual"}
        assert t.post("/api/autoscaler/scale", json={"workers": 3}).status_code == 200
        m = t.get("/metrics").text
        assert 'vp_controller_decisions_total{action="scale_up"}' in m
        assert "vp_controller_desired_workers" in m and 'vp_controller_workers{state="live"}' in m


def test_registry_down_is_survivable(env):
    c, reg, rt, clock, s = env

    async def boom():
        raise RuntimeError("registry down")
    reg.cameras = boom
    run(c.tick())
    assert c.snapshot()["last_error"] and rt.created == []
