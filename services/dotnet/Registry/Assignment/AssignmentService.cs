namespace Registry.Assignment;

public sealed record AssignmentSnapshot(
    IReadOnlyList<WorkerRow> Workers,
    IReadOnlyDictionary<string, string> Assignments,
    IReadOnlyList<CameraConfig> EnabledCameras);

/// <summary>Operator-set worker settings (never touched by heartbeats).</summary>
public sealed record WorkerSettings(string WorkerId, int? CapacityOverride, bool Draining);

public sealed record AssignmentDiff(
    WorkerRow? UpsertWorker,
    IReadOnlyList<string> DeleteWorkers,
    IReadOnlyList<string> DeleteAssignments,
    IReadOnlyDictionary<string, string> UpsertAssignments,
    WorkerSettings? UpdateSettings = null);

public interface IAssignmentTx
{
    Task<AssignmentSnapshot> LoadAsync(CancellationToken ct);
    Task SaveAsync(AssignmentDiff diff, CancellationToken ct);
}

/// <summary>
/// Runs the work with an exclusive, cluster-wide lock (Postgres advisory xact lock), so concurrent
/// heartbeats - even across several registry replicas - are strictly serialised.
/// </summary>
public interface IAssignmentStore
{
    Task<T> RunExclusiveAsync<T>(Func<IAssignmentTx, Task<T>> work, CancellationToken ct);
}

public sealed class AssignmentService(IAssignmentStore store, TimeProvider clock, int leaseSeconds, ExtractModeEngine? modes = null)
{
    private sealed record Eval(IReadOnlyDictionary<string, Extract> Modes, IReadOnlyDictionary<string, long> Switched, Dictionary<string, double> Weights);

    private static readonly Extract AlwaysFull = new(Extract.Full, 0);

    /// <summary>Extraction mode + load weight per enabled camera (contract 9.8). Without an engine everything is full / weight 1.</summary>
    private Eval Evaluate(AssignmentSnapshot s)
    {
        if (modes is null)
            return new Eval(new Dictionary<string, Extract>(), new Dictionary<string, long>(), new Dictionary<string, double>());
        var snap = modes.Resolve(s.EnabledCameras);
        var w = new Dictionary<string, double>(snap.Modes.Count, StringComparer.Ordinal);
        foreach (var (id, e) in snap.Modes) w[id] = modes.Weight(e);
        return new Eval(snap.Modes, snap.SwitchedAt, w);
    }

    private static Dictionary<string, double> Loads(IEnumerable<KeyValuePair<string, string>> assignments, Eval ev)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (cam, w) in assignments) d[w] = d.GetValueOrDefault(w) + ev.Weights.GetValueOrDefault(cam, 1.0);
        return d;
    }

    private RebalanceResult Run(AssignmentSnapshot snap, IReadOnlyCollection<WorkerRow> workers, long now, Eval ev) =>
        Assigner.Rebalance(workers, snap.Assignments, snap.EnabledCameras.Select(c => c.CameraId).ToList(),
            now, leaseSeconds * 1000L, Pins(snap), ev.Weights, ev.Switched);

    public const long DeadRetentionMs = 600_000;

    public int LeaseSeconds => leaseSeconds;

    private static IReadOnlyDictionary<string, string> Pins(AssignmentSnapshot s) =>
        s.EnabledCameras.Where(c => c.PinnedWorker is not null).ToDictionary(c => c.CameraId, c => c.PinnedWorker!);

    private static IReadOnlyList<string> PurgeList(RebalanceResult r, IEnumerable<WorkerRow> workers, long now) =>
        r.DeadWorkerIds.Where(id => workers.First(w => w.WorkerId == id) is var w && now - w.LastSeenMs > DeadRetentionMs).ToList();

    public async Task<IReadOnlyList<CameraConfig>> HeartbeatAsync(string workerId, int capacity, CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        return await store.RunExclusiveAsync(async tx =>
        {
            var snap = await tx.LoadAsync(ct);
            var existing = snap.Workers.FirstOrDefault(w => w.WorkerId == workerId);
            var me = (existing ?? new WorkerRow(workerId, capacity, now)) with { Capacity = capacity, LastSeenMs = now };
            var workers = snap.Workers.Where(w => w.WorkerId != workerId).Append(me).ToList();
            var ev = Evaluate(snap);
            var r = Run(snap, workers, now, ev);
            await tx.SaveAsync(new AssignmentDiff(me, PurgeList(r, workers, now), r.RemovedCameras, r.UpsertedAssignments), ct);
            RegistryMetrics.PublishWorkers(workers, r, now, leaseSeconds * 1000L, Loads(r.Assignments, ev));
            RegistryMetrics.PublishModes(ev.Modes);
            var byId = snap.EnabledCameras.ToDictionary(c => c.CameraId);
            return (IReadOnlyList<CameraConfig>)r.Assignments
                .Where(kv => kv.Value == workerId).Select(kv => byId[kv.Key] with { Extract = ev.Modes.GetValueOrDefault(kv.Key, AlwaysFull with { Fps = byId[kv.Key].AnalysisFps }) })
                .OrderBy(c => c.CameraId, StringComparer.Ordinal).ToList();
        }, ct);
    }

    /// <summary>Expire dead workers and rebalance without a heartbeat (periodic sweeper; also applies operator changes at once).</summary>
    public Task SweepAsync(CancellationToken ct) => store.RunExclusiveAsync<bool>(tx => SweepInTx(tx, null, ct), ct);

    private async Task<bool> SweepInTx(IAssignmentTx tx, WorkerSettings? settings, CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var snap = await tx.LoadAsync(ct);
        var workers = snap.Workers.Select(w => settings is not null && w.WorkerId == settings.WorkerId
            ? w with { CapacityOverride = settings.CapacityOverride, Draining = settings.Draining } : w).ToList();
        var ev = Evaluate(snap);
        var r = Run(snap, workers, now, ev);
        var purge = PurgeList(r, workers, now);
        if (settings is not null || purge.Count > 0 || r.RemovedCameras.Count > 0 || r.UpsertedAssignments.Count > 0)
            await tx.SaveAsync(new AssignmentDiff(null, purge, r.RemovedCameras, r.UpsertedAssignments, settings), ct);
        RegistryMetrics.PublishWorkers(workers, r, now, leaseSeconds * 1000L, Loads(r.Assignments, ev));
        RegistryMetrics.PublishModes(ev.Modes);
        return true;
    }

    /// <summary>Operator change (drain / undrain / capacity override) applied and rebalanced in one exclusive step. False = unknown worker.</summary>
    public Task<bool> UpdateWorkerAsync(string workerId, Func<WorkerRow, WorkerSettings> change, CancellationToken ct) =>
        store.RunExclusiveAsync(async tx =>
        {
            var snap = await tx.LoadAsync(ct);
            var w = snap.Workers.FirstOrDefault(x => x.WorkerId == workerId);
            if (w is null) return false;
            return await SweepInTx(tx, change(w), ct);
        }, ct);

    public Task<bool> DrainAsync(string workerId, bool drain, CancellationToken ct) =>
        UpdateWorkerAsync(workerId, w => new WorkerSettings(w.WorkerId, w.CapacityOverride, drain), ct);

    public Task<bool> SetCapacityOverrideAsync(string workerId, int? capacity, CancellationToken ct) =>
        UpdateWorkerAsync(workerId, w => new WorkerSettings(w.WorkerId, capacity, w.Draining), ct);

    public async Task<IReadOnlyList<WorkerInfo>> ListWorkersAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        return await store.RunExclusiveAsync(async tx =>
        {
            var snap = await tx.LoadAsync(ct);
            var ev = Evaluate(snap);
            var loads = Loads(snap.Assignments, ev);
            return (IReadOnlyList<WorkerInfo>)snap.Workers
                .OrderBy(w => w.WorkerId, StringComparer.Ordinal)
                .Select(w =>
                {
                    var dead = now - w.LastSeenMs >= leaseSeconds * 1000L;
                    return new WorkerInfo(w.WorkerId, w.EffectiveCapacity,
                        dead ? [] : snap.Assignments.Where(a => a.Value == w.WorkerId).Select(a => a.Key).Order(StringComparer.Ordinal).ToList(),
                        w.LastSeenMs, w.CapacityOverride, w.Draining,
                        dead ? "dead" : w.Draining ? "draining" : "live",
                        w.WorkerId.StartsWith("fx-auto-", StringComparison.Ordinal) ? "controller" : "static",
                        dead ? 0 : Math.Round(loads.GetValueOrDefault(w.WorkerId), 2));
                }).ToList();
        }, ct);
    }
}
