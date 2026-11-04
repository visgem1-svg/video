using Registry;
using Registry.Assignment;
using Xunit;

namespace Registry.Tests;

public class ExtractModeTests
{
    private readonly FakeStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly ExtractModeEngine _engine;
    private readonly AssignmentService _svc;
    private readonly CameraService _cams;

    public ExtractModeTests()
    {
        _engine = new ExtractModeEngine(_clock);
        _svc = new AssignmentService(_store, _clock, 15, _engine);
        _cams = new CameraService(_store, new FakePublisher(), _clock, new FakeMediaMtx(), null, "rtsp://mediamtx:8554", _engine);
    }

    private void Add(string id, string? plan = null, bool enabled = true, double fps = 3) =>
        _store.Cameras[id] = new CameraConfig(id, id, "rtsp://x/" + id, fps, enabled, plan, [], 1);

    private long Ms => _clock.GetUtcNow().ToUnixTimeMilliseconds();
    private string Mode(string id) => _engine.Resolve(_store.Cameras.Values.ToList()).Modes[id].Mode;
    private void Advance(int s) => _clock.Advance(TimeSpan.FromSeconds(s));

    [Fact]
    public void Plan_camera_is_full_at_analysis_fps_and_planless_goes_idle_after_60s()
    {
        Add("cam-001", "p1", fps: 4); Add("cam-002");
        var m = _engine.Resolve(_store.Cameras.Values.ToList()).Modes;
        Assert.Equal(new Extract("full", 4), m["cam-001"]);
        Assert.Equal("full", m["cam-002"].Mode); // restart: everyone starts full
        Advance(59); Assert.Equal("full", Mode("cam-002"));
        Advance(1);
        var m2 = _engine.Resolve(_store.Cameras.Values.ToList()).Modes;
        Assert.Equal(new Extract("idle", 0.2), m2["cam-002"]);
        Assert.Equal("full", m2["cam-001"].Mode);
    }

    [Fact]
    public void Frame_viewers_force_full_immediately_and_hysteresis_holds_full_for_60s_after_demand_ends()
    {
        Add("cam-001");
        Mode("cam-001"); Advance(61);
        Assert.Equal("idle", Mode("cam-001"));
        _engine.UpdateDemand("cam-001", 0, 1, Ms);
        Assert.Equal("full", Mode("cam-001"));            // idle -> full immediately
    }

    [Fact]
    public void Full_to_idle_only_after_60s_without_demand()
    {
        Add("cam-001");
        _engine.UpdateDemand("cam-001", 0, 2, Ms);
        Assert.Equal("full", Mode("cam-001"));
        Advance(30); _engine.UpdateDemand("cam-001", 0, 0, Ms); // viewer left
        Assert.Equal("full", Mode("cam-001"));
        Advance(29); Assert.Equal("full", Mode("cam-001"));    // 59 s since last demand
        Advance(1); Assert.Equal("idle", Mode("cam-001"));     // 60 s
    }

    [Fact]
    public void Stale_demand_older_than_90s_counts_as_zero()
    {
        Add("cam-001");
        _engine.UpdateDemand("cam-001", 0, 3, Ms);
        Advance(89); Assert.Equal("full", Mode("cam-001"));    // fresh
        Advance(2);                                             // entry now 91 s old -> zero; last demand was 2 s ago
        Assert.Equal("full", Mode("cam-001"));
        Advance(60); Assert.Equal("idle", Mode("cam-001"));
    }

    [Fact]
    public void Live_viewers_alone_do_not_force_full_and_disabled_is_never_demanded()
    {
        Add("cam-001"); Add("cam-002", "p1", enabled: false);
        Mode("cam-001"); Advance(61);
        _engine.UpdateDemand("cam-001", 5, 0, Ms);
        Assert.Equal("idle", Mode("cam-001"));
        Assert.Equal("idle", Mode("cam-002"));
    }

    [Fact]
    public async Task Weighted_placement_lets_a_worker_hold_many_idle_cameras()
    {
        for (var i = 1; i <= 8; i++) Add($"cam-{i:000}", "p1");
        for (var i = 9; i <= 48; i++) Add($"cam-{i:000}");
        await _svc.HeartbeatAsync("w1", 12, default);
        Advance(61);                                     // plan-less go idle: 8 + 40 * 0.1 = 12
        var a = await _svc.HeartbeatAsync("w1", 12, default);
        Assert.Equal(48, a.Count);
        Assert.Equal(48, _store.Assignments.Count);
        Assert.Equal(40, a.Count(c => c.Extract!.Mode == "idle"));
        Assert.All(a.Where(c => c.Extract!.Mode == "idle"), c => Assert.Equal(0.2, c.Extract!.Fps));
        var w = (await _svc.ListWorkersAsync(default)).Single();
        Assert.Equal(12.0, w.Load);
    }

    [Fact]
    public async Task Initial_placement_counts_full_weight()
    {
        for (var i = 1; i <= 20; i++) Add($"cam-{i:000}");
        var a = await _svc.HeartbeatAsync("w1", 12, default);
        Assert.Equal(12, a.Count);                       // everyone starts full
    }

    [Fact]
    public async Task Mode_change_does_not_move_cameras_between_healthy_workers()
    {
        for (var i = 1; i <= 10; i++) Add($"cam-{i:000}");
        await _svc.HeartbeatAsync("w1", 0, default); await _svc.HeartbeatAsync("w2", 0, default);
        await _svc.HeartbeatAsync("w1", 20, default); await _svc.HeartbeatAsync("w2", 20, default);
        var before = new Dictionary<string, string>(_store.Assignments);
        Advance(61);
        await _svc.HeartbeatAsync("w1", 20, default); await _svc.HeartbeatAsync("w2", 20, default);
        Assert.Equal(before, _store.Assignments);
    }

    [Fact]
    public async Task Over_capacity_after_switch_to_full_moves_minimum_oldest_switched_first()
    {
        // w1 capacity 5, w2 capacity 5; 10 idle cameras, w1 holds 8 of them (0.8), w2 holds 2
        for (var i = 1; i <= 10; i++) Add($"cam-{i:000}");
        await _svc.HeartbeatAsync("w1", 100, default);
        Advance(61);
        await _svc.HeartbeatAsync("w1", 100, default);   // all idle on w1
        await _svc.HeartbeatAsync("w2", 100, default);
        Assert.Equal(10, _store.Assignments.Count(a => a.Value == "w1"));
        // shrink w1 so that 4 full cameras no longer fit together with the idle rest: cap 4.5 -> use override
        Assert.True(await _svc.SetCapacityOverrideAsync("w1", 5, default));
        // 10 idle = 1.0 load on w1, all stay. Now 5 become full: 5*1.0 + 5*0.1 = 5.5 > 5
        foreach (var id in new[] { "cam-001", "cam-002", "cam-003", "cam-004", "cam-005" })
            { _store.Cameras[id] = _store.Cameras[id] with { PlanId = "p" }; Advance(1); _engine.Resolve(_store.Cameras.Values.ToList()); }
        var before = new Dictionary<string, string>(_store.Assignments);
        await _svc.SweepAsync(default);
        var moved = _store.Assignments.Where(kv => before[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
        Assert.Single(moved);                             // minimum: one full camera
        Assert.Equal("cam-001", moved[0]);                // least recently switched among the full ones
        Assert.Equal("w2", _store.Assignments["cam-001"]);
    }

    [Fact]
    public void Assigner_over_capacity_respects_pins_and_moves_only_what_is_needed()
    {
        var workers = new[] { new WorkerRow("w1", 3, 1000), new WorkerRow("w2", 10, 1000) };
        var current = new Dictionary<string, string> { ["a"] = "w1", ["b"] = "w1", ["c"] = "w1", ["d"] = "w1" };
        var pins = new Dictionary<string, string> { ["a"] = "w1" };
        var r = Assigner.Rebalance(workers, current, ["a", "b", "c", "d"], 1000, 15000, pins);
        Assert.Equal("w1", r.Assignments["a"]);
        Assert.Equal(3, r.Assignments.Count(kv => kv.Value == "w1"));
        Assert.Single(r.UpsertedAssignments);
    }

    [Fact]
    public async Task Api_cameras_carry_extract_and_camera_config_does_not()
    {
        Add("cam-001", "p1"); Add("cam-002");
        Mode("cam-001"); Advance(61);
        var list = await _cams.ListApiAsync(default);
        Assert.Equal("full", list.Single(c => c.CameraId == "cam-001").Extract!.Mode);
        Assert.Equal("idle", list.Single(c => c.CameraId == "cam-002").Extract!.Mode);
        var json = System.Text.Json.JsonSerializer.Serialize(_store.Cameras["cam-001"], Json.Options);
        Assert.DoesNotContain("extract", json);          // computed only; never stored or published to camera.config
    }
}
