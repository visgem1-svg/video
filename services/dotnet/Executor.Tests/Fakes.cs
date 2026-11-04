using System.Collections.Concurrent;
using System.Text.Json;
using Executor;

namespace Executor.Tests;

public sealed class FakeTools : IToolInvoker
{
    public readonly ConcurrentQueue<string> Calls = new();
    public Func<string, CancellationToken, Task>? Delay;
    public HashSet<string> Failing = new();
    public Dictionary<string, StepResult> Outputs = new();

    public async ValueTask<StepResult> InvokeAsync(string tool, IReadOnlyDictionary<string, JsonElement>? p, ReadOnlyMemory<byte> image, CancellationToken ct)
    {
        Calls.Enqueue(tool);
        if (Delay is not null) await Delay(tool, ct);
        if (Failing.Contains(tool)) throw new HttpRequestException("boom");
        return Outputs.TryGetValue(tool, out var r) ? r : new StepResult(StepStatus.Ok);
    }
}

public sealed class FakeSnaps : ISnapshotStore
{
    public readonly ConcurrentQueue<(string Key, int Len)> Puts = new();
    public int FailFirst;      // number of initial PUT attempts that throw
    private int _attempts;
    public int Attempts => _attempts;
    public ValueTask PutAsync(string key, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _attempts) <= FailFirst) throw new HttpRequestException("s3 down");
        Puts.Enqueue((key, jpeg.Length));
        return default;
    }
}

public static class PipelineExt
{
    public static readonly byte[] Jpeg = { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 };
    public static Task ProcessAsync(this FramePipeline p, FrameMsg f, CancellationToken ct) => p.ProcessAsync(f, Jpeg, ct);
    public static Task ProcessJobAsync(this FramePipeline p, JobMsg j, CancellationToken ct) => p.ProcessJobAsync(j, Jpeg, ct);
}

public sealed class FakeSink : IResultSink
{
    public readonly ConcurrentQueue<(string Topic, string Key, string Json)> Msgs = new();
    public readonly ConcurrentQueue<(string Topic, byte[] Value, IReadOnlyDictionary<string, byte[]>? Headers)> Raw = new();
    public ValueTask PublishAsync(string topic, string key, byte[] value, CancellationToken ct, IReadOnlyDictionary<string, byte[]>? headers = null)
    {
        Raw.Enqueue((topic, value, headers));
        // jobs.heavy: value is the JPEG, the JSON is in header "job"
        var json = headers is not null && headers.TryGetValue("job", out var j) ? System.Text.Encoding.UTF8.GetString(j) : System.Text.Encoding.UTF8.GetString(value);
        Msgs.Enqueue((topic, key, json));
        return default;
    }
    public List<JsonElement> Of(string topic) => Msgs.Where(m => m.Topic == topic).Select(m => JsonDocument.Parse(m.Json).RootElement.Clone()).ToList();
}

public sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_761_030_000_000);
    public override DateTimeOffset GetUtcNow() => Now;
}

public static class T
{
    public static FrameMsg Frame(long ts = 1_761_030_000_000, string cam = "cam-001") =>
        new("f-" + Guid.NewGuid(), cam, ts, 1, 960, 540, cam + "/x/f.jpg", "fx-1");

    public static PlanStep Step(string id, string tool, When? when = null, string mode = "inline") => new(id, tool, mode, null, when);
    public static When W(string type, string? cls = null, double? min = null, int? gt = null, string? step = null) => new(type, cls, min, gt, step);
    public static Plan P(string id, int ver, params PlanStep[] steps) => new(id, ver, "n", "q", new(), "active", 0, steps.ToList());
    public static StepResult Objs(params (string c, double conf)[] o) =>
        new(StepStatus.Ok, Objects: o.Select(x => new ObjectDet(x.c, x.conf, new[] { 1.0, 2, 3, 4 })).ToList());

    public static (FramePipeline Pipe, PlanStore Store, FakeTools Tools, FakeSink Sink) Pipeline(FakeTools? tools = null, FakeSnaps? snaps = null)
    {
        tools ??= new FakeTools();
        var store = new PlanStore();
        var sink = new FakeSink();
        var o = new ExecutorOptions { ExecutorId = "ex-1" };
        var pipe = new FramePipeline(store, snaps ?? new FakeSnaps(), new PlanRunner(tools, new StepStats()), sink, o,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return (pipe, store, tools, sink);
    }
}
