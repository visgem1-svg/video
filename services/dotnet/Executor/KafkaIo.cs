using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Executor;

public sealed class KafkaSink : IResultSink, IDisposable
{
    private readonly IProducer<string, byte[]> _p;

    public KafkaSink(ExecutorOptions o)
    {
        _p = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = o.KafkaBootstrap,
            ClientId = o.ExecutorId,
            EnableIdempotence = true,            // acks=all, no duplicates on producer retry
            LingerMs = 5,                        // small batches: latency vs. request rate
            CompressionType = CompressionType.None,  // detections are tiny, jobs carry JPEG (incompressible)
            MessageMaxBytes = 2 * 1024 * 1024,
            QueueBufferingMaxKbytes = 16384,     // memory budget
            QueueBufferingMaxMessages = 50000,
            SocketNagleDisable = true,
        }).Build();
    }

    public async ValueTask PublishAsync(string topic, string key, byte[] value, CancellationToken ct,
        IReadOnlyDictionary<string, byte[]>? headers = null)
    {
        var msg = new Message<string, byte[]> { Key = key, Value = value };
        if (headers is not null)
        {
            msg.Headers = new Headers();
            foreach (var (k, v) in headers) msg.Headers.Add(k, v);
        }
        await _p.ProduceAsync(topic, msg, ct).ConfigureAwait(false);
    }

    public void Flush(TimeSpan t) => _p.Flush(t);
    public void Dispose() { _p.Flush(TimeSpan.FromSeconds(5)); _p.Dispose(); }
}

/// <summary>Copies the message value once into a pooled buffer (no per-message byte[] garbage for ~60 KB JPEGs).</summary>
internal sealed class PooledValueDeserializer : IDeserializer<PooledImage?>
{
    public PooledImage? Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context) =>
        isNull ? null : PooledImage.Copy(data);
}

/// <summary>
/// Generic ordered-consume loop on top of <see cref="WorkDispatcher{T}"/>. Offsets are stored (not committed) when a
/// contiguous prefix has completed; librdkafka's auto-commit then flushes stored offsets every second.
/// </summary>
public sealed class KafkaWorkService<T> : BackgroundService where T : class
{
    private readonly string _topic, _group;
    private readonly ExecutorOptions _o;
    private readonly PlanStore _plans;
    private readonly Func<PooledImage, Headers, T?> _parse;
    private readonly Func<T, long> _tsOf;
    private readonly Func<T, CancellationToken, Task> _handler;
    private readonly int _maxInflight;
    private readonly long _maxAgeMs;
    private readonly Action<string> _onSkip;
    private readonly Action<int> _onInflight;
    private readonly AutoOffsetReset _reset;
    private readonly ILogger _log;
    private readonly Func<T, string?>? _keyOf;
    private readonly Action<T>? _onDiscard;
    private readonly Func<string, bool>? _isWatched;
    private readonly Action<T, long>? _onDequeue;
    private readonly CancellationTokenSource _handlerCts = new();

    public KafkaWorkService(string topic, string group, ExecutorOptions o, PlanStore plans, Func<PooledImage, Headers, T?> parse,
        Func<T, long> tsOf, Func<T, CancellationToken, Task> handler, int maxInflight, long maxAgeMs,
        Action<string> onSkip, Action<int> onInflight, AutoOffsetReset reset, ILogger log, Func<T, string?>? keyOf = null,
        Action<T>? onDiscard = null, Func<string, bool>? isWatched = null, Action<T, long>? onDequeue = null)
    {
        _onDequeue = onDequeue;
        _onDiscard = onDiscard;
        _isWatched = isWatched;
        (_topic, _group, _o, _plans, _parse, _tsOf, _handler, _maxInflight, _maxAgeMs, _onSkip, _onInflight, _reset, _log, _keyOf) =
            (topic, group, o, plans, parse, tsOf, handler, maxInflight, maxAgeMs, onSkip, onInflight, reset, log, keyOf);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);

    private async Task RunAsync(CancellationToken ct)
    {
        try { await _plans.Ready.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }

        var tracker = new OffsetTracker();
        IConsumer<Ignore, PooledImage?>? consumer = null;
        var dispatcher = new WorkDispatcher<T>(_maxInflight, _maxAgeMs, _tsOf, _handler, tracker,
            commit: (p, next) =>
            {
                try { consumer?.StoreOffset(new TopicPartitionOffset(_topic, p, next)); }
                catch (KafkaException) { /* partition no longer assigned: the new owner re-reads from the last commit */ }
            },
            onSkip: _onSkip,
            onError: ex => _log.LogError(ex, "unhandled error in {Topic} handler", _topic),
            onInflight: _onInflight,
            handlerCt: _handlerCts.Token, keyOf: _keyOf, onDiscard: _onDiscard, isWatched: _isWatched, onDequeue: _onDequeue);

        consumer = new ConsumerBuilder<Ignore, PooledImage?>(new ConsumerConfig
        {
            BootstrapServers = _o.KafkaBootstrap,
            GroupId = _group,
            ClientId = _o.ExecutorId,
            EnableAutoCommit = true,
            AutoCommitIntervalMs = 1000,
            EnableAutoOffsetStore = false,        // we store offsets ourselves, only for the contiguous completed prefix
            AutoOffsetReset = _reset,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            SessionTimeoutMs = 20000,
            MaxPollIntervalMs = 300000,
            // ~25-60 KB JPEG messages. queued.max.messages.kbytes bounds the WHOLE consumer queue (high-level consumer),
            // not each partition: 512 KB (~20 frames) capped reads below the produce rate, so everything read was stale.
            // 8 MB ≈ 1-3 s of frames at 30-100 cameras; conflation drops what ages out. Native memory fits the 128 MB limit.
            QueuedMaxMessagesKbytes = 8 * 1024,
            QueuedMinMessages = 500,
            FetchMaxBytes = 8 * 1024 * 1024,
            FetchWaitMaxMs = 50,
            MaxPartitionFetchBytes = 1024 * 1024,  // a larger single message (<= 2 MB) is still returned by the broker
        })
            .SetValueDeserializer(new PooledValueDeserializer())
            .SetPartitionsRevokedHandler((_, parts) => { foreach (var p in parts) tracker.Remove(p.Partition.Value); })
            .SetPartitionsLostHandler((_, parts) => { foreach (var p in parts) tracker.Remove(p.Partition.Value); })
            .SetErrorHandler((_, e) => _log.LogWarning("kafka {Topic}: {Reason}", _topic, e.Reason))
            .Build();

        try
        {
            consumer.Subscribe(_topic);
            _log.LogInformation("consuming {Topic} as group {Group}", _topic, _group);
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<Ignore, PooledImage?>? cr;
                try { cr = consumer.Consume(TimeSpan.FromMilliseconds(100)); }
                catch (ConsumeException ex) { _log.LogWarning("consume error: {Reason}", ex.Error.Reason); continue; }
                if (cr?.Message is null) continue;
                var value = cr.Message.Value;
                if (value is null) { dispatcher.Skip(cr.Partition.Value, cr.Offset.Value, "bad_message"); continue; }

                T? item;
                try { item = _parse(value, cr.Message.Headers ?? new Headers()); }
                catch (JsonException) { item = null; }
                if (item is null)
                {
                    value.Dispose();
                    dispatcher.Skip(cr.Partition.Value, cr.Offset.Value, "bad_message");
                    continue;
                }

                await dispatcher.SubmitAsync(cr.Partition.Value, cr.Offset.Value, item, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _log.LogInformation("{Topic}: stopping, draining {N} in-flight", _topic, dispatcher.Inflight);
            if (!await dispatcher.DrainAsync(TimeSpan.FromMilliseconds(_o.DrainTimeoutMs)).ConfigureAwait(false))
                _handlerCts.Cancel(); // give up on stragglers: their offsets stay uncommitted -> redelivered
            await dispatcher.DrainAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            try { consumer.Commit(); } catch (KafkaException) { }
            consumer.Close();
            consumer.Dispose();
        }
    }
}

/// <summary>
/// Reads the compacted topics camera.config and pipeline.plans from the beginning into the PlanStore (no consumer
/// group: explicit assignment, nothing committed), marks the store ready once every partition reached the high
/// watermark seen at start, then keeps following live updates.
/// </summary>
public sealed class CompactedStateService : BackgroundService
{
    private readonly ExecutorOptions _o;
    private readonly PlanStore _store;
    private readonly DemandStore _demand;
    private readonly ILogger<CompactedStateService> _log;

    public CompactedStateService(ExecutorOptions o, PlanStore store, DemandStore demand, ILogger<CompactedStateService> log)
    {
        _o = o; _store = store; _demand = demand; _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(() => Run(ct), CancellationToken.None);

    private void Run(CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _o.KafkaBootstrap,
            GroupId = "executor-state-" + _o.ExecutorId,   // required by the client, never used for offsets
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            QueuedMaxMessagesKbytes = 2048,
        }).Build();

        try
        {
            var topics = new[] { Topics.CameraConfig, Topics.PipelinePlans, Topics.CameraDemand };
            List<TopicPartition> parts;
            using (var admin = new DependentAdminClientBuilder(consumer.Handle).Build())
                parts = WaitForPartitions(admin, topics, ct);

            consumer.Assign(parts.Select(p => new TopicPartitionOffset(p, Offset.Beginning)));
            var target = new Dictionary<TopicPartition, long>();
            foreach (var p in parts)
            {
                var wm = consumer.QueryWatermarkOffsets(p, TimeSpan.FromSeconds(10));
                if (wm.High.Value > wm.Low.Value) target[p] = wm.High.Value; // else empty partition: nothing to wait for
            }
            _log.LogInformation("loading compacted state from {N} non-empty partitions", target.Count);
            if (target.Count == 0) _store.MarkReady();

            while (!ct.IsCancellationRequested)
            {
                var cr = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (cr?.Message is null) continue;
                try
                {
                    if (cr.Topic == Topics.CameraConfig) _store.ApplyCameraJson(cr.Message.Key, cr.Message.Value);
                    else if (cr.Topic == Topics.CameraDemand) _demand.ApplyJson(cr.Message.Key, cr.Message.Value);
                    else _store.ApplyPlanJson(cr.Message.Key, cr.Message.Value);
                }
                catch (JsonException ex) { _log.LogWarning("bad {Topic} message {Key}: {Msg}", cr.Topic, cr.Message.Key, ex.Message); }

                if (!_store.IsReady && target.TryGetValue(cr.TopicPartition, out var hi) && cr.Offset.Value + 1 >= hi)
                {
                    target.Remove(cr.TopicPartition);
                    if (target.Count == 0)
                    {
                        _store.MarkReady();
                        _log.LogInformation("state loaded: {Cams} cameras, {Plans} plans", _store.CameraCount, _store.PlanCount);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogCritical(ex, "compacted state reader died"); throw; }
    }

    private List<TopicPartition> WaitForPartitions(IAdminClient admin, string[] topics, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var list = new List<TopicPartition>();
                foreach (var t in topics)
                {
                    var md = admin.GetMetadata(t, TimeSpan.FromSeconds(5)).Topics.FirstOrDefault(x => x.Topic == t);
                    if (md is null || md.Error.IsError || md.Partitions.Count == 0) throw new InvalidOperationException(t + " not available");
                    list.AddRange(md.Partitions.Select(p => new TopicPartition(t, p.PartitionId)));
                }
                return list;
            }
            catch (Exception ex) when (ex is KafkaException or InvalidOperationException)
            {
                _log.LogWarning("waiting for topics: {Msg}", ex.Message);
                ct.WaitHandle.WaitOne(2000);
            }
        }
    }
}

/// <summary>Emits pipeline.metrics every METRICS_WINDOW_S (5 s).</summary>
public sealed class MetricsFlushService : BackgroundService
{
    private readonly ExecutorOptions _o;
    private readonly StepStats _stats;
    private readonly IResultSink _sink;
    private readonly ILogger<MetricsFlushService> _log;

    public MetricsFlushService(ExecutorOptions o, StepStats stats, IResultSink sink, ILogger<MetricsFlushService> log)
    {
        _o = o; _stats = stats; _sink = sink; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_o.MetricsWindowS));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                foreach (var m in _stats.Flush(_o.ExecutorId, _o.MetricsWindowS, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                {
                    try
                    {
                        await _sink.PublishAsync(Topics.PipelineMetrics, m.PlanId,
                            JsonSerializer.SerializeToUtf8Bytes(m, Json.Default.PipelineMetric), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning("metrics publish failed: {Msg}", ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
