"""Contract 9.8: demand-driven extraction (idle = keyframes only, coalesced mode switches)."""
import asyncio
import time

from conftest import FakeBus, FakeSource, fast_settings, make_jpeg, wait_for
from extractor.camera_worker import CameraWorker
from extractor.config import Settings
from extractor.ffmpeg_reader import build_command
from extractor.metrics import Metrics
from extractor.models import CameraSpec
from extractor.publisher import FramePublisher
from extractor.state import Backoff, CamState
from test_manager import cfg, make


def spec_of(**extract):
    d = cfg("a", fps=3)
    if extract:
        d["extract"] = extract
    return CameraSpec.from_config(d)


def vf(cmd):
    return cmd[cmd.index("-vf") + 1]


def test_spec_parsing_and_backward_compat():
    s = spec_of()  # no `extract` -> exactly today
    assert (s.mode, s.target_fps, s.extract) == ("full", 3.0, ("full", 3.0))
    s = spec_of(mode="idle", fps=0.2)
    assert (s.mode, s.target_fps) == ("idle", 0.2)
    assert spec_of(mode="idle").target_fps == 0.2  # fps missing -> 0.2
    assert spec_of(mode="full", fps=5).target_fps == 5
    assert spec_of(mode="bogus", fps="x").extract == ("full", 3.0)
    # (mode, fps) is not part of spec identity
    assert spec_of(mode="idle", fps=0.2) == spec_of(mode="full", fps=3)


def test_idle_args_skip_frame_before_input_and_fps():
    c = build_command(Settings(), spec_of(mode="idle", fps=0.2))
    assert c[c.index("-skip_frame") + 1] == "nokey"
    assert c.index("-skip_frame") < c.index("-i")
    assert vf(c) == "fps=0.2,scale='min(960,iw)':-2"


def test_full_args_unchanged():
    for sp in (spec_of(), spec_of(mode="full", fps=3)):
        c = build_command(Settings(), sp)
        assert "-skip_frame" not in c and vf(c) == "fps=3,scale='min(960,iw)':-2"
    assert vf(build_command(Settings(), spec_of(mode="full", fps=5))).startswith("fps=5,")


class Clock:
    def __init__(self):
        self.t = 100.0

    def __call__(self):
        return self.t


class Rig:
    def __init__(self, spec, interval=10.0, script=None, real_clock=False, **kw):
        self.s = fast_settings(mode_switch_min_interval_s=interval, **kw)
        self.bus, self.m = FakeBus(), Metrics()
        self.clock = time.monotonic if real_clock else Clock()
        self.pub = FramePublisher(self.s, self.bus, self.m)
        self.specs, self.sources = [], []
        self.script = script or (lambda: [make_jpeg(), "hang"])

        async def factory(sp):
            self.specs.append(sp)
            src = FakeSource(self.script())
            self.sources.append(src)
            return src

        self.w = CameraWorker(spec, self.s, factory, self.pub, self.m,
                              backoff=Backoff(base=0.01, maximum=0.05, jitter=0.0), clock=self.clock)

    def switches(self):
        return self.m.mode_switches.labels("a")._value.get()

    async def fire_deferred(self):
        """Fake-clock stand-in for the real timer: drop it and re-evaluate."""
        self.w._deferred.cancel()
        await asyncio.sleep(0)
        self.w._deferred = None
        self.w._try_switch()


def test_coalescing_with_fake_clock():
    async def go():
        r = Rig(spec_of(mode="idle", fps=0.2))
        r.w.start()
        await wait_for(lambda: len(r.specs) == 1)
        r.w.set_extract("full", 3.0)  # first change applies immediately
        assert r.switches() == 1 and r.w.spec.extract == ("full", 3.0)
        await wait_for(lambda: len(r.specs) == 2 and r.specs[1].mode == "full")
        assert r.sources[0].killed
        r.w.set_extract("full", 3.0)  # unchanged: no restart
        assert r.switches() == 1
        r.clock.t += 3  # inside the 10 s window: deferred, latest wins
        r.w.set_extract("idle", 0.2)
        r.w.set_extract("full", 5.0)
        assert r.switches() == 1 and r.w.spec.extract == ("full", 3.0) and r.w._deferred is not None
        r.clock.t += 8
        await r.fire_deferred()
        assert r.switches() == 2 and r.w.spec.extract == ("full", 5.0)
        await wait_for(lambda: len(r.specs) == 3 and r.specs[2].target_fps == 5.0)
        r.clock.t += 1  # flip and flip back inside the window: nothing to apply
        r.w.set_extract("idle", 0.2)
        r.w.set_extract("full", 5.0)
        r.clock.t += 20
        await r.fire_deferred()
        assert r.switches() == 2
        await r.w.stop()
    asyncio.run(go())


def test_deferred_timer_applies_latest_in_real_time():
    async def go():
        r = Rig(spec_of(), interval=0.15, real_clock=True)
        r.w.start()
        await wait_for(lambda: len(r.specs) == 1)
        r.w.set_extract("idle", 0.2)
        assert r.switches() == 1
        r.w.set_extract("full", 3.0)  # deferred ...
        r.w.set_extract("full", 4.0)  # ... latest wins
        assert r.switches() == 1
        await wait_for(lambda: r.switches() == 2)
        assert r.w.spec.extract == ("full", 4.0)
        await wait_for(lambda: len(r.specs) == 3)
        assert r.specs[-1].extract == ("full", 4.0)
        await r.w.stop()
    asyncio.run(go())


def test_switch_is_not_a_reconnect_and_keeps_state():
    async def go():
        r = Rig(spec_of())
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ONLINE)
        r.w.set_extract("idle", 0.2)
        await wait_for(lambda: len(r.specs) == 2)
        assert r.w.reconnects == 0 and r.m.restarts.labels("a")._value.get() == 0
        assert r.w.state is CamState.ONLINE
        await r.w.stop()
    asyncio.run(go())


def test_status_fields_and_metrics():
    async def go():
        r = Rig(spec_of(mode="idle", fps=0.2))
        r.w.start()
        await wait_for(lambda: r.bus.of("camera.status"))
        st = r.bus.of("camera.status")[0]
        assert st["mode"] == "idle" and st["fps_target"] == 0.2
        assert r.m.mode.labels("a", "idle")._value.get() == 1 and r.m.mode.labels("a", "full")._value.get() == 0
        r.w.set_extract("full", 3.0)
        assert r.bus.of("camera.status")[-1]["mode"] == "full"
        assert r.bus.of("camera.status")[-1]["fps_target"] == 3.0
        assert r.w.snapshot()["mode"] == "full"
        out = r.m.render().decode()
        assert 'vp_extractor_camera_mode{camera_id="a",mode="full"} 1.0' in out
        assert 'vp_extractor_mode_switches_total{camera_id="a"} 1.0' in out
        await r.w.stop()
        assert 'camera_mode{camera_id="a"' not in r.m.render().decode()
    asyncio.run(go())


def test_idle_stall_timeout_longer_than_full():
    async def go():
        # fast_settings: full stall 0.3 s; idle 0.8 s -> idle survives a 0.5 s gap, full stalls
        for mode, stalls in (("idle", False), ("full", True)):
            r = Rig(spec_of(mode=mode, fps=1), idle_stall_timeout_s=0.8, real_clock=True,
                    script=lambda: [make_jpeg(), 0.5, make_jpeg(), "hang"])
            r.w.start()
            await asyncio.sleep(0.7)
            seen = "stalled" in [m["state"] for m in r.bus.of("camera.status")]
            assert seen is stalls, mode
            await r.w.stop()
    asyncio.run(go())


def test_manager_applies_extract_without_recreating_worker():
    async def go():
        m, reg, made = make()
        reg.assigned = [cfg("a")]
        await m.heartbeat_once()
        a = m.cameras["a"]
        reg.assigned = [{**cfg("a"), "extract": {"mode": "idle", "fps": 0.2}}]
        await m.heartbeat_once()
        assert m.cameras["a"] is a and not a.stopped and a.extract_calls == [("idle", 0.2)]
        assert len(made) == 1
    asyncio.run(go())
