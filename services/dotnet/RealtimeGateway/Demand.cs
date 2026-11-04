using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;

namespace RealtimeGateway;

/// <summary>
/// Per-camera viewer counts (contract 9.8): live = SubscribeCameras, frames = WatchFrames. Changes are debounced 1 s per
/// camera (<see cref="CollectDue"/>); <see cref="CollectNonZero"/> feeds the 30 s re-publish. Kafka-agnostic.
/// </summary>
public sealed class DemandTracker
{
    public const long DebounceMs = 1000;

    private sealed class Entry { public int Live, Frames; public long DirtySince = -1; }
    private readonly object _l = new();
    private readonly Dictionary<string, Entry> _e = new();
    private int _watchedLive, _watchedFrames;

    public int WatchedLive { get { lock (_l) return _watchedLive; } }
    public int WatchedFrames { get { lock (_l) return _watchedFrames; } }

    public int Live(string camera) { lock (_l) return _e.TryGetValue(camera, out var e) ? e.Live : 0; }
    public int Frames(string camera) { lock (_l) return _e.TryGetValue(camera, out var e) ? e.Frames : 0; }

    public void Add(string camera, bool frames, long nowMs) => Change(camera, frames, +1, nowMs);
    public void Remove(string camera, bool frames, long nowMs) => Change(camera, frames, -1, nowMs);

    private void Change(string camera, bool frames, int delta, long nowMs)
    {
        lock (_l)
        {
            if (!_e.TryGetValue(camera, out var e))
            {
                if (delta < 0) return;
                _e[camera] = e = new Entry();
            }
            var before = frames ? e.Frames : e.Live;
            var after = Math.Max(0, before + delta);
            if (before == after) return;
            if (frames) e.Frames = after; else e.Live = after;
            if (before == 0 || after == 0)
            {
                var d = after == 0 ? -1 : 1;
                if (frames) _watchedFrames += d; else _watchedLive += d;
            }
            if (e.DirtySince < 0) e.DirtySince = nowMs;
        }
    }

    /// <summary>Cameras whose count changed at least <see cref="DebounceMs"/> ago (a drop to 0 included).</summary>
    public void CollectDue(long nowMs, List<(string Camera, int Live, int Frames)> into)
    {
        lock (_l)
        {
            List<string>? gone = null;
            foreach (var (cam, e) in _e)
            {
                if (e.DirtySince < 0 || nowMs - e.DirtySince < DebounceMs) continue;
                e.DirtySince = -1;
                into.Add((cam, e.Live, e.Frames));
                if (e.Live == 0 && e.Frames == 0) (gone ??= new()).Add(cam);
            }
            if (gone is not null) foreach (var c in gone) _e.Remove(c);
        }
    }

    public void CollectNonZero(List<(string Camera, int Live, int Frames)> into)
    {
        lock (_l) foreach (var (cam, e) in _e) if (e.Live > 0 || e.Frames > 0) into.Add((cam, e.Live, e.Frames));
    }
}

/// <summary>
/// Publishes camera.demand (contract 9.8): changed cameras after the 1 s debounce, every non-zero camera each 30 s.
/// One idempotent producer (acks=all), fire-and-forget with a delivery callback: a lost message is healed by the refresh.
/// </summary>
public sealed class DemandPublisher : BackgroundService
{
    public const long RepublishMs = 30_000;

    private readonly GatewayOptions _o;
    private readonly LiveState _state;
    private readonly ILogger<DemandPublisher> _log;

    public DemandPublisher(GatewayOptions o, LiveState state, ILogger<DemandPublisher> log) { _o = o; _state = state; _log = log; }

    public static byte[] Payload(string camera, int live, int frames, long tsMs) =>
        System.Text.Encoding.UTF8.GetBytes(
            $"{{\"camera_id\":{JsonSerializer.Serialize(camera)},\"live_viewers\":{live},\"frame_viewers\":{frames},\"ts\":{tsMs}}}");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = _o.Bootstrap,
            ClientId = _o.GroupId + "-demand",
            EnableIdempotence = true,   // implies acks=all
            LingerMs = 20,
            QueueBufferingMaxKbytes = 1024,
        }).SetErrorHandler((_, e) => _log.LogWarning("kafka demand: {Reason}", e.Reason)).Build();

        var list = new List<(string Camera, int Live, int Frames)>();
        var nextRepublish = Environment.TickCount64 + RepublishMs;
        using var t = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await t.WaitForNextTickAsync(ct))
            {
                var now = Environment.TickCount64;
                list.Clear();
                _state.Demand.CollectDue(now, list);
                if (now >= nextRepublish) { nextRepublish = now + RepublishMs; _state.Demand.CollectNonZero(list); }
                Telemetry.WatchedCameras.WithLabels("live").Set(_state.Demand.WatchedLive);
                Telemetry.WatchedCameras.WithLabels("frames").Set(_state.Demand.WatchedFrames);
                if (list.Count == 0) continue;
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var (cam, live, frames) in list)   // a camera may appear twice (change + refresh): same values, harmless
                {
                    try
                    {
                        producer.Produce(Topics.CameraDemand, new Message<string, byte[]> { Key = cam, Value = Payload(cam, live, frames, ts) },
                            r => { if (r.Error.IsError) _log.LogWarning("demand publish failed for {Cam}: {Reason}", cam, r.Error.Reason); });
                        Telemetry.DemandPublished.Inc();
                    }
                    catch (ProduceException<string, byte[]> ex) { _log.LogWarning("demand produce: {Reason}", ex.Error.Reason); }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { producer.Flush(TimeSpan.FromSeconds(3)); }
    }
}
