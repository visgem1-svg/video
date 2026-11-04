namespace Registry.Assignment;

public sealed record WorkerRow(string WorkerId, int Capacity, long LastSeenMs, int? CapacityOverride = null, bool Draining = false)
{
    public int EffectiveCapacity => Math.Max(0, CapacityOverride ?? Capacity);
}

public sealed record RebalanceResult(
    IReadOnlyList<WorkerRow> LiveWorkers,
    IReadOnlyList<string> DeadWorkerIds,
    IReadOnlyDictionary<string, string> Assignments,
    IReadOnlyList<string> RemovedCameras,
    IReadOnlyDictionary<string, string> UpsertedAssignments,
    IReadOnlyList<string> PinFallbacks);

/// <summary>
/// Pure assignment function (no I/O, deterministic). Rules, in order:
///  0. pinned cameras go to their pinned worker when it is live, not draining and has capacity (else: fall back, reported in PinFallbacks);
///  1. sticky: other assignments stay when the worker is live, not draining and within capacity;
///  2. everything else goes to the least-loaded eligible worker with free capacity (tie: worker id);
///     a camera on a draining worker with nowhere else to go stays there instead of becoming unassigned.
/// Draining workers receive no new cameras. Effective capacity = override ?? reported.
/// All concurrency control lives in <see cref="IAssignmentStore"/>, which serialises calls.
/// </summary>
public static class Assigner
{
    public static RebalanceResult Rebalance(
        IReadOnlyCollection<WorkerRow> workers,
        IReadOnlyDictionary<string, string> current,
        IReadOnlyCollection<string> enabledCameraIds,
        long nowMs,
        long leaseMs,
        IReadOnlyDictionary<string, string>? pins = null,
        IReadOnlyDictionary<string, double>? weights = null,
        IReadOnlyDictionary<string, long>? switchedAt = null)
    {
        const double Eps = 1e-9;
        double W(string cam) => weights is not null && weights.TryGetValue(cam, out var x) ? x : 1.0;

        var live = new List<WorkerRow>();
        var dead = new List<string>();
        foreach (var w in workers.OrderBy(w => w.WorkerId, StringComparer.Ordinal))
        {
            if (nowMs - w.LastSeenMs < leaseMs) live.Add(w); else dead.Add(w.WorkerId);
        }

        var enabled = new HashSet<string>(enabledCameraIds, StringComparer.Ordinal);
        var eligible = live.Where(w => !w.Draining).ToList();
        var liveIds = live.Select(w => w.WorkerId).ToHashSet(StringComparer.Ordinal);
        var load = live.ToDictionary(w => w.WorkerId, _ => 0.0, StringComparer.Ordinal);
        var cap = live.ToDictionary(w => w.WorkerId, w => w.EffectiveCapacity, StringComparer.Ordinal);
        var eligibleIds = eligible.Select(w => w.WorkerId).ToHashSet(StringComparer.Ordinal);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var fallbacks = new List<string>();

        // 0. pins
        if (pins is not null)
            foreach (var (cam, p) in pins.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!enabled.Contains(cam)) continue;
                if (eligibleIds.Contains(p) && load[p] + W(cam) <= cap[p] + Eps) { result[cam] = p; load[p] += W(cam); }
                else fallbacks.Add(cam);
            }

        // 1. sticky: everything stays where it is; a worker pushed over capacity (cameras switched to full) sheds the
        //    minimum: heaviest first (fewest moves), least recently switched first among equals. Pinned cameras never move.
        var sticky = new List<string>();
        foreach (var (cam, w) in current.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (result.ContainsKey(cam)) continue;
            if (enabled.Contains(cam) && eligibleIds.Contains(w)) { result[cam] = w; load[w] += W(cam); sticky.Add(cam); }
        }
        foreach (var w in eligible)
        {
            if (load[w.WorkerId] <= cap[w.WorkerId] + Eps) continue;
            var victims = sticky.Where(c => result[c] == w.WorkerId)
                .OrderByDescending(W).ThenBy(c => switchedAt is not null && switchedAt.TryGetValue(c, out var t) ? t : 0L)
                .ThenBy(c => c, StringComparer.Ordinal).ToList();
            foreach (var v in victims)
            {
                if (load[w.WorkerId] <= cap[w.WorkerId] + Eps) break;
                result.Remove(v); load[w.WorkerId] -= W(v);
            }
        }

        // 2. place the rest
        foreach (var cam in enabled.Where(c => !result.ContainsKey(c)).OrderBy(c => c, StringComparer.Ordinal))
        {
            string? best = null;
            var bestLoad = double.MaxValue;
            foreach (var w in eligible) // ordered by id => deterministic tie-break
            {
                var l = load[w.WorkerId];
                if (l + W(cam) <= cap[w.WorkerId] + Eps && l < bestLoad) { best = w.WorkerId; bestLoad = l; }
            }
            if (best is not null) { result[cam] = best; load[best] = bestLoad + W(cam); }
            else if (current.TryGetValue(cam, out var old) && liveIds.Contains(old) && !eligibleIds.Contains(old))
                result[cam] = old; // draining worker, no other room yet: keep running rather than going dark
        }

        var removed = current.Keys.Where(c => !result.ContainsKey(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var upserts = result.Where(kv => !current.TryGetValue(kv.Key, out var o) || o != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        // a pin is only a "fallback" if the camera did not end up on its pinned worker
        var fb = fallbacks.Where(c => result.TryGetValue(c, out var w) ? pins![c] != w : true).ToList();
        return new RebalanceResult(live, dead, result, removed, upserts, fb);
    }
}
