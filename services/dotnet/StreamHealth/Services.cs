using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Prometheus;

namespace StreamHealth;

public sealed class HealthOptions
{
    public string KafkaBootstrap { get; set; } = "redpanda:9092";
    public string ConfigTopic { get; set; } = "camera.config";
    public string StatusTopic { get; set; } = "camera.status";
    public string HealthTopic { get; set; } = "camera.health";
    public string? RegistryUrl { get; set; }
}

public sealed class ReadyState { public volatile bool Ready; }

public interface IHealthPublisher
{
    void Publish(IReadOnlyList<CameraHealth> health, IReadOnlyList<string> tombstones);
}

public sealed class KafkaHealthPublisher : IHealthPublisher, IDisposable
{
    private static readonly Counter Published = Metrics.CreateCounter("vp_streamhealth_published_total", "camera.health messages published");
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly ILogger<KafkaHealthPublisher> _log;

    public KafkaHealthPublisher(HealthOptions o, ILogger<KafkaHealthPublisher> log)
    {
        _log = log;
        _topic = o.HealthTopic;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = o.KafkaBootstrap,
            ClientId = "stream-health",
            LingerMs = 20,
            Acks = Acks.Leader,
            QueueBufferingMaxKbytes = 8192,
        }).Build();
    }

    public void Publish(IReadOnlyList<CameraHealth> health, IReadOnlyList<string> tombstones)
    {
        foreach (var h in health) Send(h.CameraId, JsonSerializer.Serialize(h, Json.Options));
        foreach (var t in tombstones) Send(t, null);
        _producer.Poll(TimeSpan.Zero);
        Published.Inc(health.Count + tombstones.Count);
    }

    private void Send(string key, string? value)
    {
        try { _producer.Produce(_topic, new Message<string, string> { Key = key, Value = value! }); }
        catch (ProduceException<string, string> ex) { _log.LogWarning("produce failed: {Error}", ex.Error.Reason); }
    }

    public void Dispose() { _producer.Flush(TimeSpan.FromSeconds(5)); _producer.Dispose(); }
}

/// <summary>Reads camera.config from the beginning on every start (compacted topic = full state), no offset commits.</summary>
public sealed class ConfigConsumer(HealthEngine engine, HealthOptions o, ReadyState ready, ILogger<ConfigConsumer> log) : BackgroundService
{
    private static readonly Counter Msgs = Metrics.CreateCounter("vp_streamhealth_config_messages_total", "camera.config messages consumed");

    protected override Task ExecuteAsync(CancellationToken ct) => Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = o.KafkaBootstrap,
            GroupId = "stream-health-config", // unused for offsets (manual assign), required by the client
            EnableAutoCommit = false,
            EnablePartitionEof = true,
            QueuedMaxMessagesKbytes = 4096,
            MaxPartitionFetchBytes = 1048576,
        }).Build();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var admin = new DependentAdminClientBuilder(consumer.Handle).Build();
                var md = admin.GetMetadata(o.ConfigTopic, TimeSpan.FromSeconds(5));
                var parts = md.Topics.FirstOrDefault()?.Partitions;
                if (parts is null || parts.Count == 0) throw new InvalidOperationException("topic has no partitions yet");
                consumer.Assign(parts.Select(p => new TopicPartitionOffset(new TopicPartition(o.ConfigTopic, p.PartitionId), Offset.Beginning)));
                var pending = parts.Select(p => p.PartitionId).ToHashSet();
                while (!ct.IsCancellationRequested)
                {
                    var r = consumer.Consume(ct);
                    if (r.IsPartitionEOF)
                    {
                        pending.Remove(r.Partition.Value);
                        if (pending.Count == 0 && !ready.Ready) { ready.Ready = true; log.LogInformation("camera.config caught up"); }
                        continue;
                    }
                    Msgs.Inc();
                    Apply(r.Message.Key, r.Message.Value);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogWarning("config consumer error: {Error}", ex.Message);
                try { Task.Delay(2000, ct).Wait(ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    internal void Apply(string key, string? value)
    {
        if (value is null) { engine.OnConfig(key, null); return; }
        try
        {
            var cfg = JsonSerializer.Deserialize<CameraConfig>(value, Json.Options);
            if (cfg is not null) engine.OnConfig(string.IsNullOrEmpty(cfg.CameraId) ? key : cfg.CameraId, cfg);
        }
        catch (JsonException ex) { log.LogWarning("bad camera.config for {Key}: {Error}", key, ex.Message); }
    }
}

/// <summary>Consumes camera.status. State is ephemeral/idempotent (latest wins) so auto-commit is fine.</summary>
public sealed class StatusConsumer(HealthEngine engine, HealthOptions o, ILogger<StatusConsumer> log) : BackgroundService
{
    private static readonly Counter Msgs = Metrics.CreateCounter("vp_streamhealth_status_messages_total", "camera.status messages consumed");
    private static readonly Counter Bad = Metrics.CreateCounter("vp_streamhealth_status_invalid_total", "unparseable camera.status messages");

    protected override Task ExecuteAsync(CancellationToken ct) => Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = o.KafkaBootstrap,
            GroupId = "stream-health",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = true,
            AutoCommitIntervalMs = 5000,
            QueuedMaxMessagesKbytes = 4096,
            MaxPartitionFetchBytes = 1048576,
            AllowAutoCreateTopics = false,
        }).Build();
        consumer.Subscribe(o.StatusTopic);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = consumer.Consume(ct);
                if (r?.Message?.Value is null) continue;
                Msgs.Inc();
                try
                {
                    var st = JsonSerializer.Deserialize<CameraStatus>(r.Message.Value, Json.Options);
                    if (st is null || string.IsNullOrEmpty(st.CameraId)) { Bad.Inc(); continue; }
                    engine.OnStatus(st);
                }
                catch (JsonException) { Bad.Inc(); }
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { log.LogWarning("status consume error: {Error}", ex.Error.Reason); Thread.Sleep(1000); }
        }
        try { consumer.Close(); } catch { /* shutting down */ }
    }
}

/// <summary>1 Hz tick: sample uptime, detect changes, publish.</summary>
public sealed class HealthTicker(HealthEngine engine, IHealthPublisher publisher, ILogger<HealthTicker> log) : BackgroundService
{
    private static readonly Gauge Cams = Metrics.CreateGauge("vp_streamhealth_cameras", "Cameras by state", "state");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var res = engine.Tick();
                if (res.Publish.Count > 0 || res.Tombstones.Count > 0) publisher.Publish(res.Publish, res.Tombstones);
                var s = engine.Summary();
                Cams.WithLabels("online").Set(s.Online); Cams.WithLabels("degraded").Set(s.Degraded);
                Cams.WithLabels("offline").Set(s.Offline); Cams.WithLabels("unassigned").Set(s.Unassigned);
                Cams.WithLabels("idle").Set(s.Idle);
            }
            catch (Exception ex) { log.LogError(ex, "tick failed"); }
        }
    }
}

/// <summary>Polls GET {REGISTRY_URL}/api/workers every 5 s to learn which cameras have an owner.</summary>
public sealed class RegistryPoller(HealthEngine engine, HealthOptions o, ILogger<RegistryPoller> log) : BackgroundService
{
    private sealed record Worker(string WorkerId, string[] Assigned);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.RegistryUrl)) return; // unknown ownership => released cameras show offline
        using var http = new HttpClient { BaseAddress = new Uri(o.RegistryUrl), Timeout = TimeSpan.FromSeconds(3) };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                var workers = await http.GetFromJsonAsync<Worker[]>("/api/workers", Json.Options, ct) ?? [];
                engine.SetOwned(workers.SelectMany(w => w.Assigned ?? []));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                engine.SetOwned(null); // registry unreachable: do not claim "unassigned"
                log.LogWarning("registry poll failed: {Error}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }
}
