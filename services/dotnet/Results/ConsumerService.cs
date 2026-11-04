using System.Threading.Channels;
using Confluent.Kafka;
using Prometheus;

namespace Results;

public sealed class ReadyState { public volatile bool Ready; }

/// <summary>
/// Consumer thread -> bounded channel (back-pressure) -> writer loop (batch flush + Kafka commit after the DB commit).
/// </summary>
public sealed class ConsumerService(ResultsOptions o, ResultsBatcher batcher, ReadyState ready, ILogger<ConsumerService> log) : BackgroundService
{
    private static readonly Counter Consumed = Metrics.CreateCounter("vp_results_consumed_total", "Messages consumed", "topic");
    private static readonly Gauge BatchSize = Metrics.CreateGauge("vp_results_last_batch_size", "Size of the last flushed batch");

    private readonly Channel<List<InMessage>> _channel = Channel.CreateBounded<List<InMessage>>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private IConsumer<string, string>? _consumer;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var reader = Task.Factory.StartNew(() => ReadLoop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var writer = WriteLoop(ct);
        await Task.WhenAll(reader, writer);
    }

    private void ReadLoop(CancellationToken ct)
    {
        try
        {
            using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = o.KafkaBootstrap,
                GroupId = "results",
                EnableAutoCommit = false,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                QueuedMaxMessagesKbytes = 8192,
                MaxPartitionFetchBytes = 1048576,
                MaxPollIntervalMs = 300000,
                PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            })
            .SetPartitionsAssignedHandler((c, parts) =>
            {
                // Seek to the offsets persisted in Postgres (authoritative for aggregates).
                // Cooperative rebalance: the handler only receives the *incremental* partitions.
                var stored = batcher.LoadOffsetsAsync(CancellationToken.None).GetAwaiter().GetResult();
                var result = parts.Select(p => stored.TryGetValue(new TP(p.Topic, p.Partition.Value), out var n)
                    ? new TopicPartitionOffset(p, n) : new TopicPartitionOffset(p, Offset.Unset)).ToList();
                log.LogInformation("assigned {Count} partitions", parts.Count);
                ready.Ready = true;
                return result;
            })
            .SetPartitionsLostHandler((_, parts) => log.LogWarning("lost {Count} partitions", parts.Count))
            .Build();
            _consumer = consumer;
            consumer.Subscribe([o.DetectionsTopic, o.RecognitionsTopic]);

            var batch = new List<InMessage>(o.BatchMax);
            var deadline = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var wait = batch.Count == 0 ? TimeSpan.FromMilliseconds(o.BatchFlushMs) : deadline - DateTime.UtcNow;
                    ConsumeResult<string, string>? r = wait > TimeSpan.Zero ? consumer.Consume(wait) : null;
                    if (r is { IsPartitionEOF: false })
                    {
                        if (batch.Count == 0) deadline = DateTime.UtcNow.AddMilliseconds(o.BatchFlushMs);
                        batch.Add(new InMessage(r.Topic, r.Partition.Value, r.Offset.Value, r.Message.Value));
                        Consumed.WithLabels(r.Topic).Inc();
                    }
                    if (batch.Count >= o.BatchMax || (batch.Count > 0 && DateTime.UtcNow >= deadline))
                    {
                        _channel.Writer.WriteAsync(batch, ct).AsTask().GetAwaiter().GetResult(); // blocks => back-pressure
                        batch = new List<InMessage>(o.BatchMax);
                    }
                }
                catch (ConsumeException ex) { log.LogWarning("consume error: {Error}", ex.Error.Reason); Thread.Sleep(500); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogCritical(ex, "consumer loop crashed"); }
        finally { _channel.Writer.TryComplete(); }
    }

    private async Task WriteLoop(CancellationToken ct)
    {
        // Drain what is already queued on shutdown, so consumed messages are not lost to redelivery needlessly.
        await foreach (var batch in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var commit = await batcher.ProcessAsync(batch, ct.IsCancellationRequested ? new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token : CancellationToken.None);
                    BatchSize.Set(batch.Count);
                    try
                    {
                        _consumer?.Commit(commit.Select(kv => new TopicPartitionOffset(kv.Key.Topic, new Partition(kv.Key.Partition), kv.Value)));
                    }
                    catch (Exception ex) { log.LogWarning("kafka commit failed (DB offsets remain authoritative): {Error}", ex.Message); }
                    break;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    log.LogError("batch flush failed (attempt {Attempt}): {Error}", attempt, ex.Message);
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 10)), ct); } catch (OperationCanceledException) { return; }
                }
            }
        }
    }
}
