using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Prometheus;
using Microsoft.Extensions.Hosting;

namespace RealtimeGateway;

public static class Topics
{
    public const string Detections = "detections";
    public const string Recognitions = "recognitions";
    public const string CameraHealth = "camera.health";
    public const string PipelinePlans = "pipeline.plans";
    public const string PipelineMetrics = "pipeline.metrics";
    public const string CameraDemand = "camera.demand";
}

public sealed class GatewayOptions
{
    public string Bootstrap { get; init; } = "redpanda:9092";
    public string GroupId { get; init; } = "";
    public int MaxPerSecond { get; init; } = 3;

    public static GatewayOptions FromEnvironment(Func<string, string?> get)
    {
        string S(string k, string d) => string.IsNullOrWhiteSpace(get(k)) ? d : get(k)!;
        // every instance needs ALL events -> unique consumer group per instance (contract 5)
        var id = S("REALTIME_INSTANCE_ID", Environment.MachineName.ToLowerInvariant()) + "-" + Guid.NewGuid().ToString("N")[..8];
        return new GatewayOptions
        {
            Bootstrap = S("KAFKA_BOOTSTRAP", "redpanda:9092"),
            GroupId = "realtime-gateway-" + id,
            MaxPerSecond = int.TryParse(get("DETECTIONS_MAX_PER_SECOND"), out var n) && n > 0 ? n : 3,
        };
    }
}

/// <summary>
/// Reads camera.health + pipeline.plans completely at start (explicit assign from the beginning, so a client connecting
/// later gets a full snapshot) then keeps following them.
/// </summary>
public sealed class CompactedFeed : BackgroundService
{
    private readonly GatewayOptions _o;
    private readonly EventRouter _router;
    private readonly LiveState _state;
    private readonly ILogger<CompactedFeed> _log;

    public CompactedFeed(GatewayOptions o, EventRouter router, LiveState state, ILogger<CompactedFeed> log)
    {
        _o = o; _router = router; _state = state; _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(() => Run(ct), CancellationToken.None);

    private async Task Run(CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _o.Bootstrap,
            GroupId = _o.GroupId + "-state",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            QueuedMaxMessagesKbytes = 2048,
        }).Build();
        try
        {
            var topics = new[] { Topics.CameraHealth, Topics.PipelinePlans };
            var parts = new List<TopicPartition>();
            using (var admin = new DependentAdminClientBuilder(consumer.Handle).Build())
                while (parts.Count == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var t in topics)
                        {
                            var md = admin.GetMetadata(t, TimeSpan.FromSeconds(5)).Topics.FirstOrDefault(x => x.Topic == t);
                            if (md is null || md.Error.IsError || md.Partitions.Count == 0) throw new InvalidOperationException(t + " not available");
                            parts.AddRange(md.Partitions.Select(p => new TopicPartition(t, p.PartitionId)));
                        }
                    }
                    catch (Exception ex) when (ex is KafkaException or InvalidOperationException)
                    {
                        parts.Clear();
                        _log.LogWarning("waiting for topics: {Msg}", ex.Message);
                        await Task.Delay(2000, ct);
                    }
                }

            consumer.Assign(parts.Select(p => new TopicPartitionOffset(p, Offset.Beginning)));
            var target = new Dictionary<TopicPartition, long>();
            foreach (var p in parts)
            {
                var wm = consumer.QueryWatermarkOffsets(p, TimeSpan.FromSeconds(10));
                if (wm.High.Value > wm.Low.Value) target[p] = wm.High.Value;
            }
            if (target.Count == 0) _state.MarkReady();

            while (!ct.IsCancellationRequested)
            {
                var cr = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (cr?.Message is null) continue;
                Telemetry.In.WithLabels(cr.Topic).Inc();
                try
                {
                    if (cr.Topic == Topics.CameraHealth) await _router.OnHealthAsync(cr.Message.Key, cr.Message.Value);
                    else await _router.OnPlanAsync(cr.Message.Key, cr.Message.Value);
                }
                catch (System.Text.Json.JsonException ex) { Telemetry.Dropped.WithLabels("parse_error").Inc(); _log.LogWarning("bad {Topic} message {Key}: {Msg}", cr.Topic, cr.Message.Key, ex.Message); }

                if (!_state.IsReady && target.TryGetValue(cr.TopicPartition, out var hi) && cr.Offset.Value + 1 >= hi)
                {
                    target.Remove(cr.TopicPartition);
                    if (target.Count == 0)
                    {
                        _state.MarkReady();
                        _log.LogInformation("snapshot loaded: {H} cameras, {P} plans", _state.Health.Count, _state.Plans.Count);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>detections / recognitions / pipeline.metrics: start from the latest offsets, unique group per instance.</summary>
public sealed class LiveFeed : BackgroundService
{
    private readonly GatewayOptions _o;
    private readonly EventRouter _router;
    private readonly ILogger<LiveFeed> _log;

    public LiveFeed(GatewayOptions o, EventRouter router, ILogger<LiveFeed> log)
    {
        _o = o; _router = router; _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(() => Run(ct), CancellationToken.None);

    private async Task Run(CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _o.Bootstrap,
            GroupId = _o.GroupId,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Latest,
            AllowAutoCreateTopics = false,
            TopicMetadataRefreshIntervalMs = 10000,
            QueuedMaxMessagesKbytes = 4096,
            FetchMaxBytes = 4 * 1024 * 1024,
            FetchWaitMaxMs = 50,
        })
            .SetErrorHandler((_, e) => _log.LogWarning("kafka: {Reason}", e.Reason))
            .Build();
        try
        {
            consumer.Subscribe(new[] { Topics.Detections, Topics.Recognitions, Topics.PipelineMetrics });
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? cr;
                try { cr = consumer.Consume(TimeSpan.FromMilliseconds(100)); }
                catch (ConsumeException ex) { _log.LogWarning("consume: {Reason}", ex.Error.Reason); continue; }
                if (cr?.Message?.Value is null) continue;
                Telemetry.In.WithLabels(cr.Topic).Inc();
                Telemetry.LagMs.WithLabels(cr.Topic).Set(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cr.Message.Timestamp.UnixTimestampMs));
                try
                {
                    switch (cr.Topic)
                    {
                        case Topics.Detections: await _router.OnDetectionAsync(cr.Message.Key ?? "", cr.Message.Value); break;
                        case Topics.Recognitions: await _router.OnRecognitionAsync(cr.Message.Value); break;
                        case Topics.PipelineMetrics: await _router.OnMetricsAsync(cr.Message.Key ?? "", cr.Message.Value); break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Telemetry.Dropped.WithLabels("parse_error").Inc();
                    _log.LogWarning("dropping bad {Topic} message: {Msg}", cr.Topic, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { consumer.Close(); }
    }
}

/// <summary>Releases throttled (pending) detections every 50 ms.</summary>
public sealed class ThrottleFlusher : BackgroundService
{
    private readonly EventRouter _router;
    public ThrottleFlusher(EventRouter router) => _router = router;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var t = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try { while (await t.WaitForNextTickAsync(ct)) await _router.FlushPendingAsync(); }
        catch (OperationCanceledException) { }
    }
}
