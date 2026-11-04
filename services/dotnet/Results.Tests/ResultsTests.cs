using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Results;
using Xunit;

namespace Results.Tests;

/// <summary>Atomic in-memory store with the same semantics as the Postgres one (fence, DO NOTHING, additive upsert).</summary>
public sealed class FakeStore : IResultsStore
{
    public readonly Dictionary<string, StoredRecognition> Recognitions = new();
    public readonly Dictionary<AggKey, long> Counts = new();
    public readonly Dictionary<TP, long> Offsets = new();
    public int Commits;
    public int FailNext;
    public Action? BeforeCommit;

    public Task<IReadOnlyDictionary<TP, long>> LoadOffsetsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<TP, long>>(new Dictionary<TP, long>(Offsets));

    public Task CommitBatchAsync(BatchWrite w, CancellationToken ct)
    {
        BeforeCommit?.Invoke();
        BeforeCommit = null;
        if (FailNext > 0) { FailNext--; throw new InvalidOperationException("db down"); } // nothing applied: atomic
        foreach (var (tp, r) in w.Offsets)
            if (Offsets.TryGetValue(tp, out var s) && s > r.First) throw new OffsetFencedException("fenced");
        foreach (var r in w.Recognitions) Recognitions.TryAdd(r.R.RecognitionId, r);
        foreach (var (k, v) in w.Aggregates) Counts[k] = Counts.GetValueOrDefault(k) + v;
        foreach (var (tp, r) in w.Offsets) Offsets[tp] = Math.Max(Offsets.GetValueOrDefault(tp), r.Next);
        Commits++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecognitionDto>> QueryRecognitionsAsync(string? c, string? t, long? s, int l, CancellationToken ct, string? personId = null) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<DetectionStat>> QueryStatsAsync(string? c, long f, CancellationToken ct) => throw new NotSupportedException();
    public List<long> Deleted = new();
    public Task<int> DeleteRecognitionsOlderThanAsync(long cutoff, int batch, CancellationToken ct)
    {
        var victims = Recognitions.Values.Where(r => r.R.Ts < cutoff).OrderBy(r => r.R.Ts).Take(batch).Select(r => r.R.RecognitionId).ToList();
        foreach (var v in victims) Recognitions.Remove(v);
        Deleted.Add(cutoff);
        return Task.FromResult(victims.Count);
    }
}

public sealed class FakeSnapshots : ISnapshotStore
{
    public Task<bool> ReadAsync(string key, Stream d, CancellationToken ct) => Task.FromResult(false);
}
public class BatcherTests
{
    private const long Minute0 = 1761030000000 / 60000 * 60000;
    private readonly FakeStore _store = new();
    private ResultsBatcher NewBatcher() => new(_store, NullLogger<ResultsBatcher>.Instance);

    private static string Det(string cam, long ts, string frame = "f1", string[]? classes = null, bool? partial = null, string? step = null) =>
        JsonSerializer.Serialize(new Detection(frame, cam, ts, 960, 540, "default", 1,
            (classes ?? ["car"]).Select(c => new ObjectDet(c, 0.9, [1, 2, 3, 4])).ToList(), [], [], [], null, 1.0, "ex-1", partial, step), Json.Options);

    private static string Rec(string id, string type = "plate", long ts = 1761030000123, string? snapshotKey = "plate/20251021/x.jpg") =>
        JsonSerializer.Serialize(new Recognition(id, type, "cam-001", "f1", ts, "12345678", null, 0.95, [1, 2, 3, 4], snapshotKey, "p1"), Json.Options);

    private static InMessage D(long off, string? v, int p = 0) => new("detections", p, off, v);
    private static InMessage R(long off, string? v, int p = 0) => new("recognitions", p, off, v);

    [Fact]
    public async Task Aggregates_per_camera_class_minute_in_one_commit()
    {
        var b = NewBatcher();
        var msgs = new List<InMessage>();
        for (var i = 0; i < 500; i++) msgs.Add(D(i, Det("cam-001", Minute0 + i * 100, classes: ["car", "car", "person"])));
        msgs.Add(D(500, Det("cam-001", Minute0 + 61_000, classes: ["car"])));   // next minute
        msgs.Add(D(501, Det("cam-002", Minute0 + 10, classes: ["truck"])));
        await b.ProcessAsync(msgs, default);
        Assert.Equal(1, _store.Commits);
        Assert.Equal(1000, _store.Counts[new AggKey("cam-001", "car", Minute0)]);
        Assert.Equal(500, _store.Counts[new AggKey("cam-001", "person", Minute0)]);
        Assert.Equal(1, _store.Counts[new AggKey("cam-001", "car", Minute0 + 60_000)]);
        Assert.Equal(4, _store.Counts.Count); // 4 aggregate rows, not 502
        Assert.Equal(502, _store.Offsets[new TP("detections", 0)]);
    }

    [Fact]
    public async Task Redelivery_of_same_batch_does_not_double_count()
    {
        var b = NewBatcher();
        var msgs = new List<InMessage> { D(10, Det("cam-001", Minute0)), D(11, Det("cam-001", Minute0 + 5)) };
        await b.ProcessAsync(msgs, default);
        var commit = await b.ProcessAsync(msgs, default);   // same process, replayed
        Assert.Equal(2, _store.Counts[new AggKey("cam-001", "car", Minute0)]);
        Assert.Equal(12, commit[new TP("detections", 0)]);   // still reports offsets for the Kafka commit
        Assert.Equal(1, _store.Commits);
    }

    [Fact]
    public async Task Redelivery_after_restart_with_partial_overlap_counts_only_new_messages()
    {
        await NewBatcher().ProcessAsync([D(0, Det("c", Minute0)), D(1, Det("c", Minute0)), D(2, Det("c", Minute0))], default);
        // crash before Kafka commit: new instance, loads DB offsets, Kafka redelivers from offset 1 (older commit)
        var b2 = NewBatcher();
        await b2.LoadOffsetsAsync(default);
        await b2.ProcessAsync([D(1, Det("c", Minute0)), D(2, Det("c", Minute0)), D(3, Det("c", Minute0))], default);
        Assert.Equal(4, _store.Counts[new AggKey("c", "car", Minute0)]);
        Assert.Equal(4, _store.Offsets[new TP("detections", 0)]);
    }

    [Fact]
    public async Task Fenced_batch_reloads_offsets_and_does_not_double_count()
    {
        var b = NewBatcher();   // knows nothing (stale view)
        // another writer (zombie / new owner) processed offsets 0..1 between our load and our commit
        _store.BeforeCommit = () =>
        {
            _store.Counts[new AggKey("c", "car", Minute0)] = 2;
            _store.Offsets[new TP("detections", 0)] = 2;
        };
        await b.ProcessAsync([D(0, Det("c", Minute0)), D(1, Det("c", Minute0)), D(2, Det("c", Minute0))], default);
        Assert.Equal(3, _store.Counts[new AggKey("c", "car", Minute0)]);
    }

    [Fact]
    public async Task Failed_commit_applies_nothing_and_retry_is_exact()
    {
        var b = NewBatcher();
        var msgs = new List<InMessage> { D(0, Det("c", Minute0)), R(0, Rec("r1")) };
        _store.FailNext = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.ProcessAsync(msgs, default));
        Assert.Empty(_store.Counts); Assert.Empty(_store.Offsets);
        await b.ProcessAsync(msgs, default);
        Assert.Equal(1, _store.Counts[new AggKey("c", "car", Minute0)]);
        Assert.Single(_store.Recognitions);
    }

    [Fact]
    public async Task Partial_and_main_messages_for_same_frame_are_each_counted_once()
    {
        var b = NewBatcher();
        var msgs = new List<InMessage>
        {
            D(0, Det("c", Minute0, "f1", ["car"], partial: false, step: null)),
            D(1, Det("c", Minute0, "f1", ["person"], partial: true, step: "s2")),
        };
        await b.ProcessAsync(msgs, default);
        await b.ProcessAsync(msgs, default);   // redelivery
        Assert.Equal(1, _store.Counts[new AggKey("c", "car", Minute0)]);
        Assert.Equal(1, _store.Counts[new AggKey("c", "person", Minute0)]);
    }

    [Fact]
    public async Task Snapshot_key_from_executor_is_stored_as_given_once()
    {
        var b = NewBatcher();
        var id = "0b9d3b4e-7c8f-4c0e-9f1a-111111111111";
        await b.ProcessAsync([R(0, Rec(id)), R(1, Rec(id))], default);   // duplicate inside batch
        var rec = Assert.Single(_store.Recognitions.Values);
        Assert.Equal("plate/20251021/x.jpg", rec.SnapshotKey);
        await b.ProcessAsync([R(0, Rec(id))], default);   // redelivery
        Assert.Single(_store.Recognitions);
    }

    [Fact]
    public async Task Null_snapshot_key_is_tolerated_and_unsafe_keys_are_dropped()
    {
        await NewBatcher().ProcessAsync([R(0, Rec("r1", snapshotKey: null)), R(1, Rec("r2", snapshotKey: "../etc/passwd")), R(2, Rec("r3", "face", snapshotKey: "face/20251021/r3.jpg"))], default);
        Assert.Null(_store.Recognitions["r1"].SnapshotKey);
        Assert.Null(_store.Recognitions["r2"].SnapshotKey);
        Assert.Equal("face/20251021/r3.jpg", _store.Recognitions["r3"].SnapshotKey);
    }

    [Fact]
    public async Task Poison_messages_are_dropped_but_offsets_advance()
    {
        var commit = await NewBatcher().ProcessAsync([D(0, "{not json"), D(1, null), R(0, "[]"), D(2, Det("c", Minute0))], default);
        Assert.Equal(1, _store.Counts[new AggKey("c", "car", Minute0)]);
        Assert.Equal(3, commit[new TP("detections", 0)]);
        Assert.Empty(_store.Recognitions);
    }

    [Fact]
    public async Task Offsets_are_tracked_per_partition()
    {
        var b = NewBatcher();
        await b.ProcessAsync([D(5, Det("a", Minute0), p: 0), D(9, Det("b", Minute0), p: 3)], default);
        Assert.Equal(6, _store.Offsets[new TP("detections", 0)]);
        Assert.Equal(10, _store.Offsets[new TP("detections", 3)]);
    }
}

public class JsonContractTests
{
    [Fact]
    public void Detection_contract_example_parses_and_roundtrips_field_names()
    {
        const string json = """
        { "frame_id": "u", "camera_id": "cam-001", "ts": 1761030000123, "width": 960, "height": 540,
          "plan_id": "default", "plan_version": 3,
          "objects": [ { "class": "truck", "confidence": 0.91, "box": [1,2,3,4] } ],
          "plates":  [ { "plate": "12345678", "confidence": 0.95, "box": [1,2,3,4] } ],
          "faces":   [ { "box": [1,2,3,4], "confidence": 0.9, "person_id": "p-12", "person_name": "Dana", "score": 0.71 } ],
          "texts":   [ { "text": "GATE 3", "confidence": 0.88, "box": [1,2,3,4] } ],
          "timings_ms": { "s1": 120.5, "s2": 33.1 }, "total_ms": 160.2, "executor_id": "ex-1",
          "partial": true, "step_id": "s2" }
        """;
        var d = JsonSerializer.Deserialize<Detection>(json, Json.Options)!;
        Assert.Equal("truck", d.Objects![0].Class);
        Assert.Equal("Dana", d.Faces![0].PersonName);
        Assert.Equal(33.1, d.TimingsMs!["s2"]);
        Assert.True(d.Partial);
        Assert.Equal("s2", d.StepId);
        var names = JsonDocument.Parse(JsonSerializer.Serialize(d, Json.Options)).RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        var expected = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(expected, names);
        var obj = JsonDocument.Parse(JsonSerializer.Serialize(d, Json.Options)).RootElement.GetProperty("objects")[0];
        Assert.True(obj.TryGetProperty("class", out _));
    }

    [Fact]
    public void Recognition_contract_example_parses()
    {
        const string json = """
        { "recognition_id": "uuid", "type": "plate", "camera_id": "cam-001", "frame_id": "uuid",
          "ts": 1761030000123, "value": "12345678", "person_id": null,
          "confidence": 0.95, "box": [1,2,3,4], "snapshot_key": "plate/20251021/uuid.jpg", "plan_id": "uuid" }
        """;
        var r = JsonSerializer.Deserialize<Recognition>(json, Json.Options)!;
        Assert.Equal("plate/20251021/uuid.jpg", r.SnapshotKey);
        var names = JsonDocument.Parse(JsonSerializer.Serialize(r, Json.Options)).RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(), names);
    }

    [Fact]
    public void Api_dtos_use_snake_case()
    {
        var dto = new RecognitionDto("i", "plate", "c", "f", 1, "v", null, 0.9, [1, 2, 3, 4], "p", "plate/20251021/i.jpg", "/api/snapshots/plate/20251021/i.jpg");
        var names = JsonDocument.Parse(JsonSerializer.Serialize(dto, Json.Options)).RootElement.EnumerateObject().Select(p => p.Name);
        Assert.Contains("snapshot_url", names);
        Assert.Contains("recognition_id", names);
        var stat = JsonDocument.Parse(JsonSerializer.Serialize(new DetectionStat("c", "car", 60000, 3), Json.Options)).RootElement;
        Assert.Equal(new[] { "camera_id", "class", "minute_ts", "count" }, stat.EnumerateObject().Select(p => p.Name));
    }
}

public class PersonFilterTests
{
    [Fact]
    public void Person_id_index_migration_is_embedded()
    {
        var asm = typeof(Detection).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("002_person_index.sql"));
        using var rd = new StreamReader(asm.GetManifestResourceStream(name)!);
        var sql = rd.ReadToEnd();
        Assert.Contains("ON recognitions (person_id", sql);
    }

    [Fact]
    public void Store_interface_accepts_person_filter()
    {
        // compile-time + signature check: the endpoint passes person_id through IResultsStore
        var m = typeof(IResultsStore).GetMethod(nameof(IResultsStore.QueryRecognitionsAsync))!;
        Assert.Contains(m.GetParameters(), p => p.Name == "personId");
    }
}

public class RetentionTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static StoredRecognition Rec(string id, DateTimeOffset ts) =>
        new(new Recognition(id, "plate", "c", "f", ts.ToUnixTimeMilliseconds(), "v", null, 0.9, null, null, null), null);

    private static RetentionJob Job(FakeStore s, int days) =>
        new(s, new ResultsOptions { RecognitionRetentionDays = days }, new Clock(Now), NullLogger<RetentionJob>.Instance);

    [Fact]
    public async Task Deletes_only_rows_older_than_horizon()
    {
        var s = new FakeStore();
        s.Recognitions["old"] = Rec("old", Now.AddDays(-2));
        s.Recognitions["edge"] = Rec("edge", Now.AddDays(-1).AddMinutes(1));
        s.Recognitions["new"] = Rec("new", Now.AddHours(-1));
        var n = await Job(s, 1).RunOnceAsync(default);
        Assert.Equal(1, n);
        Assert.Equal(new[] { "edge", "new" }, s.Recognitions.Keys.Order());
    }

    [Fact]
    public async Task Works_in_batches_until_everything_old_is_gone()
    {
        var s = new FakeStore();
        for (var i = 0; i < 2500; i++) s.Recognitions["o" + i] = Rec("o" + i, Now.AddDays(-10).AddSeconds(i));
        s.Recognitions["keep"] = Rec("keep", Now);
        var n = await Job(s, 7).RunOnceAsync(default, TimeSpan.Zero);
        Assert.Equal(2500, n);
        Assert.Equal(3, s.Deleted.Count);          // 1000 + 1000 + 500 (short batch ends the run)
        Assert.Single(s.Recognitions);
    }

    [Fact]
    public async Task Disabled_when_days_is_zero()
    {
        var s = new FakeStore();
        s.Recognitions["old"] = Rec("old", Now.AddDays(-100));
        Assert.Equal(0, await Job(s, 0).RunOnceAsync(default));
        Assert.Single(s.Recognitions);
    }
}
