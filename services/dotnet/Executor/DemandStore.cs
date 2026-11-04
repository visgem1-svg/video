using System.Collections.Concurrent;
using System.Text.Json;

namespace Executor;

/// <summary>
/// In-memory view of camera.demand (contract 9.8). An entry whose producer timestamp is older than 90 s counts as zero
/// (gateway crash safety). Only live_viewers drives executor priority.
/// </summary>
public sealed class DemandStore
{
    public const long ExpiryMs = 90_000;

    private readonly ConcurrentDictionary<string, CameraDemand> _d = new();
    private readonly TimeProvider _time;

    public DemandStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Apply(string cameraId, CameraDemand? d)
    {
        if (d is null) _d.TryRemove(cameraId, out _);
        else _d[cameraId] = d;
    }

    public void ApplyJson(string key, byte[]? value) =>
        Apply(key, value is null || value.Length == 0 ? null : JsonSerializer.Deserialize(value, Json.Default.CameraDemand));

    public bool IsWatched(string cameraId) =>
        _d.TryGetValue(cameraId, out var d) && d.LiveViewers > 0
        && _time.GetUtcNow().ToUnixTimeMilliseconds() - d.Ts <= ExpiryMs;
}
