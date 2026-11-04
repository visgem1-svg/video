using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Registry.Assignment;

namespace Registry;

public sealed record DemandMessage(string? CameraId, int LiveViewers, int FrameViewers, long Ts);

/// <summary>Reads the compacted camera.demand topic from the earliest offset; keeps the latest entry per camera in memory (contract 9.8).</summary>
public sealed class DemandConsumer(IOptions<RegistryOptions> opts, ExtractModeEngine engine, ILogger<DemandConsumer> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        var o = opts.Value;
        var cfg = new ConsumerConfig
        {
            BootstrapServers = o.KafkaBootstrap,
            GroupId = "registry-demand-" + Environment.MachineName, // own group per replica: every replica needs all partitions
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            AllowAutoCreateTopics = false,
            ClientId = "registry-demand",
        };
        using var consumer = new ConsumerBuilder<string, string>(cfg)
            .SetErrorHandler((_, e) => log.LogWarning("demand consumer: {Reason}", e.Reason)).Build();
        consumer.Subscribe(o.DemandTopic);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var m = consumer.Consume(TimeSpan.FromSeconds(1));
                if (m is null || m.Message.Key is null) continue;
                if (m.Message.Value is null) { engine.RemoveDemand(m.Message.Key); continue; }
                var d = JsonSerializer.Deserialize<DemandMessage>(m.Message.Value, Json.Options);
                if (d is not null) engine.UpdateDemand(m.Message.Key, d.LiveViewers, d.FrameViewers, d.Ts);
            }
            catch (OperationCanceledException) { break; }
            catch (JsonException ex) { log.LogWarning("bad camera.demand message: {Error}", ex.Message); }
            catch (ConsumeException ex) { log.LogWarning("camera.demand consume: {Error}", ex.Error.Reason); Thread.Sleep(1000); }
        }
        try { consumer.Close(); } catch { /* shutting down */ }
    }
}
