using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using RealtimeGateway;

namespace RealtimeGateway.Tests;

sealed class Proxy : IClientProxy
{
    public string Target = "";
    public List<(string Target, string Method, object?[] Args)> Log;
    public Proxy(List<(string, string, object?[])> log, string target) { Log = log; Target = target; }
    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) { lock (Log) Log.Add((Target, method, args)); return Task.CompletedTask; }
}

sealed class FakeClients : IHubClients
{
    public readonly List<(string Target, string Method, object?[] Args)> Log = new();
    public IClientProxy All => new Proxy(Log, "all");
    public IClientProxy AllExcept(IReadOnlyList<string> c) => throw new NotImplementedException();
    public IClientProxy Client(string id) => throw new NotImplementedException();
    public IClientProxy Clients(IReadOnlyList<string> ids) => throw new NotImplementedException();
    public IClientProxy Group(string g) => new Proxy(Log, g);
    public IClientProxy GroupExcept(string g, IReadOnlyList<string> c) => throw new NotImplementedException();
    public IClientProxy Groups(IReadOnlyList<string> g) => throw new NotImplementedException();
    public IClientProxy User(string u) => throw new NotImplementedException();
    public IClientProxy Users(IReadOnlyList<string> u) => throw new NotImplementedException();
}

sealed class FakeHub : IHubContext<LiveHub>
{
    public readonly FakeClients C = new();
    public IHubClients Clients => C;
    public IGroupManager Groups => throw new NotImplementedException();
}

public class ThrottleTests
{
    [Fact] public void At_most_three_per_second_per_camera_and_latest_wins()
    {
        var t = new DetectionThrottler<string>(3);
        var sent = new List<string>();
        for (var i = 0; i < 10; i++) { var r = t.Offer("cam-1", "m" + i, 1000 + i * 10); if (r is not null) sent.Add(r); }
        Assert.Equal(new[] { "m0", "m1", "m2" }, sent);   // m3..m8 were superseded, m9 is pending

        var flush = new List<(string, string)>();
        t.Flush(1500, flush);
        Assert.Empty(flush);                               // window not elapsed
        t.Flush(2000, flush);
        Assert.Equal(("cam-1", "m9"), flush.Single());     // newest delivered, older dropped
        Assert.Equal(6, t.Dropped);
    }

    [Fact] public void Cameras_are_independent_and_steady_3fps_is_not_throttled()
    {
        var t = new DetectionThrottler<string>(3);
        Assert.NotNull(t.Offer("a", "1", 0)); Assert.NotNull(t.Offer("a", "2", 1)); Assert.NotNull(t.Offer("a", "3", 2));
        Assert.Null(t.Offer("a", "4", 3));
        Assert.NotNull(t.Offer("b", "1", 3));
        var u = new DetectionThrottler<string>(3);
        for (var i = 0; i < 30; i++) Assert.NotNull(u.Offer("c", "x", i * 334L));   // 3 fps source passes untouched
        Assert.Equal(0, u.Dropped);
    }
}

public class RouterTests
{
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact] public async Task Detections_only_for_subscribed_cameras_and_throttled()
    {
        var hub = new FakeHub(); var state = new LiveState(); long now = 0;
        var r = new EventRouter(hub, state, new DetectionThrottler<EventRouter.Boxed>(3), () => now);
        await r.OnDetectionAsync("cam-1", B("{\"camera_id\":\"cam-1\"}"));
        Assert.Empty(hub.C.Log);

        state.AddSub("cam-1");
        for (var i = 0; i < 6; i++) { now = i * 10; await r.OnDetectionAsync("cam-1", B("{\"camera_id\":\"cam-1\",\"n\":" + i + "}")); }
        Assert.Equal(3, hub.C.Log.Count);
        Assert.All(hub.C.Log, l => { Assert.Equal("cam:cam-1", l.Target); Assert.Equal("detections", l.Method); });
        now = 1100; await r.FlushPendingAsync();
        Assert.Equal(4, hub.C.Log.Count);
        Assert.Equal(5, ((JsonElement)hub.C.Log[3].Args[0]!).GetProperty("n").GetInt32());
    }

    [Fact] public async Task Health_and_plans_are_cached_for_snapshot_and_pushed_to_all_after_ready()
    {
        var hub = new FakeHub(); var state = new LiveState();
        var r = new EventRouter(hub, state, new DetectionThrottler<EventRouter.Boxed>());
        await r.OnHealthAsync("cam-1", B("{\"camera_id\":\"cam-1\",\"state\":\"online\"}"));
        await r.OnPlanAsync("p1", B("{\"plan_id\":\"p1\",\"version\":2}"));
        Assert.Empty(hub.C.Log);                       // initial load: cached only
        Assert.Single(state.Health); Assert.Single(state.Plans);

        state.MarkReady();
        await r.OnHealthAsync("cam-1", B("{\"camera_id\":\"cam-1\",\"state\":\"offline\"}"));
        await r.OnPlanAsync("p1", B("{\"plan_id\":\"p1\",\"version\":3}"));
        Assert.Equal(new[] { ("all", "cameraHealth"), ("all", "planChanged") }, hub.C.Log.Select(l => (l.Target, l.Method)).ToArray());
        Assert.Equal("offline", state.Health["cam-1"].GetProperty("state").GetString());

        await r.OnPlanAsync("p1", null);               // tombstone
        Assert.Empty(state.Plans);
        Assert.True(((JsonElement)hub.C.Log.Last().Args[0]!).GetProperty("deleted").GetBoolean());
    }

    [Fact] public async Task Metrics_count_sent_and_dropped()
    {
        var hub = new FakeHub(); var state = new LiveState(); long now = 0;
        var r = new EventRouter(hub, state, new DetectionThrottler<EventRouter.Boxed>(3), () => now);
        var sent0 = Telemetry.Sent.WithLabels("detections").Value;
        var nosub0 = Telemetry.Dropped.WithLabels("no_subscribers").Value;
        var thr0 = Telemetry.Dropped.WithLabels("throttled").Value;
        await r.OnDetectionAsync("m-1", B("{}"));
        state.AddSub("m-1");
        for (var i = 0; i < 5; i++) await r.OnDetectionAsync("m-1", B("{}"));   // 3 sent, 2 pending (1 superseded)
        Assert.Equal(1, Telemetry.Dropped.WithLabels("no_subscribers").Value - nosub0);
        Assert.Equal(3, Telemetry.Sent.WithLabels("detections").Value - sent0);
        Assert.Equal(1, Telemetry.Dropped.WithLabels("throttled").Value - thr0);
    }

    [Fact] public async Task Recognition_to_everyone_and_metrics_to_plan_group()
    {
        var hub = new FakeHub();
        var r = new EventRouter(hub, new LiveState(), new DetectionThrottler<EventRouter.Boxed>());
        await r.OnRecognitionAsync(B("{\"type\":\"plate\"}"));
        await r.OnMetricsAsync("p1", B("{\"plan_id\":\"p1\"}"));
        Assert.Equal(new[] { ("all", "recognition"), ("plan:p1", "pipelineMetrics") }, hub.C.Log.Select(l => (l.Target, l.Method)).ToArray());
    }

    [Fact] public void Subscriber_counts_and_unique_group_ids()
    {
        var s = new LiveState();
        s.AddSub("c"); s.AddSub("c"); s.RemoveSub("c");
        Assert.True(s.HasSubscribers("c"));
        s.RemoveSub("c");
        Assert.False(s.HasSubscribers("c"));
        var a = GatewayOptions.FromEnvironment(_ => null); var b = GatewayOptions.FromEnvironment(_ => null);
        Assert.NotEqual(a.GroupId, b.GroupId);
        Assert.StartsWith("realtime-gateway-", a.GroupId);
    }
}
