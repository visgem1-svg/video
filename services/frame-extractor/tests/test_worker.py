import asyncio
import json
import re

from conftest import FakeBus, FakeSource, fast_settings, make_jpeg, wait_for
from extractor.camera_worker import CameraWorker
from extractor.metrics import Metrics
from extractor.models import CameraSpec
from extractor.publisher import FramePublisher
from extractor.state import Backoff, CamState

SPEC = CameraSpec("cam-001", "rtsp://x/cam-001", 3)
UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")


class Rig:
    def __init__(self, sources, bus=None, **kw):
        self.s = fast_settings(**kw)
        self.sources = list(sources)
        self.spawned = 0
        self.bus, self.m = bus or FakeBus(), Metrics()
        self.pub = FramePublisher(self.s, self.bus, self.m)

        async def factory(spec):
            self.spawned += 1
            nxt = self.sources.pop(0) if self.sources else FakeSource(["hang"])
            if isinstance(nxt, Exception):
                raise nxt
            return nxt

        self.w = CameraWorker(SPEC, self.s, factory, self.pub, self.m,
                              backoff=Backoff(base=0.01, maximum=0.05, jitter=0.0),
                              terminal_backoff=Backoff(base=0.2, maximum=0.4, jitter=0.0))

    def states(self):
        return [m["state"] for m in self.bus.of("camera.status")]


def run(coro):
    return asyncio.run(coro)


def test_frames_published_as_jpeg_value_with_meta_header():
    async def go():
        js = [make_jpeg(960, 540, bytes([i]) * 20) for i in range(3)]
        r = Rig([FakeSource([js[0], 0.05, js[1], 0.05, js[2]])])
        r.w.start()
        await wait_for(lambda: len(r.bus.frames()) == 3)
        for i, (key, value, hdr) in enumerate(r.bus.frames(), 1):
            assert key == "cam-001" and value == js[i - 1]  # value = exact JPEG bytes
            assert set(hdr) == {"meta", "content-type"} and hdr["content-type"] == b"image/jpeg"
            m = json.loads(hdr["meta"].decode("utf-8"))
            assert set(m) == {"frame_id", "camera_id", "ts", "seq", "width", "height", "worker_id"}  # no object_key
            assert UUID.match(m["frame_id"]) and m["camera_id"] == "cam-001" and m["worker_id"] == "fx-test"
            assert isinstance(m["ts"], int) and m["seq"] == i and (m["width"], m["height"]) == (960, 540)
        assert r.m.frames.labels("cam-001")._value.get() == 3
        assert r.w.state is CamState.ONLINE
        await r.w.stop()
    run(go())


def test_status_schema_and_transitions():
    async def go():
        r = Rig([FakeSource([make_jpeg(), 1.0])])
        r.w.start()
        await wait_for(lambda: "online" in r.states())
        await asyncio.sleep(0.5)  # periodic status (0.2 s)
        await r.w.stop()
        sts = r.bus.of("camera.status")
        keys = {"camera_id", "worker_id", "state", "fps_actual", "last_frame_ts", "reconnects", "error", "ts", "mode", "fps_target",
            "motion_skipped_pct", "error_class"}
        assert all(set(m) == keys for m in sts)
        assert all(m["state"] in {"starting", "online", "stalled", "reconnecting", "offline", "error"} for m in sts)
        assert sts[0]["state"] == "starting" and sts[-1]["state"] == "offline"
        assert r.states().count("online") >= 2  # change + periodic
        assert all(k == "cam-001" for t, k, *_ in r.bus.messages if t == "camera.status")
    run(go())


def test_stall_restarts_ffmpeg_with_backoff_and_recovers():
    async def go():
        j = make_jpeg()
        s1 = FakeSource([j, "hang"], stderr="Connection timed out")
        s2 = FakeSource([j, j, "hang"])
        r = Rig([s1, s2])
        r.w.start()
        await wait_for(lambda: len(r.bus.of("frames.raw")) >= 3)
        assert s1.killed and r.spawned == 2
        st = r.states()
        i = st.index("stalled")
        assert st[i + 1] == "reconnecting" and "online" in st[i + 2:]
        assert r.w.reconnects == 1 and r.m.restarts.labels("cam-001")._value.get() == 1
        stalled = next(m for m in r.bus.of("camera.status") if m["state"] == "stalled")
        assert "Connection timed out" in stalled["error"]
        assert r.w.error is None  # cleared when online again
        await r.w.stop()
    run(go())


def test_ffmpeg_exit_captures_stderr_and_goes_error_after_repeated_failures():
    async def go():
        r = Rig([FakeSource(["eof"], stderr="Connection refused") for _ in range(10)])
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ERROR)
        err = [m for m in r.bus.of("camera.status") if m["state"] == "error"][0]
        assert "Connection refused" in err["error"] and err["error_class"] == "unreachable"
        assert "reconnecting" in r.states()
        # keeps retrying while in error; recovers when frames arrive
        r.sources[:] = [FakeSource([make_jpeg(), "hang"])]
        await wait_for(lambda: r.w.state is CamState.ONLINE)
        await r.w.stop()
    run(go())


def test_spawn_failure_is_error_and_keeps_retrying():
    async def go():
        r = Rig([FileNotFoundError("ffmpeg"), FileNotFoundError("ffmpeg")])
        r.w.start()
        await wait_for(lambda: r.spawned >= 3)
        assert r.w.state is CamState.ERROR and "cannot start ffmpeg" in r.w.error
        await r.w.stop()
    run(go())


def test_stop_kills_ffmpeg_and_publishes_offline():
    async def go():
        src = FakeSource([make_jpeg(), "hang"])
        r = Rig([src])
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ONLINE)
        await r.w.stop()
        assert src.killed and r.states()[-1] == "offline"
        assert 'camera_id="cam-001"' not in r.m.render().decode()  # series forgotten
    run(go())


def test_producer_queue_full_drops_frame_and_never_blocks_reader():
    async def go():
        bus = FakeBus(capacity=2)
        frames = [make_jpeg(100 + i, 50) for i in range(10)]
        r = Rig([FakeSource(frames + ["hang"])], bus=bus, stall_timeout_s=5, startup_timeout_s=5)
        r.w.start()
        await wait_for(lambda: r.w._seq == 10, 1.0)
        assert len(bus.frames()) == 2
        assert r.m.dropped.labels("cam-001")._value.get() == 8
        assert r.m.frames.labels("cam-001")._value.get() == 2
        assert r.w.state is CamState.ONLINE  # camera itself stays healthy
        await r.w.stop()
    run(go())


def test_bad_jpeg_is_ignored():
    async def go():
        r = Rig([FakeSource([b"\xff\xd8\xff\xd9", make_jpeg(), "hang"])], stall_timeout_s=5, startup_timeout_s=5)
        r.w.start()
        await wait_for(lambda: len(r.bus.of("frames.raw")) == 1)
        assert r.m.bad_frames._value.get() >= 1
        await r.w.stop()
    run(go())
