using System.Text.Json;
using Registry;
using Registry.Assignment;
using Xunit;

namespace Registry.Tests;

public class WorkerOpsTests
{
    private readonly FakeStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly AssignmentService _assign;
    private readonly CameraService _cams;

    public WorkerOpsTests()
    {
        for (var i = 1; i <= 10; i++)
        {
            var id = $"cam-{i:000}";
            _store.Cameras[id] = new CameraConfig(id, id, "rtsp://x/" + id, 3, true, null, [], 1);
        }
        _assign = new AssignmentService(_store, _clock, 15);
        _cams = new CameraService(_store, new FakePublisher(), _clock, new FakeMediaMtx());
    }

    private int Count(string w) => _store.Assignments.Count(a => a.Value == w);

    [Fact]
    public async Task Drain_moves_cameras_to_least_loaded_live_workers_and_completes()
    {
        await _assign.HeartbeatAsync("w1", 10, default);          // takes all 10
        await _assign.HeartbeatAsync("w2", 10, default);
        await _assign.HeartbeatAsync("w3", 10, default);
        Assert.Equal(10, Count("w1"));
        Assert.True(await _assign.DrainAsync("w1", true, default));
        Assert.Equal(0, Count("w1"));                              // drain completed
        Assert.Equal(5, Count("w2"));
        Assert.Equal(5, Count("w3"));
        // draining worker gets nothing new, even on its own heartbeat
        Assert.Empty(await _assign.HeartbeatAsync("w1", 10, default));
        var info = (await _assign.ListWorkersAsync(default)).Single(w => w.WorkerId == "w1");
        Assert.Equal("draining", info.State);
        Assert.True(info.Draining);
    }

    [Fact]
    public async Task Drain_keeps_cameras_when_nobody_else_has_room_and_finishes_when_room_appears()
    {
        await _assign.HeartbeatAsync("w1", 10, default);
        Assert.True(await _assign.DrainAsync("w1", true, default));
        Assert.Equal(10, Count("w1"));                             // nowhere to go: still running, not dark
        await _assign.HeartbeatAsync("w2", 10, default);           // autoscaler brings a new worker
        Assert.Equal(0, Count("w1"));
        Assert.Equal(10, Count("w2"));
    }

    [Fact]
    public async Task Undrain_makes_worker_eligible_again_and_unknown_worker_is_rejected()
    {
        await _assign.HeartbeatAsync("w1", 10, default);
        await _assign.HeartbeatAsync("w2", 10, default);
        await _assign.DrainAsync("w1", true, default);
        await _assign.DrainAsync("w1", false, default);
        Assert.False(_store.Workers["w1"].Draining);
        Assert.Equal("live", (await _assign.ListWorkersAsync(default)).Single(w => w.WorkerId == "w1").State);
        Assert.False(await _assign.DrainAsync("ghost", true, default));
    }

    [Fact]
    public async Task Drain_state_survives_heartbeats()
    {
        await _assign.HeartbeatAsync("w1", 10, default);
        await _assign.DrainAsync("w1", true, default);
        await _assign.HeartbeatAsync("w1", 12, default);
        Assert.True(_store.Workers["w1"].Draining);
        Assert.Equal(12, _store.Workers["w1"].Capacity);
    }

    [Fact]
    public async Task Capacity_override_wins_over_reported_capacity_and_can_be_cleared()
    {
        await _assign.HeartbeatAsync("w1", 10, default);
        await _assign.HeartbeatAsync("w2", 10, default);
        Assert.True(await _assign.SetCapacityOverrideAsync("w1", 3, default));
        Assert.Equal(3, Count("w1"));
        Assert.Equal(7, Count("w2"));
        await _assign.HeartbeatAsync("w1", 10, default);          // worker keeps reporting 10; override persists
        Assert.Equal(3, Count("w1"));
        var info = (await _assign.ListWorkersAsync(default)).Single(w => w.WorkerId == "w1");
        Assert.Equal(3, info.CapacityOverride);
        Assert.Equal(3, info.Capacity);                            // effective
        Assert.True(await _assign.SetCapacityOverrideAsync("w1", null, default));
        Assert.Null(_store.Workers["w1"].CapacityOverride);
        Assert.False(await _assign.SetCapacityOverrideAsync("ghost", 1, default));
    }

    [Fact]
    public async Task Pin_moves_camera_to_live_worker_and_stays_there()
    {
        await _assign.HeartbeatAsync("w1", 10, default);
        await _assign.HeartbeatAsync("w2", 10, default);
        Assert.Equal("w1", _store.Assignments["cam-001"]);
        Assert.NotNull(await _cams.PinAsync("cam-001", "w2", default));
        await _assign.SweepAsync(default);
        Assert.Equal("w2", _store.Assignments["cam-001"]);
        await _assign.HeartbeatAsync("w1", 10, default);
        Assert.Equal("w2", _store.Assignments["cam-001"]);
        var cam = await _cams.GetAsync("cam-001", default);
        Assert.Equal("w2", cam!.AssignedWorker);
        Assert.False(cam.PinFallback);
    }

    [Fact]
    public async Task Pin_to_dead_worker_falls_back_and_reports_then_returns_when_worker_is_back()
    {
        await _assign.HeartbeatAsync("w1", 10, default);
        await _cams.PinAsync("cam-001", "w-dead", default);        // never seen
        await _assign.SweepAsync(default);
        Assert.Equal("w1", _store.Assignments["cam-001"]);          // fallback placement
        var cam = await _cams.GetAsync("cam-001", default);
        Assert.True(cam!.PinFallback);
        Assert.Equal("w-dead", cam.PinnedWorker);
        await _assign.HeartbeatAsync("w-dead", 10, default);        // pinned worker shows up
        Assert.Equal("w-dead", _store.Assignments["cam-001"]);
        Assert.False((await _cams.GetAsync("cam-001", default))!.PinFallback);
        _clock.Advance(TimeSpan.FromSeconds(20));                   // and dies again
        await _assign.HeartbeatAsync("w1", 10, default);
        Assert.Equal("w1", _store.Assignments["cam-001"]);
        Assert.True((await _cams.GetAsync("cam-001", default))!.PinFallback);
    }

    [Fact]
    public async Task Pin_to_full_or_draining_worker_falls_back()
    {
        await _assign.HeartbeatAsync("w1", 10, default);
        await _assign.HeartbeatAsync("w2", 1, default);
        await _cams.PinAsync("cam-001", "w2", default);
        await _cams.PinAsync("cam-002", "w2", default);             // w2 only fits one
        await _assign.SweepAsync(default);
        Assert.Equal(1, Count("w2"));
        Assert.Equal("w2", _store.Assignments["cam-001"]);
        Assert.True((await _cams.GetAsync("cam-002", default))!.PinFallback);
        await _assign.DrainAsync("w2", true, default);
        Assert.Equal(0, Count("w2"));
        Assert.True((await _cams.GetAsync("cam-001", default))!.PinFallback);
    }

    [Fact]
    public async Task Unpin_keeps_camera_where_it_is()
    {
        await _assign.HeartbeatAsync("w1", 10, default);
        await _assign.HeartbeatAsync("w2", 10, default);
        await _cams.PinAsync("cam-001", "w2", default);
        await _assign.SweepAsync(default);
        await _cams.PinAsync("cam-001", null, default);
        await _assign.HeartbeatAsync("w1", 10, default);
        Assert.Equal("w2", _store.Assignments["cam-001"]);
        Assert.Null(await _cams.PinAsync("nope-cam", "w1", default));
    }

    [Fact]
    public async Task Restart_bumps_generation_and_heartbeat_carries_it()
    {
        var first = await _assign.HeartbeatAsync("w1", 10, default);
        Assert.All(first, c => Assert.Equal(0, c.Generation));
        var r = await _cams.RestartAsync("cam-003", default);
        Assert.Equal(1, r!.Generation);
        await _cams.RestartAsync("cam-003", default);
        var hb = await _assign.HeartbeatAsync("w1", 10, default);
        Assert.Equal(2, hb.Single(c => c.CameraId == "cam-003").Generation);
        Assert.Equal(0, hb.Single(c => c.CameraId == "cam-004").Generation);
        Assert.Contains("\"generation\":2", JsonSerializer.Serialize(hb.Single(c => c.CameraId == "cam-003"), Json.Options));
        Assert.Null(await _cams.RestartAsync("nope-cam", default));
    }

    [Fact]
    public async Task Update_keeps_pin_and_generation()
    {
        await _cams.PinAsync("cam-001", "w1", default);
        await _cams.RestartAsync("cam-001", default);
        var u = await _cams.UpdateAsync("cam-001", new CameraInput(null, "renamed", null, 3, true, null, null, "publisher"), default);
        Assert.Equal("w1", u!.PinnedWorker);
        Assert.Equal(1, u.Generation);
        Assert.Equal(1, _store.Cameras["cam-001"].Generation);
    }

    [Fact]
    public async Task Workers_list_has_state_and_managed_by()
    {
        await _assign.HeartbeatAsync("fx-1", 10, default);
        await _assign.HeartbeatAsync("fx-auto-3", 10, default);
        await _assign.HeartbeatAsync("fx-old", 10, default);
        _clock.Advance(TimeSpan.FromSeconds(10));
        await _assign.HeartbeatAsync("fx-1", 10, default);
        await _assign.HeartbeatAsync("fx-auto-3", 10, default);
        _clock.Advance(TimeSpan.FromSeconds(10));                   // fx-old silent for 20 s
        var l = await _assign.ListWorkersAsync(default);
        Assert.Equal("controller", l.Single(w => w.WorkerId == "fx-auto-3").ManagedBy);
        Assert.Equal("static", l.Single(w => w.WorkerId == "fx-1").ManagedBy);
        Assert.Equal("dead", l.Single(w => w.WorkerId == "fx-old").State);
        Assert.Empty(l.Single(w => w.WorkerId == "fx-old").Assigned);
    }

    [Fact]
    public async Task Dead_workers_are_purged_after_retention()
    {
        await _assign.HeartbeatAsync("w1", 10, default);
        _clock.Advance(TimeSpan.FromMinutes(11));
        await _assign.SweepAsync(default);
        Assert.Empty(_store.Workers);
    }
}
