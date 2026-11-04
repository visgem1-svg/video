using System.Text.Json;
using Prometheus;

namespace Results;

public interface IResultsStore
{
    Task<IReadOnlyDictionary<TP, long>> LoadOffsetsAsync(CancellationToken ct);

    /// <summary>
    /// ONE transaction: insert recognitions (ON CONFLICT DO NOTHING), add aggregate deltas, advance offsets.
    /// Throws <see cref="OffsetFencedException"/> when a partition's stored offset is already past the batch start.
    /// </summary>
    Task CommitBatchAsync(BatchWrite write, CancellationToken ct);

    Task<IReadOnlyList<RecognitionDto>> QueryRecognitionsAsync(string? cameraId, string? type, long? since, int limit, CancellationToken ct, string? personId = null);
    Task<IReadOnlyList<DetectionStat>> QueryStatsAsync(string? cameraId, long fromMinuteTs, CancellationToken ct);

    /// <summary>Deletes up to <paramref name="batch"/> recognitions with ts     Task<IReadOnlyList<DetectionStat>> QueryStatsAsync(string? cameraId, long fromMinuteTs, CancellationToken ct);lt; cutoff; returns the number deleted.</summary>
    Task<int> DeleteRecognitionsOlderThanAsync(long cutoffTsMs, int batch, CancellationToken ct);
}

public interface ISnapshotStore
{
    /// <summary>Stream an object; false when not found.</summary>
    Task<bool> ReadAsync(string snapshotKey, Stream destination, CancellationToken ct);
}

/// <summary>
/// Batch processing core (no Kafka / Postgres / MinIO dependencies).
///
/// Idempotency:
///  * recognitions: PRIMARY KEY recognition_id + ON CONFLICT DO NOTHING (also deduped inside a batch).
///  * aggregates: counts are additive, so redelivery would double count. We make the *offset* the
///    idempotency token: Kafka offsets of the source partitions are stored in Postgres in the SAME
///    transaction as the aggregate deltas. On (re)assignment the consumer seeks to the DB offsets, and
///    every batch is filtered against them (offset &lt; stored next offset => already counted, skipped).
///    The DB additionally fences concurrent/zombie writers (stored offset past batch start => rollback).
///    Result: exactly-once aggregates without a per-frame row. Kafka commits only mirror this for lag metrics.
/// </summary>
public sealed class ResultsBatcher(IResultsStore store, ILogger<ResultsBatcher> log, string detectionsTopic = "detections")
{
    private static readonly Counter Skipped = Metrics.CreateCounter("vp_results_redelivered_skipped_total", "Messages skipped by the offset fence");
    private static readonly Counter Invalid = Metrics.CreateCounter("vp_results_invalid_messages_total", "Unparseable messages dropped", "topic");
    private static readonly Counter Dets = Metrics.CreateCounter("vp_results_detections_total", "Detection messages aggregated");
    private static readonly Counter RecsC = Metrics.CreateCounter("vp_results_recognitions_total", "Recognitions processed");
    private static readonly Histogram FlushSec = Metrics.CreateHistogram("vp_results_flush_duration_seconds", "Batch flush duration");

    private Dictionary<TP, long> _next = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Reload the persisted offsets (call on every partition assignment). Returns them for seeking.</summary>
    public async Task<IReadOnlyDictionary<TP, long>> LoadOffsetsAsync(CancellationToken ct)
    {
        var o = await store.LoadOffsetsAsync(ct);
        await _gate.WaitAsync(ct);
        try { _next = new Dictionary<TP, long>(o); }
        finally { _gate.Release(); }
        return o;
    }

    /// <returns>Next offset per partition for the Kafka commit (covers skipped and invalid messages too).</returns>
    public async Task<IReadOnlyDictionary<TP, long>> ProcessAsync(IReadOnlyList<InMessage> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return new Dictionary<TP, long>();
        using var _ = FlushSec.NewTimer();
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await ProcessOnce(batch, ct); }
                catch (OffsetFencedException ex) when (attempt < 2)
                {
                    log.LogWarning("fenced ({Msg}); reloading offsets and retrying", ex.Message);
                    _next = new Dictionary<TP, long>(await store.LoadOffsetsAsync(ct));
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyDictionary<TP, long>> ProcessOnce(IReadOnlyList<InMessage> batch, CancellationToken ct)
    {
        var commit = new Dictionary<TP, long>();
        var offsets = new Dictionary<TP, OffsetRange>();
        var aggs = new Dictionary<AggKey, long>();
        var recs = new Dictionary<string, Recognition>();
        var skipped = 0;

        foreach (var m in batch)
        {
            var tp = new TP(m.Topic, m.Partition);
            commit[tp] = Math.Max(commit.GetValueOrDefault(tp), m.Offset + 1);
            if (_next.TryGetValue(tp, out var n) && m.Offset < n) { skipped++; continue; }
            offsets[tp] = offsets.TryGetValue(tp, out var r)
                ? new OffsetRange(Math.Min(r.First, m.Offset), Math.Max(r.Next, m.Offset + 1))
                : new OffsetRange(m.Offset, m.Offset + 1);

            if (m.Value is null) continue;
            try
            {
                if (m.Topic == detectionsTopic)
                {
                    var d = JsonSerializer.Deserialize<Detection>(m.Value, Json.Options);
                    if (d is null || string.IsNullOrEmpty(d.CameraId)) { Invalid.WithLabels(m.Topic).Inc(); continue; }
                    Dets.Inc();
                    if (d.Objects is not { Count: > 0 }) continue;
                    var minute = d.Ts / 60_000 * 60_000;
                    foreach (var o in d.Objects)
                    {
                        var k = new AggKey(d.CameraId, o.Class, minute);
                        aggs[k] = aggs.GetValueOrDefault(k) + 1;
                    }
                }
                else
                {
                    var rec = JsonSerializer.Deserialize<Recognition>(m.Value, Json.Options);
                    if (rec is null || string.IsNullOrEmpty(rec.RecognitionId) || string.IsNullOrEmpty(rec.CameraId)
                        || rec.Type is not ("plate" or "face")) { Invalid.WithLabels(m.Topic).Inc(); continue; }
                    recs.TryAdd(rec.RecognitionId, rec);
                }
            }
            catch (JsonException) { Invalid.WithLabels(m.Topic).Inc(); }
        }
        if (skipped > 0) Skipped.Inc(skipped);

        var stored = recs.Values.Select(r => new StoredRecognition(r, ValidSnapshotKey(r))).ToArray();
        RecsC.Inc(stored.Length);

        if (offsets.Count > 0)
            await store.CommitBatchAsync(new BatchWrite(stored, aggs, offsets), ct);
        foreach (var (tp, r) in offsets) _next[tp] = Math.Max(_next.GetValueOrDefault(tp), r.Next);
        // skipped-only partitions need no DB write; advance the in-memory view for them as well
        foreach (var (tp, n) in commit) if (!_next.ContainsKey(tp)) _next[tp] = n;
        return commit;
    }

    /// <summary>The executor uploads the snapshot (contract 9.7); keep its key as given, tolerate null, reject odd keys.</summary>
    internal static string? ValidSnapshotKey(Recognition r) =>
        string.IsNullOrWhiteSpace(r.SnapshotKey) || r.SnapshotKey.Contains("..") || r.SnapshotKey.StartsWith('/') ? null : r.SnapshotKey;
}
