"""Contract 9.9: motion gate chain, analysis URL, error classes, terminal backoff, status fields."""
import asyncio

import pytest

from conftest import FakeBus, FakeSource, fast_settings, make_jpeg, wait_for
from extractor import errors
from extractor.camera_worker import CameraWorker, TERMINAL_BACKOFF
from extractor.config import Settings
from extractor.ffmpeg_reader import FfmpegSource, build_command
from extractor.manager import CameraManager
from extractor.metrics import Metrics
from extractor.models import CameraSpec, MotionConfig, status_message
from extractor.motion_presets import PRESETS
from extractor.publisher import FramePublisher
from extractor.state import Backoff, CamState, SkipMeter
from test_manager import FakeRegistry, FakeWorker

URL = "rtsp://mediamtx:8554/cam-001"


def spec(motion=None, fps=3.0, mode="full", aurl=None):
    return CameraSpec("cam-001", URL, fps, mode=mode, motion=motion, analysis_url=aurl)


def vf(c):
    return c[c.index("-vf") + 1]


# ---- classifier: REAL ffmpeg 7.1 stderr captured against MediaMTX 1.9.1 (stack of this repo) -------------------
REAL = [
    # wrong path on a configured MediaMTX path regex (no publisher)
    ("[rtsp @ 0x5c6299f5f2c0] method DESCRIBE failed: 404Not Found\n[in#0 @ 0x5c6299f5f000] Error opening input: "
     "Server returned 404 Not Found\nError opening input files: Server returned 404 Not Found", 8, errors.NOT_FOUND),
    # path not configured at all
    ("[in#0 @ 0x626c73cff000] Error opening input: Server returned 400 Bad Request\nError opening input file "
     "rtsp://mediamtx:8554/doesnotexist.", 8, errors.NOT_FOUND),
    # wrong credentials AND no credentials (MediaMTX internal auth)
    ("[rtsp @ 0x5b35ce7622c0] method DESCRIBE failed: 401Unauthorized\n[in#0 @ 0x5b35ce762000] Error opening input: "
     "Server returned 401 Unauthorized (authorization failed)", 8, errors.AUTH_FAILED),
    # closed port
    ("[tcp @ 0x6347c2e8df40] Connection to tcp://mediamtx:9999?timeout=10000000 failed: Connection refused\n"
     "[in#0 @ 0x6347c2e8b000] Error opening input: Connection refused", 145, errors.UNREACHABLE),
    # bogus host
    ("[tcp @ 0x560f0c837f40] Failed to resolve hostname nohost.invalid: Name or service not known\n"
     "[in#0 @ 0x560f0c835000] Error opening input: Input/output error", 251, errors.UNREACHABLE),
    # blackholed address
    ("[tcp @ 0x5b9aad82cf40] Connection to tcp://10.255.255.1:554?timeout=10000000 failed: Connection timed out\n"
     "[in#0 @ 0x5b9aad82a000] Error opening input: Connection timed out", 146, errors.UNREACHABLE),
    # non-RTSP URL answering with something that is not media (http API) / garbage bytes
    ("[in#0 @ 0x59a923978000] Error opening input: Invalid data found when processing input\nError opening input "
     "file http://mediamtx:9997/v3/config/global/get.", 183, errors.DECODE_ERROR),
    # URL scheme ffmpeg does not have
    ("[in#0 @ 0x5e0cf10ff000] Error opening input: Protocol not found\nDid you mean file:foo://bar/x?", 8,
     errors.NOT_FOUND),
    # audio-only stream with -an: nothing to extract
    ("[out#0/image2pipe @ 0x5eefe3f616c0] Output file does not contain any stream\nError opening output file pipe:1."
     "\nError opening output files: Invalid argument", 234, errors.CODEC_UNSUPPORTED),
    # decoder-side garbage while running
    ("[vf#0:0 @ 0x5781509b4580] Terminating thread with return code -1094995529 (Invalid data found when processing "
     "input)", 183, errors.DECODE_ERROR),
    # ffmpeg-source strings for codecs without a decoder (not capturable here: every codec in the stack decodes)
    ("Decoder (codec hevc) not found for input stream #0:0", 1, errors.CODEC_UNSUPPORTED),
    ("[rtsp @ 0x1] Unsupported codec", 1, errors.CODEC_UNSUPPORTED),
    # nothing recognisable
    ("", 0, errors.EOF_),
    ("", 1, errors.EOF_),
    ("[rtsp @ 0x1] Connection reset by peer", 1, errors.UNREACHABLE),
    ("something never seen", 1, errors.DECODE_ERROR),
]


@pytest.mark.parametrize("text,rc,want", REAL)
def test_classify(text, rc, want):
    assert errors.classify(text, rc) == want


def test_terminal_set_is_exactly_the_spec_one():
    assert errors.TERMINAL == {"auth_failed", "not_found", "codec_unsupported"}
    assert not any(errors.is_terminal(c) for c in (None, "unreachable", "stalled", "decode_error", "eof"))


# ---- chain building ---------------------------------------------------------------------------------------------
def test_chain_without_motion_is_unchanged():
    c = build_command(Settings(), spec())
    assert vf(c) == "fps=3,scale='min(960,iw)':-2"
    assert not {"-fps_mode", "-progress", "-skip_loop_filter"} & set(c)


def test_chain_with_motion_medium():
    c = build_command(Settings(), spec(MotionConfig("medium", 5)))
    m = PRESETS["medium"]
    assert vf(c) == f"fps=3,mpdecimate=hi={m.hi}:lo={m.lo}:frac={m.frac:g}:max=14,scale='min(960,iw)':-2"
    assert c[c.index("-fps_mode") + 1] == "vfr" and c.index("-fps_mode") > c.index("-i")
    assert c[c.index("-progress") + 1] == "pipe:2"
    assert c[-3:] == ["-f", "image2pipe", "pipe:1"]


@pytest.mark.parametrize("hb,fps,m", [(5, 3, 14), (1, 3, 2), (60, 3, 179), (5, 2.5, 12), (2, 1, 1), (1.5, 2, 2)])
def test_heartbeat_to_max_math(hb, fps, m):
    assert MotionConfig("medium", hb).max_drops(fps) == m
    assert f":max={m}," in vf(build_command(Settings(), spec(MotionConfig("medium", hb), fps)))


def test_heartbeat_not_longer_than_one_frame_means_no_gate():
    c = build_command(Settings(), spec(MotionConfig("medium", 1), fps=1.0))  # max would be 0 = "drop without limit"
    assert "mpdecimate" not in vf(c) and "-fps_mode" not in c


@pytest.mark.parametrize("sens", ["low", "medium", "high"])
def test_presets_come_from_the_table(sens):
    p = PRESETS[sens]
    assert f"mpdecimate=hi={p.hi}:lo={p.lo}:frac={p.frac:g}:" in vf(build_command(Settings(), spec(MotionConfig(sens))))


def test_presets_ordering_low_drops_more_than_high():
    assert PRESETS["low"].hi > PRESETS["medium"].hi > PRESETS["high"].hi
    assert PRESETS["medium"].lo > 320  # calibrated above ffmpeg's defaults: sensor noise at every keyframe false-triggers 768/320 (RESULTS.md)


def test_idle_mode_is_never_gated():
    c = build_command(Settings(), spec(MotionConfig("high", 5), mode="idle"))
    assert "mpdecimate" not in vf(c) and "-fps_mode" not in c and "-skip_frame" in c


def test_analysis_url_is_read_and_rtsp_flags_follow_it():
    c = build_command(Settings(), spec(aurl="rtsp://mediamtx:8554/cam-001-analysis"))
    assert c[c.index("-i") + 1] == "rtsp://mediamtx:8554/cam-001-analysis" and "-rtsp_transport" in c
    assert build_command(Settings(), spec())[build_command(Settings(), spec()).index("-i") + 1] == URL
    f = build_command(Settings(), CameraSpec("a", "rtsp://x/a", 3, analysis_url="file.mp4"))
    assert "-rtsp_transport" not in f  # decided by the URL actually read


def test_skip_loop_filter_flag_default_off_and_before_input():
    assert "-skip_loop_filter" not in build_command(Settings(), spec())
    s = Settings.from_env({"FFMPEG_SKIP_LOOP_FILTER": "1"})
    assert s.ffmpeg_skip_loop_filter and not Settings.from_env({}).ffmpeg_skip_loop_filter
    c = build_command(s, spec(MotionConfig()))
    assert c[c.index("-skip_loop_filter") + 1] == "all" and c.index("-skip_loop_filter") < c.index("-i")
    ci = build_command(s, spec(mode="idle"))
    assert ci.index("-skip_loop_filter") < ci.index("-i")


# ---- heartbeat parsing / backward compatibility -----------------------------------------------------------------
def cfg(**kw):
    return {"camera_id": "cam-001", "source_url": URL, "analysis_fps": 3, **kw}


def test_old_heartbeat_items_behave_as_before():
    s = CameraSpec.from_config(cfg())
    assert s.motion is None and s.analysis_url is None and s.read_url == URL and not s.gated
    assert s == CameraSpec("cam-001", URL, 3.0)


@pytest.mark.parametrize("motion", [None, {}, {"enabled": False}, {"enabled": "yes"}, "on", 5, []])
def test_motion_disabled_or_malformed_is_disabled(motion):
    assert CameraSpec.from_config(cfg(motion=motion)).motion is None


def test_motion_parsing_and_defaults():
    m = CameraSpec.from_config(cfg(motion={"enabled": True})).motion
    assert m == MotionConfig("medium", 5.0)
    m = CameraSpec.from_config(cfg(motion={"enabled": True, "sensitivity": "high", "heartbeat_s": 10})).motion
    assert m == MotionConfig("high", 10.0)
    assert CameraSpec.from_config(cfg(motion={"enabled": True, "sensitivity": "x", "heartbeat_s": 999})).motion == \
        MotionConfig("medium", 5.0)
    assert CameraSpec.from_config(cfg(motion={"enabled": True, "heartbeat_s": "abc"})).motion.heartbeat_s == 5.0


def test_analysis_source_url_missing_or_null_falls_back():
    assert CameraSpec.from_config(cfg(analysis_source_url=None)).read_url == URL
    assert CameraSpec.from_config(cfg(analysis_source_url="")).read_url == URL
    assert CameraSpec.from_config(cfg(analysis_source_url="rtsp://m/x-analysis")).read_url == "rtsp://m/x-analysis"


def test_status_message_new_fields_default_to_null():
    m = status_message("c", "w", "online", 3.0, None, 0, None)
    assert m["motion_skipped_pct"] is None and m["error_class"] is None
    m = status_message("c", "w", "error", 0, None, 0, "x", motion_skipped_pct=93.333, error_class="auth_failed")
    assert m["motion_skipped_pct"] == 93.3 and m["error_class"] == "auth_failed"


# ---- gate accounting from the real ffmpeg -progress output ----------------------------------------------------
# Captured: 32 s run, static scene, medium, hb 5 s, 3 fps (first frame pts is NOT 0 on live RTSP).
REAL_PROGRESS = """frame=1
fps=0.00
stream_0_0_q=5.0
bitrate=   0.6kbits/s
total_size=1978
out_time_us=1000000
out_time_ms=1000000
out_time=00:00:01.000000
dup_frames=0
drop_frames=0
speed=1.05x
progress=continue
frame=6
fps=0.20
stream_0_0_q=5.0
bitrate=   3.0kbits/s
total_size=9871
out_time_us=26000000
out_time_ms=26000000
out_time=00:00:26.000000
dup_frames=0
drop_frames=0
speed=0.879x
progress=continue
frame=7
fps=0.23
stream_0_0_q=5.0
bitrate=   3.6kbits/s
total_size=13849
out_time_us=31000000
out_time_ms=31000000
out_time=00:00:31.000000
dup_frames=0
drop_frames=0
speed=1.01x
progress=end
"""


class _Proc:
    def __init__(self, stderr_text):
        self.stderr = asyncio.StreamReader()
        self.stderr.feed_data(stderr_text.encode())
        self.stderr.feed_eof()
        self.stdout = None
        self.returncode = None


def test_gate_counter_from_progress_and_stderr_tail_stays_clean():
    async def go():
        src = FfmpegSource(_Proc(REAL_PROGRESS + "[rtsp @ 0x1] Connection reset by peer\n"), fps=3.0)
        await src._drain
        # 30 s of stream time at 3 fps = 90 frames expected, 6 written -> 84 gated
        assert src.gated_frames == 84
        assert src.stderr_tail() == "[rtsp @ 0x1] Connection reset by peer"
        off = FfmpegSource(_Proc(REAL_PROGRESS), fps=0.0)  # gate not in the chain: progress-looking text is plain stderr
        await off._drain
        assert off.gated_frames == 0
    asyncio.run(go())


def test_skip_meter_rolling_window():
    t = [0.0]
    m = SkipMeter(60.0, lambda: t[0])
    assert m.pct() == 0.0
    for i in range(0, 130, 2):  # every 2 s: 14 gated, 1 emitted per 5 s => 93.3 %
        t[0] = float(i)
        m.sample(int(i / 5 * 14), int(i / 5))
    assert m.pct() == pytest.approx(93.33, abs=0.5)
    for i in range(130, 260, 2):  # then everything passes for > 60 s: window forgets the old gating
        t[0] = float(i)
        m.sample(26 * 14, 26 + int((i - 130) * 3))
    assert m.pct() == 0.0
    assert len(m._s) < 40  # bounded


# ---- worker -----------------------------------------------------------------------------------------------------
def run(c):
    return asyncio.run(c)


class Rig:
    def __init__(self, sources, sp=None, **kw):
        self.s = fast_settings(**kw)
        self.sources = list(sources)
        self.specs = []
        self.bus, self.m = FakeBus(), Metrics()
        self.pub = FramePublisher(self.s, self.bus, self.m)

        async def factory(spec_):
            self.specs.append(spec_)
            return self.sources.pop(0) if self.sources else FakeSource(["hang"])

        self.w = CameraWorker(sp or spec(), self.s, factory, self.pub, self.m,
                              backoff=Backoff(base=0.01, maximum=0.05, jitter=0.0),
                              terminal_backoff=Backoff(base=0.15, maximum=0.3, jitter=0.0))

    def statuses(self):
        return self.bus.of("camera.status")


def test_terminal_backoff_schedule_matches_spec():
    b = Backoff(*TERMINAL_BACKOFF, jitter=0.0)
    assert [b.next_delay() for _ in range(7)] == [30, 60, 120, 240, 300, 300, 300]
    b.reset()
    assert b.next_delay() == 30


@pytest.mark.parametrize("text,cls", [("Server returned 401 Unauthorized (authorization failed)", "auth_failed"),
                                      ("Server returned 404 Not Found", "not_found")])
def test_terminal_class_goes_to_error_at_once_and_uses_slow_backoff(text, cls):
    async def go():
        r = Rig([FakeSource(["eof"], stderr=text) for _ in range(20)])
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ERROR)
        assert r.w.error_class == cls
        await asyncio.sleep(0.5)  # transient backoff would have respawned ~20x (10 ms); terminal: 0.15, 0.3, 0.3...
        assert 2 <= len(r.specs) <= 4
        assert "reconnecting" not in [m["state"] for m in r.statuses()]
        last = [m for m in r.statuses() if m["state"] == "error"][-1]
        assert last["error_class"] == cls and last["motion_skipped_pct"] is None
        await r.w.stop()
    run(go())


def test_transient_class_keeps_fast_backoff_and_reconnecting():
    async def go():
        r = Rig([FakeSource(["eof"], stderr="Connection refused") for _ in range(30)])
        r.w.start()
        await asyncio.sleep(0.4)
        assert len(r.specs) >= 6 and r.w.error_class == "unreachable"
        assert "reconnecting" in [m["state"] for m in r.statuses()]
        await r.w.stop()
    run(go())


def test_error_class_clears_when_frames_flow_and_terminal_backoff_resets():
    async def go():
        r = Rig([FakeSource(["eof"], stderr="Server returned 404 Not Found"),
                 FakeSource([make_jpeg(), "hang"])])
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ONLINE)
        assert r.w.error_class is None and r.w._term_backoff.attempts == 0
        assert r.statuses()[-1]["error_class"] is None
        await r.w.stop()
    run(go())


def test_config_change_resets_terminal_backoff_and_retries_immediately():
    async def go():
        s = fast_settings(mode_switch_min_interval_s=0.0)
        r = Rig([FakeSource(["eof"], stderr="Server returned 401 Unauthorized") for _ in range(3)])
        r.w = CameraWorker(spec(), s, r.w._factory, r.pub, r.m, backoff=Backoff(0.01, 0.05, 0.0),
                           terminal_backoff=Backoff(30.0, 300.0, 0.0))  # real 30 s schedule
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ERROR and len(r.specs) == 1)
        await asyncio.sleep(0.1)
        assert len(r.specs) == 1  # sleeping 30 s
        r.sources[:] = [FakeSource([make_jpeg(), "hang"])]
        r.w.set_feed("rtsp://mediamtx:8554/cam-001-analysis", None)
        r.w.set_extract("full", 3.0)
        await wait_for(lambda: r.w.state is CamState.ONLINE)
        assert r.specs[-1].read_url.endswith("-analysis") and r.w._term_backoff.attempts == 0
        await r.w.stop()
    run(go())


def test_gate_metric_and_status_pct():
    async def go():
        src = FakeSource([make_jpeg(), "hang"])
        r = Rig([src], sp=spec(MotionConfig("medium", 5)), status_interval_s=0.05)
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ONLINE)
        await asyncio.sleep(0.12)
        src.gated_frames = 14
        await wait_for(lambda: (r.statuses()[-1]["motion_skipped_pct"] or 0) > 80)
        assert r.m.motion_skipped.labels("cam-001")._value.get() == 14
        assert r.statuses()[-1]["motion_skipped_pct"] == pytest.approx(93.3, abs=0.1)  # 14 gated, 1 emitted
        assert 'vp_extractor_frames_motion_skipped_total{camera_id="cam-001"} 14.0' in r.m.render().decode()
        await r.w.stop()
        assert 'motion_skipped_total{camera_id="cam-001"}' not in r.m.render().decode()
    run(go())


def test_motion_disabled_status_pct_is_null_and_counter_untouched():
    async def go():
        src = FakeSource([make_jpeg(), "hang"])
        src.gated_frames = 99  # must be ignored when the gate is not in the chain
        r = Rig([src], status_interval_s=0.05)
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ONLINE)
        await asyncio.sleep(0.15)
        assert all(m["motion_skipped_pct"] is None for m in r.statuses())
        assert r.m.motion_skipped.labels("cam-001")._value.get() == 0
        await r.w.stop()
    run(go())


def test_gated_stall_timeout_covers_the_heartbeat():
    async def go():
        # stall_timeout 0.2 s but heartbeat 1 s (+3 s margin): a quiet static scene must not be restarted
        r = Rig([FakeSource([make_jpeg(), "hang"])], sp=spec(MotionConfig("medium", 1)), stall_timeout_s=0.2)
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ONLINE)
        await asyncio.sleep(0.6)
        assert r.w.state is CamState.ONLINE and len(r.specs) == 1
        await r.w.stop()
        r2 = Rig([FakeSource([make_jpeg(), "hang"])], stall_timeout_s=0.2)  # not gated: stalls as before
        r2.w.start()
        await wait_for(lambda: r2.w.state is CamState.STALLED)
        assert r2.w.error_class == "stalled"
        await r2.w.stop()
    run(go())


def test_motion_change_restarts_once_in_place_and_coalesces():
    async def go():
        r = Rig([FakeSource([make_jpeg(), "hang"]), FakeSource([make_jpeg(), "hang"]), FakeSource([make_jpeg(), "hang"])],
                mode_switch_min_interval_s=0.3, stall_timeout_s=5, startup_timeout_s=5)
        r.w.start()
        await wait_for(lambda: r.w.state is CamState.ONLINE)
        r.w.set_feed(None, MotionConfig("low", 5))
        r.w.set_extract("full", 3.0)
        await wait_for(lambda: len(r.specs) == 2)  # first change: immediate, in place, not a reconnect
        assert r.specs[1].motion == MotionConfig("low", 5) and r.w.reconnects == 0
        r.w.set_feed(None, MotionConfig("high", 5))  # within 0.3 s of the last restart: deferred, latest wins
        r.w.set_extract("full", 3.0)
        r.w.set_feed(None, None)
        r.w.set_extract("full", 3.0)
        await asyncio.sleep(0.1)
        assert len(r.specs) == 2
        await wait_for(lambda: len(r.specs) == 3)
        await asyncio.sleep(0.4)
        assert len(r.specs) == 3 and r.specs[2].motion is None
        await r.w.stop()
    run(go())


# ---- manager ----------------------------------------------------------------------------------------------------
class FeedWorker(FakeWorker):
    def __init__(self, spec_, delay):
        super().__init__(spec_, delay)
        self.calls = []

    def set_feed(self, url, motion):
        self.calls.append(("feed", url, motion))

    def set_extract(self, mode, fps):
        self.calls.append(("extract", mode, fps))


def test_manager_feed_change_is_in_place_one_call_pair_and_old_items_do_nothing():
    async def go():
        reg, made = FakeRegistry(), []
        m = CameraManager(fast_settings(), reg, lambda sp, d: made.append(FeedWorker(sp, d)) or made[-1], Metrics())
        await m.reconcile([cfg()])
        w = made[0]
        await m.reconcile([cfg()])
        assert w.calls == [] and len(made) == 1
        mo = {"enabled": True, "sensitivity": "low", "heartbeat_s": 5}
        await m.reconcile([cfg(motion=mo, analysis_source_url="rtsp://m/x-analysis")])
        assert len(made) == 1 and not w.stopped  # not a new worker
        assert w.calls == [("feed", "rtsp://m/x-analysis", MotionConfig("low", 5)), ("extract", "full", 3.0)]
    run(go())
