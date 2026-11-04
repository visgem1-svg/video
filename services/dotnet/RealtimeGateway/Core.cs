using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Prometheus;

namespace RealtimeGateway;

/// <summary>
/// Per-camera rate limiter with drop-oldest semantics: at most <c>maxPerSecond</c> sends in any 1 s window.
/// A message that cannot be sent immediately becomes the camera's single pending slot, replacing (dropping) any older
/// pending message; <see cref="Flush"/> releases it as soon as the window allows.
/// </summary>
public sealed class DetectionThrottler<T> where T : class
{
    private sealed class Slot
    {
        public readonly object Lock = new();
        public readonly long[] Sent;
        public int Next, Count;
        public T? Pending;
        public Slot(int n) => Sent = new long[n];
    }

    private readonly int _max;
    private readonly long _windowMs;
    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    public long Dropped;
    /// <summary>Called for every message dropped because a newer one superseded it.</summary>
    public Action? OnDrop { get; set; }

    public DetectionThrottler(int maxPerSecond = 3, long windowMs = 1000)
    {
        _max = maxPerSecond;
        _windowMs = windowMs;
    }

    private bool CanSend(Slot s, long now)
    {
        if (s.Count < _max) return true;
        // Sent is a ring of the last _max send times; the oldest is at s.Next
        return now - s.Sent[s.Next] >= _windowMs;
    }

    private void Record(Slot s, long now)
    {
        s.Sent[s.Next] = now;
        s.Next = (s.Next + 1) % _max;
        if (s.Count < _max) s.Count++;
    }

    /// <summary>Returns the payload when it may be sent right now; otherwise stores it as pending and returns null.</summary>
    public T? Offer(string camera, T payload, long nowMs)
    {
        var s = _slots.GetOrAdd(camera, _ => new Slot(_max));
        lock (s.Lock)
        {
            if (CanSend(s, nowMs))
            {
                if (s.Pending is not null) { s.Pending = null; Interlocked.Increment(ref Dropped); OnDrop?.Invoke(); } // superseded by the newer one
                Record(s, nowMs);
                return payload;
            }
            if (s.Pending is not null) { Interlocked.Increment(ref Dropped); OnDrop?.Invoke(); }
            s.Pending = payload;
            return null;
        }
    }

    /// <summary>Releases pending messages whose camera now has budget.</summary>
    public void Flush(long nowMs, List<(string Camera, T Payload)> into)
    {
        foreach (var (cam, s) in _slots)
        {
            if (Volatile.Read(ref s.Pending) is null) continue;
            lock (s.Lock)
            {
                if (s.Pending is null || !CanSend(s, nowMs)) continue;
                Record(s, nowMs);
                into.Add((cam, s.Pending));
                s.Pending = null;
            }
        }
    }

    public void Forget(string camera) => _slots.TryRemove(camera, out _);
}

public static class Telemetry
{
    public static readonly Counter In = Metrics.CreateCounter("vp_realtime_messages_in_total", "Kafka messages consumed.", new CounterConfiguration { LabelNames = new[] { "topic" } });
    public static readonly Counter Sent = Metrics.CreateCounter("vp_realtime_sent_total", "Hub sends (one per group/all send, not per client).", new CounterConfiguration { LabelNames = new[] { "method" } });
    public static readonly Counter Dropped = Metrics.CreateCounter("vp_realtime_dropped_total", "Messages not delivered.", new CounterConfiguration { LabelNames = new[] { "reason" } });
    public static readonly Gauge Connections = Metrics.CreateGauge("vp_realtime_connections", "Connected hub clients.");
    public static readonly Counter DemandPublished = Metrics.CreateCounter("vp_realtime_demand_published_total", "camera.demand messages published.");
    public static readonly Gauge WatchedCameras = Metrics.CreateGauge("vp_realtime_watched_cameras", "Cameras with at least one viewer.", new GaugeConfiguration { LabelNames = new[] { "kind" } });
    public static readonly Gauge LagMs = Metrics.CreateGauge("vp_realtime_consumer_lag_ms", "now - Kafka message timestamp of the last consumed message.", new GaugeConfiguration { LabelNames = new[] { "topic" } });
}

/// <summary>Snapshots for new clients (latest camera.health per camera, plans) + subscriber bookkeeping.</summary>
public sealed class LiveState
{
    public readonly ConcurrentDictionary<string, JsonElement> Health = new();
    public readonly ConcurrentDictionary<string, JsonElement> Plans = new();
    public readonly DemandTracker Demand = new();
    private readonly ConcurrentDictionary<string, int> _camSubs = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;
    public void MarkReady() => _ready.TrySetResult();

    public bool HasSubscribers(string camera) => _camSubs.TryGetValue(camera, out var n) && n > 0;
    public int SubscriberCount(string camera) => _camSubs.TryGetValue(camera, out var n) ? n : 0;

    public void AddSub(string camera)
    {
        _camSubs.AddOrUpdate(camera, 1, (_, n) => n + 1);
        Demand.Add(camera, false, Environment.TickCount64);
    }

    public void AddFrameWatch(string camera) => Demand.Add(camera, true, Environment.TickCount64);
    public void RemoveFrameWatch(string camera) => Demand.Remove(camera, true, Environment.TickCount64);

    public void RemoveSub(string camera)
    {
        var n = _camSubs.AddOrUpdate(camera, 0, (_, c) => Math.Max(0, c - 1));
        if (n == 0) _camSubs.TryRemove(new KeyValuePair<string, int>(camera, 0));
        Demand.Remove(camera, false, Environment.TickCount64);
    }
}

/// <summary>SignalR hub /hubs/live (contract 7.7).</summary>
public sealed class LiveHub : Hub
{
    public const int MaxIdsPerCall = 1000;
    private const string CamKey = "cams";
    private const string FrameKey = "frames";

    public static string CamGroup(string id) => "cam:" + id;
    public static string PlanGroup(string id) => "plan:" + id;

    private readonly LiveState _state;

    public LiveHub(LiveState state) => _state = state;

    public override async Task OnConnectedAsync()
    {
        Telemetry.Connections.Inc();
        // snapshot: current health of all cameras and all current plans (clients are not "late" for compacted state)
        foreach (var h in _state.Health.Values) await Clients.Caller.SendAsync("cameraHealth", h);
        foreach (var p in _state.Plans.Values) await Clients.Caller.SendAsync("planChanged", p);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Telemetry.Connections.Dec();
        // SignalR removes the connection from its groups itself; we only fix our subscriber counters
        if (Context.Items.TryGetValue(CamKey, out var o) && o is HashSet<string> set)
            lock (set) foreach (var c in set) _state.RemoveSub(c);
        if (Context.Items.TryGetValue(FrameKey, out var f) && f is HashSet<string> fset)
            lock (fset) foreach (var c in fset) _state.RemoveFrameWatch(c);
        return base.OnDisconnectedAsync(exception);
    }

    private HashSet<string> Cams() =>
        (HashSet<string>)(Context.Items.TryGetValue(CamKey, out var o) ? o! : Context.Items[CamKey] = new HashSet<string>());

    private HashSet<string> Frames() =>
        (HashSet<string>)(Context.Items.TryGetValue(FrameKey, out var o) ? o! : Context.Items[FrameKey] = new HashSet<string>());

    /// <summary>Contract 9.8: this connection looks at extracted frames of these cameras.</summary>
    public Task WatchFrames(string[] cameraIds)
    {
        var set = Frames();
        foreach (var id in Clean(cameraIds))
        {
            bool added;
            lock (set) added = set.Add(id);
            if (added) _state.AddFrameWatch(id);
        }
        return Task.CompletedTask;
    }

    public Task UnwatchFrames(string[] cameraIds)
    {
        var set = Frames();
        foreach (var id in Clean(cameraIds))
        {
            bool removed;
            lock (set) removed = set.Remove(id);
            if (removed) _state.RemoveFrameWatch(id);
        }
        return Task.CompletedTask;
    }

    public async Task SubscribeCameras(string[] cameraIds)
    {
        var set = Cams();
        foreach (var id in Clean(cameraIds))
        {
            bool added;
            lock (set) added = set.Add(id);
            if (!added) continue;
            _state.AddSub(id);
            await Groups.AddToGroupAsync(Context.ConnectionId, CamGroup(id));
        }
    }

    public async Task UnsubscribeCameras(string[] cameraIds)
    {
        var set = Cams();
        foreach (var id in Clean(cameraIds))
        {
            bool removed;
            lock (set) removed = set.Remove(id);
            if (!removed) continue;
            _state.RemoveSub(id);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, CamGroup(id));
        }
    }

    public async Task SubscribePlans(string[] planIds)
    {
        foreach (var id in Clean(planIds))
            await Groups.AddToGroupAsync(Context.ConnectionId, PlanGroup(id));
    }

    private static IEnumerable<string> Clean(string[]? ids) =>
        (ids ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 128).Distinct().Take(MaxIdsPerCall);
}

/// <summary>Routes Kafka events to SignalR clients. Kafka-agnostic so it can be tested.</summary>
public sealed class EventRouter
{
    private readonly IHubContext<LiveHub> _hub;
    private readonly LiveState _state;
    private readonly DetectionThrottler<Boxed> _throttle;
    private readonly Func<long> _clock;

    public sealed class Boxed
    {
        public JsonElement Value;
    }

    public EventRouter(IHubContext<LiveHub> hub, LiveState state, DetectionThrottler<Boxed> throttle, Func<long>? clock = null)
    {
        _hub = hub;
        _state = state;
        _throttle = throttle;
        _clock = clock ?? (() => Environment.TickCount64);
        _throttle.OnDrop = () => Telemetry.Dropped.WithLabels("throttled").Inc();
    }

    private static JsonElement Parse(byte[] b)
    {
        using var d = JsonDocument.Parse(b);
        return d.RootElement.Clone();
    }

    /// <param name="key">Kafka key = camera_id</param>
    public async Task OnDetectionAsync(string key, byte[] value)
    {
        if (!_state.HasSubscribers(key)) { _throttle.Forget(key); Telemetry.Dropped.WithLabels("no_subscribers").Inc(); return; } // nobody watching: do no work at all
        var sendNow = _throttle.Offer(key, new Boxed { Value = Parse(value) }, _clock());
        if (sendNow is not null)
        {
            Telemetry.Sent.WithLabels("detections").Inc();
            await _hub.Clients.Group(LiveHub.CamGroup(key)).SendAsync("detections", sendNow.Value);
        }
    }

    public async Task FlushPendingAsync()
    {
        var list = new List<(string, Boxed)>();
        _throttle.Flush(_clock(), list);
        foreach (var (cam, b) in list)
        {
            if (!_state.HasSubscribers(cam)) { Telemetry.Dropped.WithLabels("no_subscribers").Inc(); continue; }
            Telemetry.Sent.WithLabels("detections").Inc();
            await _hub.Clients.Group(LiveHub.CamGroup(cam)).SendAsync("detections", b.Value);
        }
    }

    public Task OnRecognitionAsync(byte[] value)
    {
        Telemetry.Sent.WithLabels("recognition").Inc();
        return _hub.Clients.All.SendAsync("recognition", Parse(value));
    }

    public Task OnMetricsAsync(string planId, byte[] value)
    {
        Telemetry.Sent.WithLabels("pipelineMetrics").Inc();
        return _hub.Clients.Group(LiveHub.PlanGroup(planId)).SendAsync("pipelineMetrics", Parse(value));
    }

    public Task OnHealthAsync(string cameraId, byte[]? value)
    {
        if (value is null || value.Length == 0) { _state.Health.TryRemove(cameraId, out _); return Task.CompletedTask; }
        var el = Parse(value);
        _state.Health[cameraId] = el;
        if (!_state.IsReady) return Task.CompletedTask;
        Telemetry.Sent.WithLabels("cameraHealth").Inc();
        return _hub.Clients.All.SendAsync("cameraHealth", el); // during initial load only cache
    }

    public Task OnPlanAsync(string planId, byte[]? value)
    {
        if (value is null || value.Length == 0)
        {
            _state.Plans.TryRemove(planId, out _);
            // contract has no tombstone event: tell clients the plan is gone
            var gone = Parse(System.Text.Encoding.UTF8.GetBytes("{\"plan_id\":" + JsonSerializer.Serialize(planId) + ",\"deleted\":true}"));
            if (!_state.IsReady) return Task.CompletedTask;
            Telemetry.Sent.WithLabels("planChanged").Inc();
            return _hub.Clients.All.SendAsync("planChanged", gone);
        }
        var el = Parse(value);
        _state.Plans[planId] = el;
        if (!_state.IsReady) return Task.CompletedTask;
        Telemetry.Sent.WithLabels("planChanged").Inc();
        return _hub.Clients.All.SendAsync("planChanged", el);
    }
}
