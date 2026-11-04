using System.Text.Json;
using StreamHealth;
using Xunit;

namespace StreamHealth.Tests;

public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan t) => _now += t;
    public long Ms => _now.ToUnixTimeMilliseconds();
}

public class HealthEngineTests
{
    private readonly FakeClock _clock = new();
    private readonly HealthEngine _e;
    public HealthEngineTests() { _e = new HealthEngine(_clock); }

    private void Config(string id = "cam-001", double fps = 3, bool enabled = true) =>
        _e.OnConfig(id, new CameraConfig(id, "Gate 3", fps, enabled));

    private void Status(string state = "online", double fps = 3, string worker = "fx-1", int reconnects = 0, string id = "cam-001", string? error = null,
        string? mode = null, double? target = null) =>
        _e.OnStatus(new CameraStatus(id, worker, state, fps, _clock.Ms, reconnects, error, _clock.Ms, mode, target));

    /// <summary>Advance 1 s at a time, ticking, re-sending status every 5 s via <paramref name="feed"/>.</summary>
    private void Run(int seconds, Action? feed = null)
    {
        for (var i = 0; i < seconds; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            if (feed != null && i % 5 == 0) feed();
            _e.Tick();
        }
    }

    private CameraHealth H(string id = "cam-001") => _e.GetHealth(id)!;

    private void ClassStatus(string state, string? errorClass, double? skipped = null, string id = "cam-001", double fps = 0) =>
        _e.OnStatus(new CameraStatus(id, "fx-1", state, fps, _clock.Ms, 0, errorClass is null ? null : "ffmpeg: " + errorClass, _clock.Ms, null, null, errorClass, skipped));

    [Theory]
    [InlineData("auth_failed", "Wrong credentials")]
    [InlineData("not_found", "Stream path not found")]
    [InlineData("codec_unsupported", "Codec not supported")]
    public void Terminal_classes_are_offline_never_degraded_with_hint(string cls, string hint)
    {
        Config();
        ClassStatus("online", cls);          // even if the extractor still labels the state otherwise
        Assert.Equal(("offline", cls, hint), (H().State, H().ErrorClass, H().ErrorHint));
        for (var i = 0; i < 6; i++) { _clock.Advance(TimeSpan.FromSeconds(5)); ClassStatus("error", cls); _e.Tick(); }
        Assert.Equal(("offline", cls), (H().State, H().ErrorClass));   // hot-loop / backoff cycles never flip to degraded
        ClassStatus("reconnecting", cls);
        Assert.Equal("offline", H().State);
    }

    [Fact]
    public void Transient_class_keeps_the_normal_state_rules_and_gets_a_hint()
    {
        Config();
        ClassStatus("reconnecting", "unreachable");
        var h = H();
        Assert.Equal(("degraded", "unreachable", "Camera unreachable"), (h.State, h.ErrorClass, h.ErrorHint));
    }

    [Fact]
    public void Missing_class_and_motion_are_null_and_class_clears_on_recovery()
    {
        Config(); Status();
        var h = H();
        Assert.Null(h.ErrorClass); Assert.Null(h.ErrorHint); Assert.Null(h.MotionSkippedPct);
        ClassStatus("error", "auth_failed");
        Assert.Equal("auth_failed", H().ErrorClass);
        _clock.Advance(TimeSpan.FromSeconds(1));
        ClassStatus("online", null, skipped: 82.5, fps: 3);
        Assert.Equal(("online", null, 82.5), (H().State, H().ErrorClass, H().MotionSkippedPct));
    }

    [Fact]
    public void Stale_status_drops_class_and_skip_rate()
    {
        Config(); ClassStatus("error", "not_found", 10);
        _clock.Advance(TimeSpan.FromSeconds(20));
        var h = H();
        Assert.Equal("offline", h.State);
        Assert.Null(h.ErrorClass); Assert.Null(h.MotionSkippedPct);
    }

    [Fact]
    public void Class_change_triggers_a_publish()
    {
        Config(); ClassStatus("reconnecting", "unreachable"); _e.Tick();
        _clock.Advance(TimeSpan.FromSeconds(1)); ClassStatus("error", "auth_failed");
        Assert.Contains(_e.Tick().Publish, h => h.ErrorClass == "auth_failed");
    }

    [Fact]
    public void Summary_counts_error_classes_non_zero_only()
    {
        Config(); Config("cam-002"); Config("cam-003"); Config("cam-004");
        ClassStatus("error", "auth_failed"); ClassStatus("error", "auth_failed", id: "cam-002");
        ClassStatus("error", "not_found", id: "cam-003");
        Status(id: "cam-004");
        var s = _e.Summary();
        Assert.Equal(new Dictionary<string, int> { ["auth_failed"] = 2, ["not_found"] = 1 }, s.ByErrorClass);
        Assert.Equal(3, s.Offline);
        Assert.Empty(new HealthEngine(_clock).Summary().ByErrorClass);
    }

    private void MotionCam(bool enabled = true, double hb = 5) =>
        _e.OnConfig("cam-001", new CameraConfig("cam-001", "Gate 3", 3, true, new MotionCfg(enabled, hb)));

    [Fact]
    public void Motion_gated_camera_with_low_fps_is_online()
    {
        MotionCam();
        Run(30, () => ClassStatus("online", null, 79, fps: 1));
        Assert.Equal("online", H().State);
    }

    [Fact]
    public void Motion_gated_camera_degrades_when_last_frame_older_than_heartbeat_plus_5s()
    {
        MotionCam();
        _e.OnStatus(new CameraStatus("cam-001", "fx-1", "online", 1, _clock.Ms - 11_000, 0, null, _clock.Ms, null, null, null, 79));
        Run(12, () => _e.OnStatus(new CameraStatus("cam-001", "fx-1", "online", 1, _clock.Ms - 11_000, 0, null, _clock.Ms, null, null, null, 79)));
        Assert.Equal("degraded", H().State);
    }

    [Fact]
    public void Motion_disabled_keeps_the_fps_rule()
    {
        MotionCam(enabled: false);
        ClassStatus("online", null, fps: 1);
        Assert.Equal("degraded", H().State);
    }

    [Fact]
    public void New_status_fields_parse_and_old_status_still_does()
    {
        var s = JsonSerializer.Deserialize<CameraStatus>("""{"camera_id":"c","state":"error","fps_actual":0,"reconnects":0,"ts":1,"error_class":"auth_failed","motion_skipped_pct":12.5}""", Json.Options)!;
        Assert.Equal(("auth_failed", 12.5), (s.ErrorClass, s.MotionSkippedPct));
        var old = JsonSerializer.Deserialize<CameraStatus>("""{"camera_id":"c","state":"online","fps_actual":3,"reconnects":0,"ts":1}""", Json.Options)!;
        Assert.Null(old.ErrorClass); Assert.Null(old.MotionSkippedPct);
    }

    [Fact]
    public void Unassigned_without_status_or_when_disabled()
    {
        Config();
        Assert.Equal("unassigned", H().State);
        Config(enabled: false);
        Status();
        Assert.Equal("unassigned", H().State);
    }

    [Fact]
    public void Online_with_good_fps()
    {
        Config(); Status(fps: 2.9);
        var h = H();
        Assert.Equal("online", h.State);
        Assert.Equal("fx-1", h.WorkerId);
        Assert.Equal(3, h.FpsTarget);
        Assert.Equal("Gate 3", h.Name);
    }

    [Fact]
    public void Degraded_when_fps_below_70_percent_of_target()
    {
        Config(); Status(fps: 2.0);       // 2.0 < 2.1
        Assert.Equal("degraded", H().State);
        Status(fps: 2.1);                 // exactly 70 % is fine
        Assert.Equal("online", H().State);
    }

    [Fact]
    public void Degraded_only_after_condition_holds_for_10s_and_a_blip_never_shows()
    {
        Config(); Status(fps: 3);
        Run(2, () => Status(fps: 3));
        Status(fps: 1.0);                                   // CPU pressure blip
        Assert.Equal("online", H().State);
        Run(5, () => Status(fps: 1.0));
        Assert.Equal("online", H().State);
        Status(fps: 3);                                     // recovered before 10 s
        Assert.Equal("online", H().State);
        Status(fps: 1.0);                                   // new episode restarts the 10 s timer
        Run(9, () => Status(fps: 1.0));
        Assert.Equal("online", H().State);
        Run(2, () => Status(fps: 1.0));
        Assert.Equal("degraded", H().State);
    }

    [Fact]
    public void Offline_for_full_mode_still_15s_even_with_hold()
    {
        Config(); Status(); Run(16);
        Assert.Equal("offline", H().State);
    }

    [Fact]
    public void Idle_camera_with_recent_frames_is_idle_and_counts_in_summary()
    {
        Config(fps: 3); Status(fps: 0.2, mode: "idle", target: 0.2);
        var h = H();
        Assert.Equal("idle", h.State);
        Assert.Equal("idle", h.Mode);
        Assert.Equal(0.2, h.FpsTarget);
        var s = _e.Summary();
        Assert.Equal(1, s.Idle);
        Assert.Equal(0, s.Online);
    }

    [Fact]
    public void Idle_camera_does_not_compare_against_analysis_fps_and_missing_mode_means_full()
    {
        Config(fps: 3); Status(fps: 0.2, mode: "idle", target: 0.2);
        Run(30, () => Status(fps: 0.2, mode: "idle", target: 0.2));
        Assert.Equal("idle", H().State);
        Config("cam-002", fps: 3); Status(id: "cam-002", fps: 3);     // old extractor: no mode/fps_target
        Assert.Equal("online", H("cam-002").State);
        Assert.Equal("full", H("cam-002").Mode);
        Assert.Equal(3, H("cam-002").FpsTarget);
    }

    [Fact]
    public void Idle_offline_threshold_is_45s_and_full_target_uses_status_fps_target()
    {
        Config(fps: 3); Status(fps: 0.2, mode: "idle", target: 0.2);
        Run(44); Assert.Equal("idle", H().State);
        Run(2); Assert.Equal("offline", H().State);
        Config("cam-002", fps: 3); Status(id: "cam-002", fps: 1.0, mode: "full", target: 1.0);   // target 1 fps: 1.0 is fine
        Assert.Equal("online", H("cam-002").State);
    }

    [Fact]
    public void Idle_with_stale_frames_degrades_after_hold()
    {
        Config(fps: 3);
        _e.OnStatus(new CameraStatus("cam-001", "fx-1", "online", 0.2, _clock.Ms, 0, null, _clock.Ms, "idle", 0.2));
        Assert.Equal("idle", H().State);
        for (var i = 0; i < 40; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            if (i % 5 == 0) _e.OnStatus(new CameraStatus("cam-001", "fx-1", "online", 0.2, _clock.Ms - 35_000, 0, null, _clock.Ms, "idle", 0.2));
            _e.Tick();
        }
        Assert.Equal("degraded", H().State);
    }

    [Fact]
    public void Degraded_with_three_reconnects_in_ten_minutes_and_recovers_after()
    {
        Config(); Status(reconnects: 0);
        Run(5, () => Status(reconnects: 0));
        Status(reconnects: 1); Status(reconnects: 2);
        Assert.Equal("online", H().State);
        Status(reconnects: 3);
        Assert.Equal("online", H().State);      // held: degraded only after 10 s
        Run(11, () => Status(reconnects: 3));
        Assert.Equal("degraded", H().State);
        Assert.Equal(3, H().Reconnects1h);
        Run(601, () => Status(reconnects: 3));  // > 10 min later
        Assert.Equal("online", H().State);
        Assert.Equal(3, H().Reconnects1h);      // still inside the 1 h window
    }

    [Fact]
    public void Reconnects_1h_window_expires()
    {
        Config(); Status(reconnects: 0);
        Status(reconnects: 2);
        Assert.Equal(2, H().Reconnects1h);
        Run(3599, () => Status(reconnects: 2));
        Assert.Equal(2, H().Reconnects1h);
        Run(5, () => Status(reconnects: 2));
        Assert.Equal(0, H().Reconnects1h);
    }

    [Fact]
    public void Counter_reset_after_worker_restart_is_not_a_negative_or_double_count()
    {
        Config(); Status(reconnects: 5); Status(reconnects: 6);
        Assert.Equal(1, H().Reconnects1h);
        Status(reconnects: 0);   // process restarted
        Status(reconnects: 1);
        Assert.Equal(2, H().Reconnects1h);
    }

    [Fact]
    public void Offline_after_15s_without_status_when_owned_unknown()
    {
        Config(); Status();
        Run(14);
        Assert.Equal("online", H().State);
        Run(2);
        Assert.Equal("offline", H().State);
    }

    [Fact]
    public void Silent_camera_becomes_unassigned_when_registry_has_no_owner()
    {
        Config(); Status();
        _e.SetOwned([]);
        Run(16);
        Assert.Equal("unassigned", H().State);
        _e.SetOwned(["cam-001"]);
        Assert.Equal("offline", H().State);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("offline")]
    public void Error_and_offline_states_become_offline(string state)
    {
        Config(); Status();
        Run(1);
        Status(state);
        if (state == "offline") Run(16);   // release: held for the 15 s handover window, then offline
        Assert.Equal("offline", H().State);
    }

    [Theory]
    [InlineData("stalled")]
    [InlineData("reconnecting")]
    [InlineData("starting")]
    public void Transitional_states_count_as_degraded(string state)
    {
        Config(); Status(state);
        Assert.Equal("degraded", H().State);
    }

    [Fact]
    public void Worker_handover_does_not_cause_downtime_or_reconnect()
    {
        Config(); Status(worker: "fx-1", reconnects: 4);
        Run(60, () => Status(worker: "fx-1", reconnects: 4));
        Assert.Equal(100.0, H().UptimePct1h);
        Status("offline", worker: "fx-1");             // fx-1 releases the camera
        Run(3);
        Assert.Equal("online", H().State);              // held during handover
        Status(worker: "fx-2", reconnects: 0);          // fx-2 picks it up (own counter)
        Run(30, () => Status(worker: "fx-2", reconnects: 0));
        var h = H();
        Assert.Equal("online", h.State);
        Assert.Equal("fx-2", h.WorkerId);
        Assert.Equal(100.0, h.UptimePct1h);
        Assert.Equal(0, h.Reconnects1h);
    }

    [Fact]
    public void Late_release_from_old_owner_does_not_override_new_owner()
    {
        Config(); Status(worker: "fx-1");
        Status(worker: "fx-2");
        Status("offline", worker: "fx-1");
        Assert.Equal("online", H().State);
        Assert.Equal("fx-2", H().WorkerId);
    }

    [Fact]
    public void Released_camera_with_no_new_owner_goes_unassigned_or_offline_after_15s()
    {
        Config(); Status();
        Status("offline");
        _e.SetOwned([]);
        Run(16);
        Assert.Equal("unassigned", H().State);
        _e.SetOwned(["cam-001"]);
        Assert.Equal("offline", H().State);
    }

    [Fact]
    public void Uptime_is_ratio_over_rolling_hour()
    {
        Config(); Status();
        Run(100, () => Status());      // up
        Run(100);                      // goes offline after 15 s of silence => ~85 down
        var pct = H().UptimePct1h;
        Assert.InRange(pct, 50.0, 60.0);
        // after an hour of being up the earlier downtime has rolled out
        Run(3700, () => Status());
        Assert.Equal(100.0, H().UptimePct1h);
    }

    [Fact]
    public void Publishes_on_change_and_every_10s_only()
    {
        Config(); Status();
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(_e.Tick().Publish);            // first sighting
        var published = 0;
        for (var i = 0; i < 29; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            if (i % 5 == 0) Status();
            published += _e.Tick().Publish.Count;
        }
        Assert.InRange(published, 2, 3);             // ~ every 10 s over 29 s
        Status(fps: 1.0);                            // change => immediate, but degraded is held for 10 s
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("online", Assert.Single(_e.Tick().Publish).State);
        _clock.Advance(TimeSpan.FromSeconds(10));
        Status(fps: 1.0);
        var r = _e.Tick();
        Assert.Equal("degraded", Assert.Single(r.Publish).State);
    }

    [Fact]
    public void Deleted_camera_emits_tombstone_once()
    {
        Config(); Status();
        _clock.Advance(TimeSpan.FromSeconds(1)); _e.Tick();
        _e.OnConfig("cam-001", null);
        Assert.Equal(new[] { "cam-001" }, _e.Tick().Tombstones);
        Assert.Empty(_e.Tick().Tombstones);
        Assert.Null(_e.GetHealth("cam-001"));
    }

    [Fact]
    public void Summary_counts_states()
    {
        Config("a"); Status(id: "a");
        Config("b"); Status(id: "b", fps: 1);
        Config("c");
        Config("d"); Status("error", id: "d");
        var s = _e.Summary();
        Assert.Equal((4, 1, 1, 1, 1, 0), (s.Total, s.Online, s.Degraded, s.Offline, s.Unassigned, s.Idle));
        Assert.Empty(s.ByErrorClass);
    }

    [Fact]
    public void Handles_400_cameras_cheaply()
    {
        for (var i = 0; i < 400; i++) { Config($"c{i}"); Status(id: $"c{i}"); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 60; i++) { _clock.Advance(TimeSpan.FromSeconds(1)); _e.Tick(); }
        Assert.True(sw.ElapsedMilliseconds < 1000, $"60 ticks of 400 cameras took {sw.ElapsedMilliseconds} ms");
    }
}

public class JsonContractTests
{
    [Fact]
    public void Health_matches_contract_5_6()
    {
        var h = new CameraHealth("cam-001", "Gate 3", "online", "fx-1", 2.9, 3, 1761030000123, 2, 99.2, null, 1761030000600);
        var names = JsonDocument.Parse(JsonSerializer.Serialize(h, Json.Options)).RootElement.EnumerateObject().Select(p => p.Name);
        Assert.Equal(new[] { "camera_id", "name", "state", "worker_id", "fps_actual", "fps_target", "last_frame_ts",
            "reconnects_1h", "uptime_pct_1h", "last_error", "ts", "mode", "error_class", "error_hint", "motion_skipped_pct" }, names);
    }

    [Fact]
    public void Status_and_config_parse_from_contract_examples()
    {
        var s = JsonSerializer.Deserialize<CameraStatus>(
            """{ "camera_id": "cam-001", "worker_id": "fx-1", "state": "online", "fps_actual": 2.9, "last_frame_ts": 1761030000123, "reconnects": 2, "error": null, "ts": 1761030000500 }""",
            Json.Options)!;
        Assert.Equal(("cam-001", "fx-1", 2.9, 2, 1761030000500), (s.CameraId, s.WorkerId, s.FpsActual, s.Reconnects, s.Ts));
        var c = JsonSerializer.Deserialize<CameraConfig>(
            """{ "camera_id": "cam-001", "name": "Gate 3", "source_url": "rtsp://x", "analysis_fps": 3, "enabled": true, "plan_id": null, "tags": ["gate"], "updated_at": 1761030000000 }""",
            Json.Options)!;
        Assert.Equal(("cam-001", 3.0, true), (c.CameraId, c.AnalysisFps, c.Enabled));
    }

    [Fact]
    public void Summary_field_names()
    {
        var names = JsonDocument.Parse(JsonSerializer.Serialize(new HealthSummary(1, 1, 0, 0, 0), Json.Options)).RootElement.EnumerateObject().Select(p => p.Name);
        Assert.Equal(new[] { "total", "online", "degraded", "offline", "unassigned", "idle", "by_error_class" }, names);
    }
}
