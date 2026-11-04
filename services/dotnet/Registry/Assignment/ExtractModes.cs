using System.Collections.Concurrent;

namespace Registry.Assignment;

/// <summary>Contract 9.8: what the extractor should do for a camera. Mode is runtime state, never published to camera.config.</summary>
public sealed record Extract(string Mode, double Fps)
{
    public const string Full = "full";
    public const string Idle = "idle";
}

public sealed record ModeSnapshot(IReadOnlyDictionary<string, Extract> Modes, IReadOnlyDictionary<string, long> SwitchedAt);

/// <summary>
/// Single decision point for extraction mode (contract 9.8). Demand comes from camera.demand (latest per camera, in memory).
/// full  = enabled and (plan_id set or fresh frame_viewers > 0); idle otherwise, after 60 s without demand (hysteresis).
/// A camera first seen by this process (registry restart) starts as full and settles through the same hysteresis.
/// </summary>
public sealed class ExtractModeEngine(TimeProvider clock, double idleWeight = 0.1, double idleFps = 0.2,
    long hysteresisMs = 60_000, long demandTtlMs = 90_000)
{
    private sealed class State { public bool Full = true; public long LastDemandMs; public long SwitchedMs; }

    private readonly ConcurrentDictionary<string, (int Live, int Frame, long Ts)> _demand = new(StringComparer.Ordinal);
    private readonly Dictionary<string, State> _state = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public double IdleWeight => idleWeight;
    public double Weight(Extract e) => e.Mode == Extract.Full ? 1.0 : idleWeight;

    public void UpdateDemand(string cameraId, int liveViewers, int frameViewers, long tsMs) =>
        _demand[cameraId] = (liveViewers, frameViewers, tsMs);

    public void RemoveDemand(string cameraId) => _demand.TryRemove(cameraId, out _);

    private int FrameViewers(string id, long now) =>
        _demand.TryGetValue(id, out var d) && now - d.Ts < demandTtlMs ? d.Frame : 0;

    /// <summary>Evaluates the cameras at "now" and advances hysteresis. <paramref name="prune"/> forgets cameras not in the list.</summary>
    public ModeSnapshot Resolve(IReadOnlyCollection<CameraConfig> cameras, bool prune = false)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var modes = new Dictionary<string, Extract>(cameras.Count, StringComparer.Ordinal);
        var switched = new Dictionary<string, long>(cameras.Count, StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var c in cameras)
            {
                if (!_state.TryGetValue(c.CameraId, out var s))
                    _state[c.CameraId] = s = new State { Full = true, LastDemandMs = now, SwitchedMs = now };
                var demand = c.Enabled && (c.PlanId is not null || FrameViewers(c.CameraId, now) > 0);
                if (demand)
                {
                    s.LastDemandMs = now;
                    if (!s.Full) { s.Full = true; s.SwitchedMs = now; }
                }
                else if (s.Full && now - s.LastDemandMs >= hysteresisMs)
                {
                    s.Full = false; s.SwitchedMs = now;
                }
                modes[c.CameraId] = s.Full ? new Extract(Extract.Full, c.AnalysisFps) : new Extract(Extract.Idle, idleFps);
                switched[c.CameraId] = s.SwitchedMs;
            }
            if (prune)
            {
                var keep = cameras.Select(c => c.CameraId).ToHashSet(StringComparer.Ordinal);
                foreach (var k in _state.Keys.Where(k => !keep.Contains(k)).ToList()) { _state.Remove(k); _demand.TryRemove(k, out _); }
            }
        }
        return new ModeSnapshot(modes, switched);
    }
}
