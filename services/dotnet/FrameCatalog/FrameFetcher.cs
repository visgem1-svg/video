using Confluent.Kafka;

namespace FrameCatalog;

public sealed class FetchBusyException : Exception { }

public interface IFrameFetcher
{
    /// <summary>Fetches the JPEG at (partition, offset) of frames.raw. Null = below log start / not found.</summary>
    Task<byte[]?> FetchAsync(int partition, long offset, CancellationToken ct);
}

/// <summary>
/// One dedicated consumer (no group commits): assign + seek + poll one message. Fetches are serialized;
/// callers waiting longer than 3 s get <see cref="FetchBusyException"/> (HTTP 503) instead of piling up.
/// </summary>
public sealed class KafkaFrameFetcher(CatalogOptions o) : IFrameFetcher, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConsumer<Ignore, byte[]>? _consumer;

    private IConsumer<Ignore, byte[]> Consumer => _consumer ??= new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
    {
        BootstrapServers = o.KafkaBootstrap,
        GroupId = "frame-catalog-fetch",
        EnableAutoCommit = false,
        EnableAutoOffsetStore = false,
        AutoOffsetReset = AutoOffsetReset.Error,   // offset below log start => error instead of silently jumping
        FetchMaxBytes = 4 * 1024 * 1024,
        MaxPartitionFetchBytes = 2 * 1024 * 1024 + 4096,
        QueuedMaxMessagesKbytes = 2048,
        QueuedMinMessages = 1,
    }).SetValueDeserializer(Deserializers.ByteArray).Build();

    public async Task<byte[]?> FetchAsync(int partition, long offset, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(3000, ct)) throw new FetchBusyException();
        try { return await Task.Run(() => FetchOne(partition, offset), ct); }
        finally { _gate.Release(); }
    }

    private byte[]? FetchOne(int partition, long offset)
    {
        var c = Consumer;
        try
        {
            c.Assign(new TopicPartitionOffset("frames.raw", partition, offset));
            var deadline = DateTime.UtcNow.AddMilliseconds(o.FetchTimeoutMs);
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return null;
                var r = c.Consume(left);
                if (r is null || r.IsPartitionEOF) return null;
                if (r.Partition.Value != partition) continue;
                return r.Offset.Value == offset ? r.Message.Value : null; // a gap means the frame is gone
            }
        }
        catch (ConsumeException ex) when (ex.Error.Code is ErrorCode.OffsetOutOfRange or ErrorCode.Local_AutoOffsetReset or ErrorCode.Local_NoOffset)
        {
            return null;
        }
        finally
        {
            try { c.Unassign(); } catch (KafkaException) { /* ignore */ }
        }
    }

    public void Dispose() { _consumer?.Dispose(); _gate.Dispose(); }
}
