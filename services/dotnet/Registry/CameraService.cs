using System.Text.Json;
using System.Text.RegularExpressions;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Registry.Data;

namespace Registry;

public sealed class RegistryOptions
{
    public string KafkaBootstrap { get; set; } = "redpanda:9092";
    public string CameraConfigTopic { get; set; } = "camera.config";
    public int LeaseSeconds { get; set; } = 15;
    public int SeedCameras { get; set; }
    public string SeedRtspBase { get; set; } = "rtsp://mediamtx:8554";
    public string MediaMtxApiUrl { get; set; } = "http://mediamtx:9997";
    public string DemandTopic { get; set; } = "camera.demand";
}

public interface ICameraPublisher
{
    /// <summary>Publish (config) or tombstone (null) and wait for broker ack.</summary>
    Task PublishAsync(string cameraId, CameraConfig? config, CancellationToken ct);
    Task PublishManyAsync(IReadOnlyList<CameraConfig> configs, CancellationToken ct);
}

public sealed class KafkaCameraPublisher : ICameraPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;

    public KafkaCameraPublisher(IOptions<RegistryOptions> o)
    {
        _topic = o.Value.CameraConfigTopic;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = o.Value.KafkaBootstrap,
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = 5,
            ClientId = "registry",
        }).Build();
    }

    public async Task PublishAsync(string cameraId, CameraConfig? config, CancellationToken ct)
    {
        var msg = new Message<string, string>
        {
            Key = cameraId,
            Value = config is null ? null! : JsonSerializer.Serialize(config, Json.Options), // null value = tombstone
        };
        await _producer.ProduceAsync(_topic, msg, ct);
    }

    public Task PublishManyAsync(IReadOnlyList<CameraConfig> configs, CancellationToken ct)
    {
        foreach (var c in configs)
            _producer.Produce(_topic, new Message<string, string> { Key = c.CameraId, Value = JsonSerializer.Serialize(c, Json.Options) });
        _producer.Flush(ct);
        return Task.CompletedTask;
    }

    public void Dispose() { _producer.Flush(TimeSpan.FromSeconds(5)); _producer.Dispose(); }
}

