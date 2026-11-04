using System.Collections.Concurrent;
using System.Text.Json;

namespace Executor;

/// <summary>
/// In-memory views of the compacted topics: camera -> config and plan_id -> plan (latest + a few older versions for
/// heavy jobs). Plans are immutable records; a swap is one dictionary write, and a frame holds its own reference, so a
/// frame that started on v1 finishes on v1 while new frames pick up v2.
/// </summary>
public sealed class PlanStore
{
    private const int KeepVersions = 4;

    private readonly ConcurrentDictionary<string, CameraConfig> _cameras = new();
    private readonly ConcurrentDictionary<string, Plan> _plans = new();
    private readonly ConcurrentDictionary<(string, int), Plan> _versions = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _ready.Task;
    public bool IsReady => _ready.Task.IsCompletedSuccessfully;
    public void MarkReady() => _ready.TrySetResult();
    public int CameraCount => _cameras.Count;
    public int PlanCount => _plans.Count;

    public void ApplyCamera(string cameraId, CameraConfig? cfg)
    {
        if (cfg is null) _cameras.TryRemove(cameraId, out _);
        else _cameras[cameraId] = cfg;
    }

    public void ApplyPlan(string planId, Plan? plan)
    {
        if (plan is null)
        {
            _plans.TryRemove(planId, out _);
            foreach (var k in _versions.Keys) if (k.Item1 == planId) _versions.TryRemove(k, out _);
            return;
        }
        // never regress to an older version (re-read after rebalance etc.)
        _plans.AddOrUpdate(planId, plan, (_, old) => plan.Version >= old.Version ? plan : old);
        _versions[(planId, plan.Version)] = plan;
        _versions.TryRemove((planId, plan.Version - KeepVersions), out _);
    }

    public void ApplyCameraJson(string key, byte[]? value)
    {
        if (value is null || value.Length == 0) { ApplyCamera(key, null); return; }
        ApplyCamera(key, JsonSerializer.Deserialize(value, Json.Default.CameraConfig));
    }

    public void ApplyPlanJson(string key, byte[]? value)
    {
        if (value is null || value.Length == 0) { ApplyPlan(key, null); return; }
        ApplyPlan(key, JsonSerializer.Deserialize(value, Json.Default.Plan));
    }

    public Plan? GetVersion(string planId, int version) =>
        _versions.TryGetValue((planId, version), out var p) ? p
        : _plans.TryGetValue(planId, out var l) ? l : null;

    /// <summary>
    /// Picks the plan for a camera (contract 9.8: no built-in default plan). Returns null + reason when the frame must
    /// not be processed: "camera_disabled" (enabled=false) or "no_plan" (unknown camera, plan_id null, plan missing or
    /// not active).
    /// </summary>
    public (Plan? Plan, string? SkipReason) Resolve(string cameraId)
    {
        if (!_cameras.TryGetValue(cameraId, out var cam)) return (null, "no_plan");
        if (!cam.Enabled) return (null, "camera_disabled");
        if (cam.PlanId is null) return (null, "no_plan");
        return _plans.TryGetValue(cam.PlanId, out var plan) && plan.Status == "active" ? (plan, null) : (null, "no_plan");
    }
}
