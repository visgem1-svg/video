using Confluent.Kafka;
using Prometheus;

namespace FrameCatalog;

public sealed class ReadyState { public volatile bool Ready; }

/// <summary>One consumer thread for both topics, started at the latest offsets (live window). Offsets are stored after the message is applied.</summary>
public sealed class ConsumerService(CatalogOptions o, Catalog catalog, ReadyState ready, ILogger<ConsumerService> log) : BackgroundService
{
    private static readonly Counter Consumed = Metrics.CreateCounter("vp_catalog_consumed_total", "Messages consumed", "topic");
    private static readonly Counter Invalid = Metrics.CreateCounter("vp_catalog_invalid_messages_total", "Unparseable messages", "topic");

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        var lastSweep = Environment.TickCount64;
        try
        {
            using var c = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
            {
                BootstrapServers = o.KafkaBootstrap,
                GroupId = "frame-catalog",
                EnableAutoCommit = true,
                EnableAutoOffsetStore = false,
                AutoOffsetReset = AutoOffsetReset.Latest,
                // frames.raw carries JPEGs (~25-60 KB). This limit is for the whole consumer queue, not per partition:
                // 1 MB (~40 frames) starved partitions until retention deleted their frames. 16 MB ≈ 2 s at 100 cameras.
                QueuedMaxMessagesKbytes = 16 * 1024,
                QueuedMinMessages = 1000,
                FetchWaitMaxMs = 100,
                FetchMaxBytes = 8 * 1024 * 1024,
                MaxPartitionFetchBytes = 2 * 1024 * 1024 + 4096,
                PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            })
            .SetValueDeserializer(Deserializers.ByteArray)
            .SetPartitionsAssignedHandler((_, parts) =>
            {
                log.LogInformation("assigned {Count} partitions (seeking to end)", parts.Count);
                ready.Ready = true;
                return parts.Select(p => new TopicPartitionOffset(p, Offset.End)).ToList();
            })
            .Build();
            c.Subscribe(["frames.raw", "detections"]);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var r = c.Consume(TimeSpan.FromMilliseconds(500));
                    if (r is { IsPartitionEOF: false, Message.Value: not null })
                    {
                        Consumed.WithLabels(r.Topic).Inc();
                        if (r.Topic == "frames.raw")
                        {
                            var meta = r.Message.Headers is { } h && h.TryGetLastBytes("meta", out var m) ? m : null;
                            var f = meta is null ? null : Parser.ParseFrame(meta);
                            if (f is null) Invalid.WithLabels(r.Topic).Inc();
                            else catalog.AddFrame(f with { Partition = r.Partition.Value, Offset = r.Offset.Value }, r.Message.Value); // bytes kept only by the LRU
                        }
                        else
                        {
                            var d = Parser.ParseDetection(r.Message.Value);
                            if (d is null) Invalid.WithLabels(r.Topic).Inc(); else catalog.MergeDetections(d);
                        }
                        c.StoreOffset(r);
                    }
                }
                catch (ConsumeException ex) { log.LogWarning("consume error: {Error}", ex.Error.Reason); Thread.Sleep(500); }
                if (Environment.TickCount64 - lastSweep > 5000) { lastSweep = Environment.TickCount64; catalog.SweepPending(); catalog.UpdateGauges(); }
            }
            c.Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogCritical(ex, "consumer loop crashed"); }
    }
}
