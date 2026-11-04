namespace Executor;

/// <summary>
/// A recognition is a sighting event, not a per-frame result (contract 9.6): the same (camera, type, value) is emitted at
/// most once per cooldown; unknown faces use a shorter per-camera cooldown. Expired keys are swept periodically so memory
/// stays bounded by the number of distinct sightings inside one cooldown window.
/// </summary>
public sealed class RecognitionCooldown
{
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _until = new();
    private readonly long _cooldownMs, _unknownMs;
    private readonly TimeProvider _time;
    private long _lastSweep;

    public RecognitionCooldown(int cooldownS, int unknownFaceCooldownS, TimeProvider? time = null)
    {
        _cooldownMs = cooldownS * 1000L;
        _unknownMs = unknownFaceCooldownS * 1000L;
        _time = time ?? TimeProvider.System;
    }

    public int Tracked { get { lock (_lock) return _until.Count; } }

    /// <summary>True = emit; false = suppressed (and counted).</summary>
    public bool Allow(Recognition r)
    {
        var unknownFace = r.Type == "face" && r.Value == "unknown";
        var window = unknownFace ? _unknownMs : _cooldownMs;
        if (window <= 0) return true;
        var key = unknownFace ? r.CameraId + "|face|unknown" : r.CameraId + "|" + r.Type + "|" + r.Value;
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_lock)
        {
            if (now - _lastSweep > 10_000) { Sweep(now); }
            if (_until.TryGetValue(key, out var until) && now < until)
            {
                Telemetry.RecognitionsSuppressed.WithLabels(r.Type).Inc();
                return false;
            }
            _until[key] = now + window;
            return true;
        }
    }

    private void Sweep(long now)
    {
        _lastSweep = now;
        List<string>? dead = null;
        foreach (var (k, u) in _until) if (u <= now) (dead ??= new()).Add(k);
        if (dead is not null) foreach (var k in dead) _until.Remove(k);
    }
}
