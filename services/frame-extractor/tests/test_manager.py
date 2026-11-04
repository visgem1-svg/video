import asyncio

from conftest import fast_settings, wait_for
from extractor.config import Settings
from extractor.ffmpeg_reader import build_command
from extractor.manager import CameraManager
from extractor.metrics import Metrics
from extractor.models import CameraSpec
from extractor.registry_client import HeartbeatResponse, RegistryError


class FakeWorker:
    def __init__(self, spec, delay):
        self.spec, self.delay = spec, delay
        self.started = self.stopped = False

    def start(self):
        self.started = True

    async def stop(self):
        self.stopped = True

    def set_extract(self, mode, fps):
        self.extract_calls = getattr(self, "extract_calls", []) + [(mode, fps)]

    def snapshot(self):
        return {"camera_id": self.spec.camera_id, "state": "online"}


class FakeRegistry:
    def __init__(self):
        self.assigned = []
        self.down = False
        self.calls = []
        self.capacity = None

    async def heartbeat(self, capacity, running):
        self.calls.append((capacity, running))
        if self.down:
            raise RegistryError("connection refused")
        return HeartbeatResponse(self.assigned, self.capacity)

    async def close(self):
        pass


def cfg(cid, url=None, fps=3, enabled=True, generation=None):
    return {"camera_id": cid, "name": cid, "source_url": url or f"rtsp://m/{cid}",
            "analysis_fps": fps, "enabled": enabled, "plan_id": None, "tags": [], "updated_at": 1,
            **({} if generation is None else {"generation": generation})}


def make():
    reg, made = FakeRegistry(), []

    def factory(spec, delay):
        w = FakeWorker(spec, delay)
        made.append(w)
        return w

    return CameraManager(fast_settings(worker_capacity=10), reg, factory, Metrics()), reg, made


def test_add_remove_change_reconcile():
    async def go():
        m, reg, made = make()
        reg.assigned = [cfg("a"), cfg("b")]
        assert await m.heartbeat_once()
        assert sorted(m.cameras) == ["a", "b"] and all(w.started for w in made)
        assert reg.calls[0] == (10, [])
        a, b = m.cameras["a"], m.cameras["b"]

        reg.assigned = [cfg("a"), cfg("b", fps=5), cfg("c")]  # b changed, c added
        await m.heartbeat_once()
        assert m.cameras["a"] is a and not a.stopped  # untouched
        assert b.stopped and m.cameras["b"] is not b and m.cameras["b"].spec.analysis_fps == 5
        assert reg.calls[1][1] == ["a", "b"]  # running reported
        assert "c" in m.cameras

        reg.assigned = [cfg("a", url="rtsp://other/a"), cfg("c")]  # a url changed, b removed
        await m.heartbeat_once()
        assert a.stopped and m.cameras["a"].spec.source_url == "rtsp://other/a"
        assert "b" not in m.cameras and m._m.cameras_running._value.get() == 2

        reg.assigned = []
        await m.heartbeat_once()
        assert m.cameras == {}
    asyncio.run(go())


def test_disabled_and_invalid_entries_ignored():
    async def go():
        m, reg, _ = make()
        reg.assigned = [cfg("a", enabled=False), {"camera_id": "x"}, "junk", cfg("ok")]
        await m.heartbeat_once()
        assert list(m.cameras) == ["ok"]
    asyncio.run(go())


def test_registry_outage_keeps_cameras_and_recovers():
    async def go():
        m, reg, made = make()
        reg.assigned = [cfg("a"), cfg("b")]
        await m.heartbeat_once()
        reg.down = True
        for _ in range(3):
            assert await m.heartbeat_once() is False
        assert sorted(m.cameras) == ["a", "b"] and not any(w.stopped for w in made)
        assert m.registry_ok is False
        reg.down = False
        reg.assigned = [cfg("a")]
        assert await m.heartbeat_once() and sorted(m.cameras) == ["a"] and m.registry_ok
    asyncio.run(go())


def test_control_loop_survives_unexpected_errors_and_stops():
    async def go():
        m, reg, _ = make()
        n = {"i": 0}

        async def flaky(capacity, running):
            n["i"] += 1
            if n["i"] == 1:
                raise ValueError("boom")  # not a RegistryError
            return HeartbeatResponse([cfg("a")])

        reg.heartbeat = flaky
        stop = asyncio.Event()
        t = asyncio.create_task(m.run(stop))
        await wait_for(lambda: "a" in m.cameras)
        stop.set()
        await asyncio.wait_for(t, 1)
        await m.stop_all()
        assert m.cameras == {}
    asyncio.run(go())


def test_start_is_staggered():
    async def go():
        m, reg, made = make()
        reg.assigned = [cfg(f"c{i}") for i in range(5)]
        await m.heartbeat_once()
        delays = [w.delay for w in made]
        assert delays == sorted(delays) and delays[0] == 0 and delays[-1] > 0
    asyncio.run(go())


def test_camera_spec_from_config():
    assert CameraSpec.from_config(cfg("a", fps=0)).analysis_fps == 3.0
    assert CameraSpec.from_config({"camera_id": "a", "source_url": "u"}).analysis_fps == 3.0
    assert CameraSpec.from_config(cfg("a", enabled=False)) is None


def test_ffmpeg_command():
    s = Settings(ffmpeg_path="ff", ffmpeg_hwaccel="cuda", jpeg_max_width=960)
    c = build_command(s, CameraSpec("a", "rtsp://h/a", 3))
    assert c[0] == "ff" and c[c.index("-rtsp_transport") + 1] == "tcp"
    assert "-fflags" in c and "nobuffer" in c and c[c.index("-hwaccel") + 1] == "cuda"
    assert c[c.index("-vf") + 1] == "fps=3,scale='min(960,iw)':-2"
    assert c[c.index("-q:v") + 1] == "5" and c[-3:] == ["-f", "image2pipe", "pipe:1"]
    # encoder threads must be pinned after -i (default = 1 thread/core -> ~4x memory per camera)
    out = c[c.index("-i"):]
    assert out[out.index("-threads") + 1] == "1"
    assert c.index("-hwaccel") < c.index("-i") and c[c.index("-timeout") + 1] == "10000000" and "-rw_timeout" not in c
    f = build_command(Settings(), CameraSpec("a", "file.mp4", 2.5))
    assert "-rtsp_transport" not in f and "-hwaccel" not in f and "fps=2.5" in f[f.index("-vf") + 1]


def test_settings_from_env():
    s = Settings.from_env({"WORKER_ID": "fx-9", "WORKER_CAPACITY": "25",
                           "REGISTRY_URL": "http://r:1/", "FFMPEG_HWACCEL": "vaapi"})
    assert (s.worker_id, s.worker_capacity, s.registry_url, s.ffmpeg_hwaccel) == (
        "fx-9", 25, "http://r:1", "vaapi")
    assert s.port == 9101 and s.jpeg_max_width == 960


def test_settings_defaults_from_empty_env():
    s = Settings.from_env({})
    assert s.registry_url == "http://registry:5001" and s.kafka_bootstrap == "redpanda:9092"
    assert s.worker_capacity == 10 and not hasattr(s, "minio_endpoint")


def test_generation_change_restarts_camera_only_that_one():
    async def go():
        m, reg, made = make()
        reg.assigned = [cfg("a", generation=1), cfg("b", generation=1)]
        await m.heartbeat_once()
        a, b = m.cameras["a"], m.cameras["b"]
        await m.heartbeat_once()  # unchanged -> nothing restarts
        assert not a.stopped and not b.stopped
        reg.assigned = [cfg("a", generation=2), cfg("b", generation=1)]
        await m.heartbeat_once()
        assert a.stopped and not b.stopped
        assert m.cameras["a"] is not a and m.cameras["a"].spec.generation == 2 and m.cameras["a"].started
        assert m.cameras["b"] is b
    asyncio.run(go())


def test_missing_generation_defaults_to_zero():
    assert CameraSpec.from_config(cfg("a")).generation == 0
    assert CameraSpec.from_config(cfg("a", generation=7)).generation == 7


def test_registry_capacity_override_honored_else_env():
    async def go():
        m, reg, _ = make()
        assert m.capacity == 10
        reg.capacity = 4
        await m.heartbeat_once()
        assert m.capacity == 4
        reg.capacity = None  # override cleared -> back to env
        await m.heartbeat_once()
        assert m.capacity == 10
    asyncio.run(go())


def test_registry_client_parses_capacity_and_generation():
    from aiohttp import web
    from aiohttp.test_utils import TestServer
    from extractor.registry_client import RegistryClient

    async def go():
        body = {"assigned": [cfg("a", generation=3)], "lease_s": 15, "capacity": 6}

        async def hb(req):
            return web.json_response(body)

        app = web.Application()
        app.router.add_post("/api/workers/fx-t/heartbeat", hb)
        async with TestServer(app) as srv:
            c = RegistryClient(str(srv.make_url("")).rstrip("/"), "fx-t")
            r = await c.heartbeat(10, [])
            assert r.capacity == 6 and r.assigned[0]["generation"] == 3
            del body["capacity"]
            assert (await c.heartbeat(10, [])).capacity is None
            await c.close()
    asyncio.run(go())


def test_worker_id_defaults_to_hostname_and_status_interval_2s():
    import socket
    s = Settings.from_env({})
    assert s.worker_id == socket.gethostname() and s.status_interval_s == 2.0
    assert Settings.from_env({"WORKER_ID": ""}).worker_id == socket.gethostname()
    assert Settings.from_env({"WORKER_ID": "fx-1"}).worker_id == "fx-1"


def test_kafka_bus_config_and_headers(monkeypatch):
    """Real KafkaBus with a stubbed librdkafka Producer: config + produce() arguments."""
    import confluent_kafka
    from extractor.publisher import FramePublisher, KafkaBus

    seen = {}

    class StubProducer:
        def __init__(self, conf):
            seen["conf"] = conf
            seen["sent"] = []

        def poll(self, t):
            import time
            time.sleep(0.01)

        def produce(self, topic, value, key=None, headers=None, on_delivery=None):
            if len(seen["sent"]) >= 1:
                raise BufferError("queue full")
            seen["sent"].append((topic, value, key, headers))

        def flush(self, t):
            pass

    monkeypatch.setattr(confluent_kafka, "Producer", StubProducer)
    m = Metrics()
    s = fast_settings(worker_capacity=25)
    bus = KafkaBus(s, m)
    c = seen["conf"]
    assert c["compression.type"] == "none" and 10 <= c["linger.ms"] <= 20 and c["message.max.bytes"] >= 2 * 1024 * 1024
    assert c["queue.buffering.max.kbytes"] >= 25 * 3 * 2 * 100  # >= ~2 s of frames
    from extractor.models import Frame
    from conftest import make_jpeg
    pub = FramePublisher(s, bus, m)
    f = Frame("id", "cam-1", 1, 1, 960, 540, make_jpeg())
    assert pub.publish_frame(f) is True
    assert pub.publish_frame(f) is False  # BufferError -> reported as drop
    topic, value, key, headers = seen["sent"][0]
    assert (topic, key, value) == ("frames.raw", b"cam-1", f.data) and dict(headers)["content-type"] == b"image/jpeg"
    bus.close()
