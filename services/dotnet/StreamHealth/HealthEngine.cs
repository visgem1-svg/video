namespace StreamHealth;

public sealed record TickResult(IReadOnlyList<CameraHealth> Publish, IReadOnlyList<string> Tombstones);

/// <summary>
/// In-memory camera.health computation (contract 5.6). Thread-safe (single lock; all operations are O(1)
/// per camera so 400+ cameras cost microseconds per tick).
///
/// Rolling windows:
///  * uptime: 360 buckets x 10 s = 1 h per camera, running sums updated incrementally on bucket advance.
///  * reconnects: queue of (ts, delta) events, pruned on tick; 10 min count scans only the tail.
/// </summary>
public sealed class HealthEngine(TimeProvider clock)
{
    public const long OfflineAfterMs = 15_000;
    public const long IdleOfflineAfterMs = 45_000;   // idle = keyframes only (GOP up to 10 s), contract 9.8
    public const long IdleFramesWithinMs = 30_000;
    public const long DegradedHoldMs = 10_000;       // degraded only after the condition held this long (anti-flap)
    public const long PublishEveryMs = 10_000;
    public const long BucketMs = 10_000;
    public const int Buckets = 360;
    private const long HourMs = 3_600_000;
    private const long TenMinMs = 600_000;

    private sealed class Cam
    {
        public CameraConfig? Config;
        public CameraStatus? Status;
        public long StatusAtMs;
        public long CreatedAtMs;
        public int? LastCounter;
        public string? LastWorker;
        public string? PrevState;   // effective state before a release ("offline" status), held during the handover grace
        public bool PrevUp;
        public long DegradedSinceMs;  // 0 = condition not currently true
        public bool WasHealthy;       // reached online/idle at least once since the current owner started
        public readonly Queue<(long ts, int n)> Reconnects = new();
        public readonly int[] Up = new int[Buckets];
        public readonly int[] Total = new int[Buckets];
        public long CurBucket = -1;
        public long UpSum, TotalSum;
        public CameraHealth? LastPublished;
        public long LastPublishedAt;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Cam> _cams = new(StringComparer.Ordinal);
    private readonly List<string> _pendingTombstones = new();

    private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public void OnConfig(string cameraId, CameraConfig? config)
    {
        lock (_gate)
        {
            if (config is null)
            {
                if (_cams.Remove(cameraId, out var c) && c.LastPublished is not null) _pendingTombstones.Add(cameraId);
                return;
            }
            Get(cameraId).Config = config;
        }
    }

    public void OnStatus(CameraStatus s)
    {
        var now = Now();
        lock (_gate)
        {
            var c = Get(s.CameraId);
            // A late "offline" (release) from a previous owner must not override a fresh status from a new owner.
            if (s.State == "offline" && c.Status is { State: not "offline" } cur && cur.WorkerId != s.WorkerId
                && now - c.StatusAtMs <= OfflineAfterMs)
                return;
            c.Status = s;
            c.StatusAtMs = now;
            if (s.State == "offline") return; // final release: no counter information, no reconnect
            // reconnect counter is cumulative per worker process: count increases; re-baseline on reset/handover
            if (c.LastWorker == s.WorkerId && c.LastCounter is { } last && s.Reconnects > last)
                c.Reconnects.Enqueue((now, s.Reconnects - last));
            c.LastCounter = s.Reconnects;
            c.LastWorker = s.WorkerId;
        }
    }

    private HashSet<string>? _owned; // cameras the registry currently assigns to a live worker; null = unknown

    /// <summary>Registry view (GET /api/workers). Used to tell "unassigned" from "offline" after a release.</summary>
    public void SetOwned(IEnumerable<string>? cameraIds)
    {
        lock (_gate) _owned = cameraIds is null ? null : new HashSet<string>(cameraIds, StringComparer.Ordinal);
    }

    private Cam Get(string id)
    {
        if (!_cams.TryGetValue(id, out var c)) _cams[id] = c = new Cam { CreatedAtMs = Now() };
        return c;
    }

    private bool HasOwner(string id) => _owned is null || _owned.Contains(id);

    /// <summary>Sample uptime, detect changes. Call about once per second.</summary>
    public TickResult Tick()
    {
        var now = Now();
        var publish = new List<CameraHealth>();
        lock (_gate)
        {
            foreach (var (id, c) in _cams)
            {
                if (c.Config is null) continue;
                var h = Compute(id, c, now, includeSample: true);
                if (h is null) continue;
                if (c.LastPublished is null || Changed(c.LastPublished, h) || now - c.LastPublishedAt >= PublishEveryMs)
                {
                    c.LastPublished = h;
                    c.LastPublishedAt = now;
                    publish.Add(h);
                }
            }
            var tomb = _pendingTombstones.ToArray();
            _pendingTombstones.Clear();
            return new TickResult(publish, tomb);
        }
    }

    /// <summary>Current view of all cameras (for the HTTP API), without sampling side effects.</summary>
    public IReadOnlyList<CameraHealth> Snapshot()
    {
        var now = Now();
        lock (_gate)
        {
            var list = new List<CameraHealth>(_cams.Count);
            foreach (var (id, c) in _cams.OrderBy(k => k.Key, StringComparer.Ordinal))
                if (Compute(id, c, now, includeSample: false) is { } h) list.Add(h);
            return list;
        }
    }

    public CameraHealth? GetHealth(string id)
    {
        lock (_gate) return _cams.TryGetValue(id, out var c) ? Compute(id, c, Now(), false) : null;
    }

    public HealthSummary Summary()
    {
        var all = Snapshot();
        var byClass = all.Where(h => h.ErrorClass is not null).GroupBy(h => h.ErrorClass!)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        return new HealthSummary(all.Count, all.Count(h => h.State == "online"), all.Count(h => h.State == "degraded"),
            all.Count(h => h.State == "offline"), all.Count(h => h.State == "unassigned"), all.Count(h => h.State == "idle"), byClass);
    }

    private static bool Changed(CameraHealth a, CameraHealth b) =>
        a.State != b.State || a.Mode != b.Mode || a.WorkerId != b.WorkerId || a.Name != b.Name || a.FpsTarget != b.FpsTarget ||
        a.Reconnects1h != b.Reconnects1h || a.LastError != b.LastError || a.ErrorClass != b.ErrorClass || Math.Abs(a.FpsActual - b.FpsActual) >= 0.05;
    // last_frame_ts moves every frame and is deliberately not a change trigger: it rides on the 10 s refresh.

    private CameraHealth? Compute(string id, Cam c, long now, bool includeSample)
    {
        var cfg = c.Config;
        if (cfg is null) return null;
        var st = c.Status;
        PruneReconnects(c, now);
        var r1h = c.Reconnects.Sum(e => e.n);
        var r10 = 0;
        foreach (var e in c.Reconnects) if (e.ts >= now - TenMinMs) r10 += e.n;

        var idleMode = st?.Mode == "idle";
        var offlineMs = idleMode ? IdleOfflineAfterMs : OfflineAfterMs;
        var healthyName = idleMode ? "idle" : "online";
        var target = st?.FpsTarget is > 0 ? st.FpsTarget.Value : cfg.AnalysisFps;
        string state;
        var statusUp = false;
        var held = false;
        if (!cfg.Enabled) state = "unassigned";
        else if (st is null) state = HasOwner(id) && now - c.CreatedAtMs > OfflineAfterMs ? "offline" : "unassigned";
        else if (st.State == "offline" && now - c.StatusAtMs <= OfflineAfterMs && c.PrevState is not null)
        {
            // final release by the previous owner: hold the previous state for the handover window
            state = c.PrevState; statusUp = c.PrevUp; held = true;
        }
        else if (now - c.StatusAtMs > offlineMs || st.State == "offline")
            state = HasOwner(id) ? "offline" : "unassigned";   // silent (or released) for > 15 s
        else if (st.State == "error" || ErrorClasses.IsTerminal(st.ErrorClass)) state = "offline"; // 5.6 + 9.9: error / terminal class => offline, never degraded
        else if (st.State == "online")
        {
            statusUp = true;
            var framesStale = idleMode && st.LastFrameTs is { } lf && c.StatusAtMs - lf > IdleFramesWithinMs;
            // Motion gate on (contract 9.9): low fps is expected, so liveness = a frame within heartbeat_s + 5 s.
            var gated = !idleMode && (cfg.Motion?.Enabled == true || st.MotionSkippedPct is not null);
            var gatedStale = gated && (st.LastFrameTs is not { } gl || c.StatusAtMs - gl > (long)((cfg.Motion?.HeartbeatS ?? 5) * 1000) + 5_000);
            var bad = r10 >= 3 || (idleMode ? framesStale : gated ? gatedStale : st.FpsActual < 0.7 * target);
            state = bad ? "degraded" : healthyName;
        }
        else state = "degraded"; // starting | stalled | reconnecting: owned but frames not flowing normally

        // Anti-flap: a degraded verdict is only reported once it has held for DegradedHoldMs. Until then the
        // camera keeps its healthy label - unless it never was healthy (a fresh start shows its real state).
        if (state == "degraded")
        {
            if (c.DegradedSinceMs == 0) c.DegradedSinceMs = now;
            if (c.WasHealthy && now - c.DegradedSinceMs < DegradedHoldMs) state = healthyName;
        }
        else
        {
            c.DegradedSinceMs = 0;
            c.WasHealthy = state is "online" or "idle";
        }

        if (!held) { c.PrevState = state; c.PrevUp = statusUp; }
        if (includeSample && cfg.Enabled) Sample(c, now, statusUp);

        var pct = c.TotalSum == 0 ? 0.0 : Math.Round(100.0 * c.UpSum / c.TotalSum, 1);
        // The class / skip rate describe the CURRENT status only: dropped once the status is stale, released or handed over.
        var fresh = st is not null && !held && st.State != "offline" && now - c.StatusAtMs <= offlineMs;
        var errorClass = fresh ? st!.ErrorClass : null;
        return new CameraHealth(id, cfg.Name, state, st?.WorkerId, st?.FpsActual ?? 0, target, st?.LastFrameTs,
            r1h, pct, st?.Error, now, idleMode ? "idle" : "full", errorClass, ErrorClasses.Hint(errorClass),
            fresh ? st!.MotionSkippedPct : null);
    }

    private static void PruneReconnects(Cam c, long now)
    {
        while (c.Reconnects.Count > 0 && c.Reconnects.Peek().ts < now - HourMs) c.Reconnects.Dequeue();
    }

    private static void Sample(Cam c, long now, bool up)
    {
        var idx = now / BucketMs;
        if (c.CurBucket < 0) c.CurBucket = idx;
        if (idx != c.CurBucket)
        {
            var steps = (int)Math.Min(idx - c.CurBucket, Buckets);
            for (var s = 1; s <= steps; s++)
            {
                var slot = (int)((c.CurBucket + s) % Buckets);
                c.UpSum -= c.Up[slot]; c.TotalSum -= c.Total[slot];
                c.Up[slot] = 0; c.Total[slot] = 0;
            }
            c.CurBucket = idx;
        }
        var cur = (int)(idx % Buckets);
        c.Total[cur]++; c.TotalSum++;
        if (up) { c.Up[cur]++; c.UpSum++; }
    }
}
