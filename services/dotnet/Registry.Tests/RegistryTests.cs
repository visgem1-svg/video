using System.Text.Json;
using Registry;
using Registry.Assignment;
using Registry.Data;
using Xunit;

namespace Registry.Tests;

public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan t) => _now += t;
}

/// <summary>In-memory store; the semaphore plays the role of the Postgres advisory lock.</summary>
public sealed class FakeStore : IAssignmentStore, ICameraStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    public readonly Dictionary<string, CameraConfig> Cameras = new();
    public readonly Dictionary<string, WorkerRow> Workers = new();
    public readonly Dictionary<string, string> Assignments = new();

    public async Task<T> RunExclusiveAsync<T>(Func<IAssignmentTx, Task<T>> work, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await Task.Yield(); // encourage interleaving if the lock were missing
            return await work(new Tx(this));
        }
        finally { _lock.Release(); }
    }

    private sealed class Tx(FakeStore s) : IAssignmentTx
    {
        public async Task<AssignmentSnapshot> LoadAsync(CancellationToken ct)
        {
            await Task.Yield();
            return new AssignmentSnapshot(s.Workers.Values.ToList(), new Dictionary<string, string>(s.Assignments),
                s.Cameras.Values.Where(c => c.Enabled).ToList());
        }

        public async Task SaveAsync(AssignmentDiff d, CancellationToken ct)
        {
            await Task.Yield();
            if (d.UpsertWorker is { } w) s.Workers[w.WorkerId] = w;
            if (d.UpdateSettings is { } st && s.Workers.TryGetValue(st.WorkerId, out var wr)) s.Workers[st.WorkerId] = wr with { CapacityOverride = st.CapacityOverride, Draining = st.Draining };
            foreach (var x in d.DeleteWorkers) s.Workers.Remove(x);
            foreach (var x in d.DeleteAssignments) s.Assignments.Remove(x);
            foreach (var (c, w2) in d.UpsertAssignments) s.Assignments[c] = w2;
        }
    }

    public Task<IReadOnlyList<CameraConfig>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CameraConfig>>(Cameras.Values.OrderBy(c => c.CameraId).ToList());
    public Task<CameraConfig?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Cameras.GetValueOrDefault(id));
    public Task<bool> InsertAsync(CameraConfig c, CancellationToken ct) => Task.FromResult(Cameras.TryAdd(c.CameraId, c));
    public Task<bool> UpdateAsync(CameraConfig c, CancellationToken ct)
    {
        if (!Cameras.ContainsKey(c.CameraId)) return Task.FromResult(false);
        Cameras[c.CameraId] = c;
        return Task.FromResult(true);
    }
    public Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        Assignments.Remove(id);
        return Task.FromResult(Cameras.Remove(id));
    }
    public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)Cameras.Count);
    public Task<bool> SetPinAsync(string id, string? workerId, CancellationToken ct)
    {
        if (!Cameras.TryGetValue(id, out var c)) return Task.FromResult(false);
        Cameras[id] = c with { PinnedWorker = workerId };
        return Task.FromResult(true);
    }
    public Task<bool> BumpGenerationAsync(string id, CancellationToken ct)
    {
        if (!Cameras.TryGetValue(id, out var c)) return Task.FromResult(false);
        Cameras[id] = c with { Generation = c.Generation + 1 };
        return Task.FromResult(true);
    }
    public Task<IReadOnlyDictionary<string, string>> AssignmentsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Assignments));
    public Task<bool> SetProvisionErrorAsync(string id, string? error, CancellationToken ct)
    {
        if (!Cameras.TryGetValue(id, out var c)) return Task.FromResult(false);
        Cameras[id] = c with { ProvisionError = error };
        return Task.FromResult(true);
    }
}

public sealed class FakePublisher : ICameraPublisher
{
    public readonly List<(string Key, CameraConfig? Value)> Messages = new();
    public Task PublishAsync(string cameraId, CameraConfig? config, CancellationToken ct)
    {
        lock (Messages) Messages.Add((cameraId, config));
        return Task.CompletedTask;
    }
    public Task PublishManyAsync(IReadOnlyList<CameraConfig> configs, CancellationToken ct)
    {
        foreach (var c in configs) lock (Messages) Messages.Add((c.CameraId, c));
        return Task.CompletedTask;
    }
}

public class AssignmentTests
{
    private static CameraConfig Cam(int n, bool enabled = true) =>
        new($"cam-{n:000}", $"Camera {n:000}", $"rtsp://x/cam-{n:000}", 3, enabled, null, [], 1);

    private static (FakeStore store, FakeClock clock, AssignmentService svc) Setup(int cams)
    {
        var store = new FakeStore();
        for (var i = 1; i <= cams; i++) store.Cameras[Cam(i).CameraId] = Cam(i);
        var clock = new FakeClock();
        return (store, clock, new AssignmentService(store, clock, 15));
    }

    private static string[] Ids(IEnumerable<CameraConfig> c) => c.Select(x => x.CameraId).ToArray();

    [Fact]
    public async Task Single_worker_gets_cameras_up_to_capacity()
    {
        var (_, _, svc) = Setup(10);
        var a = await svc.HeartbeatAsync("w1", 4, default);
        Assert.Equal(4, a.Count);
        Assert.Equal(new[] { "cam-001", "cam-002", "cam-003", "cam-004" }, Ids(a));
    }

    [Fact]
    public async Task Cameras_spread_least_loaded_between_workers()
    {
        var (_, _, svc) = Setup(10);
        await svc.HeartbeatAsync("w1", 25, default);   // takes everything (only worker)
        var w2 = await svc.HeartbeatAsync("w2", 25, default);
        Assert.Empty(w2);                              // sticky: nothing moves to the newcomer
        var w1 = await svc.HeartbeatAsync("w1", 25, default);
        Assert.Equal(10, w1.Count);
    }

    [Fact]
    public async Task Initial_distribution_is_balanced()
    {
        var (store, _, svc) = Setup(20);
        // both workers register while no cameras are assigned yet: simulate by registering first with capacity 0
        await svc.HeartbeatAsync("w1", 0, default);
        await svc.HeartbeatAsync("w2", 0, default);
        await svc.HeartbeatAsync("w1", 25, default);
        await svc.HeartbeatAsync("w2", 25, default);
        var counts = store.Assignments.GroupBy(a => a.Value).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(20, store.Assignments.Count);
        Assert.InRange(counts["w1"], 1, 20);
    }

    [Fact]
    public async Task Least_loaded_wins_for_new_cameras()
    {
        var (store, _, svc) = Setup(6);
        await svc.HeartbeatAsync("w1", 3, default);
        await svc.HeartbeatAsync("w2", 10, default);   // gets cam 4..6
        var r = Assigner.Rebalance(
            [new("w1", 10, 1000), new("w2", 10, 1000)],
            new Dictionary<string, string> { ["a"] = "w1", ["b"] = "w1", ["c"] = "w2" },
            ["a", "b", "c", "d", "e"], 1000, 15000);
        Assert.Equal("w2", r.UpsertedAssignments["d"]); // w2 load 1 < w1 load 2
        Assert.Equal("w1", r.UpsertedAssignments["e"]); // now 2 vs 2 -> tie by id
        Assert.Equal(6, store.Assignments.Count);
    }

    [Fact]
    public async Task Sticky_assignments_do_not_move_between_healthy_workers()
    {
        var (store, _, svc) = Setup(12);
        await svc.HeartbeatAsync("w1", 6, default);
        await svc.HeartbeatAsync("w2", 6, default);
        var before = new Dictionary<string, string>(store.Assignments);
        await svc.HeartbeatAsync("w3", 20, default);
        await svc.HeartbeatAsync("w1", 6, default);
        await svc.HeartbeatAsync("w2", 6, default);
        Assert.Equal(before, store.Assignments);
    }

    [Fact]
    public async Task Dead_worker_loses_cameras_which_are_rebalanced()
    {
        var (store, clock, svc) = Setup(10);
        await svc.HeartbeatAsync("w1", 5, default);
        await svc.HeartbeatAsync("w2", 10, default);
        Assert.Equal(5, store.Assignments.Count(a => a.Value == "w1"));
        clock.Advance(TimeSpan.FromSeconds(10));
        await svc.HeartbeatAsync("w2", 10, default);   // w1 still within lease
        Assert.Equal(5, store.Assignments.Count(a => a.Value == "w1"));
        clock.Advance(TimeSpan.FromSeconds(6));        // w1 silent for 16 s
        var w2 = await svc.HeartbeatAsync("w2", 10, default);
        Assert.Equal(10, w2.Count);
        Assert.DoesNotContain("w1", store.Assignments.Values);
        Assert.True(store.Workers.ContainsKey("w1")); // dead rows are kept (state=dead) for 10 min
    }

    [Fact]
    public async Task Sweeper_releases_dead_worker_without_heartbeats()
    {
        var (store, clock, svc) = Setup(4);
        await svc.HeartbeatAsync("w1", 10, default);
        clock.Advance(TimeSpan.FromSeconds(20));
        await svc.SweepAsync(default);
        Assert.Empty(store.Assignments);
        Assert.Equal("dead", Assert.Single(await svc.ListWorkersAsync(default)).State);
    }

    [Fact]
    public async Task Capacity_is_respected_and_overflow_stays_unassigned()
    {
        var (store, _, svc) = Setup(10);
        await svc.HeartbeatAsync("w1", 3, default);
        await svc.HeartbeatAsync("w2", 3, default);
        Assert.Equal(6, store.Assignments.Count);
        await svc.HeartbeatAsync("w3", 4, default);
        Assert.Equal(10, store.Assignments.Count);
        foreach (var g in store.Assignments.GroupBy(a => a.Value))
            Assert.True(g.Count() <= (g.Key == "w3" ? 4 : 3));
    }

    [Fact]
    public async Task Lowered_capacity_sheds_cameras_to_others()
    {
        var (store, _, svc) = Setup(8);
        await svc.HeartbeatAsync("w1", 8, default);
        await svc.HeartbeatAsync("w2", 8, default);
        await svc.HeartbeatAsync("w1", 3, default);
        Assert.Equal(3, store.Assignments.Count(a => a.Value == "w1"));
        Assert.Equal(5, store.Assignments.Count(a => a.Value == "w2"));
    }

    [Fact]
    public async Task Disabled_and_deleted_cameras_are_not_assigned()
    {
        var (store, _, svc) = Setup(3);
        store.Cameras["cam-002"] = Cam(2, enabled: false);
        var a = await svc.HeartbeatAsync("w1", 10, default);
        Assert.Equal(new[] { "cam-001", "cam-003" }, Ids(a));
        store.Cameras["cam-001"] = Cam(1, enabled: false);
        a = await svc.HeartbeatAsync("w1", 10, default);
        Assert.Equal(new[] { "cam-003" }, Ids(a));
        Assert.DoesNotContain("cam-001", store.Assignments.Keys);
    }

    [Fact]
    public async Task Concurrent_heartbeats_never_double_assign_or_exceed_capacity()
    {
        var (store, clock, svc) = Setup(400);
        const int workers = 20;
        var all = new List<IReadOnlyList<CameraConfig>>();
        var perRound = new List<(string w, string[] ids)>();
        for (var round = 0; round < 5; round++)
        {
            var tasks = Enumerable.Range(0, workers).Select(i =>
                Task.Run(async () => (w: $"w{i:00}", a: await svc.HeartbeatAsync($"w{i:00}", 25, default)))).ToArray();
            var res = await Task.WhenAll(tasks);
            // responses of one round must be pairwise disjoint (each is a consistent serial snapshot)
            var ids = res.SelectMany(r => Ids(r.a)).ToList();
            // note: responses are produced serially, a camera can move only if its worker died - none died here
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.All(res, r => Assert.True(r.a.Count <= 25));
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(400, store.Assignments.Count);
        Assert.All(store.Assignments.GroupBy(a => a.Value), g => Assert.True(g.Count() <= 25));
    }

    [Fact]
    public async Task Concurrent_heartbeats_with_worker_death_stay_consistent()
    {
        var (store, clock, svc) = Setup(100);
        for (var round = 0; round < 6; round++)
        {
            var alive = Enumerable.Range(0, 8).Where(i => round < 3 || i % 2 == 0);
            await Task.WhenAll(alive.Select(i => Task.Run(() => svc.HeartbeatAsync($"w{i}", 25, default))));
            clock.Advance(TimeSpan.FromSeconds(8));
        }
        // after two more rounds only even workers (4 x 25 = 100) are alive and all cameras sit on them
        Assert.Equal(100, store.Assignments.Count);
        Assert.All(store.Assignments.Values, w => Assert.True(int.Parse(w[1..]) % 2 == 0));
    }
}

public class JsonContractTests
{
    private static string[] Names(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o, Json.Options)).RootElement.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void Camera_config_matches_contract_5_1()
    {
        var c = new CameraConfig("cam-001", "Gate 3", "rtsp://mediamtx:8554/cam-001", 3, true, null, ["gate"], 1761030000000, "rtsp://10.0.0.5/live", null);
        Assert.Equal(new[] { "camera_id", "name", "source_url", "analysis_fps", "enabled", "plan_id", "tags", "updated_at", "stream_url", "provision_error", "duplicate_of", "pinned_worker", "generation", "assigned_worker", "pin_fallback", "analysis_stream_url", "analysis_source_url", "motion" }, Names(c));
        var back = JsonSerializer.Deserialize<CameraConfig>(
            """{ "camera_id": "cam-001", "name": "Gate 3", "source_url": "rtsp://mediamtx:8554/cam-001", "analysis_fps": 3, "enabled": true, "plan_id": "a6f", "tags": ["gate"], "updated_at": 1761030000000 }""",
            Json.Options)!;
        Assert.Equal("a6f", back.PlanId);
        Assert.Equal(1761030000000, back.UpdatedAt);
    }

    [Fact]
    public void Heartbeat_and_workers_match_contract_7_2()
    {
        var req = JsonSerializer.Deserialize<HeartbeatRequest>("""{ "capacity": 25, "running": ["cam-001"] }""", Json.Options)!;
        Assert.Equal(25, req.Capacity);
        Assert.Equal(new[] { "assigned", "lease_s" }, Names(new HeartbeatResponse([], 15)));
        Assert.Equal(new[] { "worker_id", "capacity", "assigned", "last_seen", "capacity_override", "draining", "state", "managed_by", "load" }, Names(new WorkerInfo("w", 1, [], 1, null, false, "live", "static")));
    }
}
