namespace Executor;

/// <summary>
/// Tracks completion of in-flight messages per partition so that only the highest CONTIGUOUS completed offset is
/// committed. Messages of a partition are registered in consume order; they may complete in any order (frames run in
/// parallel). Committing offset N+1 means "everything up to N is done", so an out-of-order completion must not advance
/// the commit past a still-running earlier message (a crash would lose it). Gaps in the offset sequence (compaction,
/// transaction markers) are fine: only registered offsets matter.
/// </summary>
public sealed class OffsetTracker
{
    private sealed class Part
    {
        public readonly Queue<long> Order = new();
        public readonly Dictionary<long, bool> Done = new();
    }

    private readonly Dictionary<int, Part> _parts = new();
    private readonly object _lock = new();

    public void Register(int partition, long offset)
    {
        lock (_lock)
        {
            if (!_parts.TryGetValue(partition, out var p)) _parts[partition] = p = new Part();
            if (p.Done.TryAdd(offset, false)) p.Order.Enqueue(offset);
        }
    }

    /// <summary>Marks the message done. Returns the new offset to commit (last contiguous done + 1) or null.</summary>
    public long? Complete(int partition, long offset)
    {
        lock (_lock)
        {
            if (!_parts.TryGetValue(partition, out var p) || !p.Done.ContainsKey(offset)) return null;
            p.Done[offset] = true;
            long? commit = null;
            while (p.Order.Count > 0 && p.Done[p.Order.Peek()])
            {
                var o = p.Order.Dequeue();
                p.Done.Remove(o);
                commit = o + 1;
            }
            return commit;
        }
    }

    /// <summary>Partition revoked/lost: forget its state (the new owner re-reads from the last commit).</summary>
    public void Remove(int partition)
    {
        lock (_lock) _parts.Remove(partition);
    }

    public int Pending(int partition)
    {
        lock (_lock) return _parts.TryGetValue(partition, out var p) ? p.Order.Count : 0;
    }
}
