using System.Text.Json;
using Executor;
using static Executor.Tests.T;

namespace Executor.Tests;

public class ConditionTests
{
    static Dictionary<string, StepResult> R(StepResult s1) => new() { ["s1"] = s1 };

    [Fact] public void Always_and_missing_are_true() { Assert.True(Conditions.Evaluate(null, new Dictionary<string, StepResult>())); Assert.True(Conditions.Evaluate(W("always"), new Dictionary<string, StepResult>())); }

    [Fact] public void Found_respects_class_and_confidence()
    {
        var r = R(Objs(("truck", 0.6), ("cell phone", 0.3)));
        Assert.True(Conditions.Evaluate(W("found", "truck"), r));
        Assert.True(Conditions.Evaluate(W("found", "TRUCK", 0.5), r));
        Assert.False(Conditions.Evaluate(W("found", "truck", 0.7), r));
        Assert.True(Conditions.Evaluate(W("found", "Cell Phone"), r));
        Assert.False(Conditions.Evaluate(W("found", "cell phone", 0.5), r));
        Assert.False(Conditions.Evaluate(W("found", "car"), r));
    }

    [Fact] public void Count_is_strictly_greater_and_unions_steps()
    {
        var r = R(Objs(("person", 0.9), ("person", 0.8), ("car", 0.9)));
        Assert.True(Conditions.Evaluate(W("count", "person", gt: 1), r));
        Assert.False(Conditions.Evaluate(W("count", "person", gt: 2), r));
        r["s2"] = Objs(("person", 0.5));
        Assert.True(Conditions.Evaluate(W("count", "person", gt: 2), r));
    }

    [Fact] public void Class_may_be_a_comma_separated_any_of_list()
    {
        var r = R(Objs(("car", 0.9), ("truck", 0.4), ("bus", 0.8), ("person", 0.9)));
        Assert.True(Conditions.Evaluate(W("found", "bicycle, Truck ,bus"), r));
        Assert.False(Conditions.Evaluate(W("found", "bicycle,truck", 0.5), r));
        Assert.False(Conditions.Evaluate(W("found", "bicycle,motorcycle"), r));
        Assert.True(Conditions.Evaluate(W("count", "car,truck,bus", gt: 2), r));
        Assert.False(Conditions.Evaluate(W("count", "car,truck,bus", gt: 3), r));
    }

    [Fact] public void Breaker_ignores_429_but_trips_on_exceptions_timeouts_and_5xx()
    {
        Assert.False(ToolResilience.TripsBreaker(null, System.Net.HttpStatusCode.TooManyRequests));
        Assert.False(ToolResilience.TripsBreaker(null, System.Net.HttpStatusCode.OK));
        Assert.True(ToolResilience.TripsBreaker(null, System.Net.HttpStatusCode.ServiceUnavailable));
        Assert.True(ToolResilience.TripsBreaker(new TimeoutException(), null));
    }

    [Fact] public void Failed_matches_only_failed_steps()
    {
        Assert.True(Conditions.Evaluate(W("failed", step: "s1"), R(new StepResult(StepStatus.Failed))));
        Assert.False(Conditions.Evaluate(W("failed", step: "s1"), R(Objs())));
        Assert.False(Conditions.Evaluate(W("failed", step: "s1"), R(new StepResult(StepStatus.Skipped))));
        Assert.False(Conditions.Evaluate(W("failed", step: "s9"), R(Objs())));
    }

    [Fact] public void Skipped_or_failed_step_never_satisfies_found()
    {
        Assert.False(Conditions.Evaluate(W("found", "truck"), R(new StepResult(StepStatus.Failed))));
        Assert.False(Conditions.Evaluate(W("found", "truck"), R(new StepResult(StepStatus.Skipped))));
    }
}

public class PlanExecutionTests
{
    [Fact] public async Task Steps_run_in_order_and_conditions_skip()
    {
        var (pipe, store, tools, sink) = Pipeline();
        tools.Outputs[Tools.DetectObjects] = Objs(("truck", 0.9));
        store.ApplyPlan("p", P("p", 1,
            Step("s1", Tools.DetectObjects),
            Step("s2", Tools.ReadPlate, W("found", "truck")),
            Step("s3", Tools.DetectFaces, W("found", "person")),
            Step("s4", Tools.ReadText, W("always"))));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));

        await pipe.ProcessAsync(Frame(), CancellationToken.None);

        Assert.Equal(new[] { Tools.DetectObjects, Tools.ReadPlate, Tools.ReadText }, tools.Calls.ToArray());
        var ds = sink.Of("detections");
        Assert.Equal(3, ds.Count);                                   // s1 + s2 + s4; skipped s3 publishes nothing
        Assert.All(ds, d => Assert.Equal("p", d.GetProperty("plan_id").GetString()));
        Assert.Equal(new string?[] { null, "s2", "s4" }, ds.Select(d => d.GetProperty("step_id").GetString()).ToArray());
        Assert.Equal(new[] { false, true, true }, ds.Select(d => d.GetProperty("partial").GetBoolean()).ToArray());
        Assert.All(ds, d => Assert.Single(d.GetProperty("timings_ms").EnumerateObject()));
        Assert.DoesNotContain(ds, d => d.GetProperty("timings_ms").TryGetProperty("s3", out _));
    }

    [Fact] public async Task Idempotency_keys_frame_id_and_step_id_are_unique_and_all_failed_frame_still_emits_one_main()
    {
        var (pipe, store, tools, sink) = Pipeline();
        store.ApplyPlan("p", P("p", 1, Step("s1", Tools.DetectObjects), Step("s2", Tools.ReadText), Step("s3", Tools.ReadPlate)));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        var keys = sink.Of("detections").Select(d => d.GetProperty("frame_id").GetString() + "/" + (d.GetProperty("step_id").GetString() ?? "main")).ToList();
        Assert.Equal(6, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(2, keys.Count(k => k.EndsWith("/main")));

        tools.Failing.Add(Tools.DetectObjects); tools.Failing.Add(Tools.ReadText); tools.Failing.Add(Tools.ReadPlate);
        var before = sink.Of("detections").Count;
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        var added = sink.Of("detections").Skip(before).ToList();
        Assert.Single(added);
        Assert.False(added[0].GetProperty("partial").GetBoolean());
    }

    [Fact] public async Task First_completed_step_is_main_even_if_an_earlier_step_failed()
    {
        var tools = new FakeTools(); tools.Failing.Add(Tools.IdentifyFaces);
        var (pipe, store, _, sink) = Pipeline(tools);
        store.ApplyPlan("p", P("p", 1, Step("s1", Tools.IdentifyFaces), Step("s2", Tools.DetectFaces, W("failed", step: "s1")), Step("s3", Tools.ReadText)));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        var ds = sink.Of("detections");
        Assert.Equal(new string?[] { null, "s3" }, ds.Select(d => d.GetProperty("step_id").GetString()).ToArray());
    }

    [Fact] public async Task Failed_branch_runs_fallback_and_frame_still_emits()
    {
        var tools = new FakeTools();
        tools.Failing.Add(Tools.IdentifyFaces);
        var (pipe, store, _, sink) = Pipeline(tools);
        store.ApplyPlan("p", P("p", 1,
            Step("s1", Tools.IdentifyFaces),
            Step("s2", Tools.DetectFaces, W("failed", step: "s1"))));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        Assert.Equal(new[] { Tools.IdentifyFaces, Tools.DetectFaces }, tools.Calls.ToArray());
        Assert.Single(sink.Of("detections"));
    }

    [Fact] public async Task Skipped_step_is_not_failed()
    {
        var (pipe, store, tools, _) = Pipeline();
        store.ApplyPlan("p", P("p", 1,
            Step("s1", Tools.DetectObjects),
            Step("s2", Tools.ReadPlate, W("found", "truck")),
            Step("s3", Tools.ReadText, W("failed", step: "s2"))));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        Assert.Equal(new[] { Tools.DetectObjects }, tools.Calls.ToArray());
    }

    [Fact] public async Task No_plan_for_null_unknown_missing_or_inactive_plan_and_disabled_camera_is_skipped_without_work()
    {
        var (pipe, store, tools, sink) = Pipeline();
        await pipe.ProcessAsync(Frame(cam: "unknown"), CancellationToken.None);
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, null));
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        store.ApplyCamera("cam-003", new CameraConfig("cam-003", true, "ghost"));
        await pipe.ProcessAsync(Frame(cam: "cam-003"), CancellationToken.None);
        store.ApplyPlan("off", P("off", 1, Step("s1", Tools.ReadText)) with { Status = "disabled" });
        store.ApplyCamera("cam-004", new CameraConfig("cam-004", true, "off"));
        await pipe.ProcessAsync(Frame(cam: "cam-004"), CancellationToken.None);
        store.ApplyCamera("cam-002", new CameraConfig("cam-002", false, null));
        await pipe.ProcessAsync(Frame(cam: "cam-002"), CancellationToken.None);
        Assert.Empty(tools.Calls);
        Assert.Empty(sink.Of("detections"));
        Assert.Equal((null, "no_plan"), (store.Resolve("cam-001").Plan, store.Resolve("cam-001").SkipReason));
        Assert.Equal("camera_disabled", store.Resolve("cam-002").SkipReason);
    }

    [Fact] public async Task Version_swap_mid_flight_finishes_on_old_version()
    {
        var tools = new FakeTools();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        tools.Delay = async (t, ct) => { if (t == Tools.DetectObjects) { started.TrySetResult(); await gate.Task; } };
        var (pipe, store, _, sink) = Pipeline(tools);
        store.ApplyPlan("p", P("p", 1, Step("s1", Tools.DetectObjects), Step("s2", Tools.ReadText)));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));

        var f1 = pipe.ProcessAsync(Frame(), CancellationToken.None);
        await started.Task;
        store.ApplyPlan("p", P("p", 2, Step("s1", Tools.DetectObjects), Step("s2", Tools.ReadPlate)));
        gate.SetResult();
        await f1;
        await pipe.ProcessAsync(Frame(), CancellationToken.None);

        var ds = sink.Of("detections");
        Assert.Equal(new[] { 1, 1, 2, 2 }, ds.Select(d => d.GetProperty("plan_version").GetInt32()).ToArray());
        Assert.Contains(Tools.ReadText, tools.Calls);   // v1 frame used v1's s2
        Assert.Contains(Tools.ReadPlate, tools.Calls);  // v2 frame used v2's s2
    }

    [Fact] public async Task Queue_mode_publishes_job_instead_of_calling_tool_and_heavy_job_emits()
    {
        var (pipe, store, tools, sink) = Pipeline();
        store.ApplyPlan("p", P("p", 3, Step("s1", Tools.DetectObjects), Step("s2", Tools.ReadText, mode: "queue")));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        Assert.Equal(new[] { Tools.DetectObjects }, tools.Calls.ToArray());
        var job = sink.Of("jobs.heavy").Single();
        Assert.Equal("s2", job.GetProperty("step_id").GetString());
        Assert.Equal(3, job.GetProperty("plan_version").GetInt32());

        var msg = JsonSerializer.Deserialize(job.GetRawText(), Json.Default.JobMsg)!;
        await pipe.ProcessJobAsync(msg, CancellationToken.None);
        Assert.Equal(Tools.ReadText, tools.Calls.Last());
        var ds = sink.Of("detections");
        Assert.Equal(2, ds.Count);
        Assert.False(ds[0].GetProperty("partial").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ds[0].GetProperty("step_id").ValueKind);
        Assert.True(ds[1].GetProperty("partial").GetBoolean());
        Assert.Equal("s2", ds[1].GetProperty("step_id").GetString());
    }
}

public class MappingTests
{
    [Fact] public async Task Detection_and_recognition_json_field_names_match_contract()
    {
        var tools = new FakeTools();
        tools.Outputs[Tools.DetectObjects] = Objs(("truck", 0.91));
        tools.Outputs[Tools.ReadPlate] = new StepResult(StepStatus.Ok, Plates: new() { new PlateDet("12345678", 0.95, new[] { 1.0, 2, 3, 4 }) });
        tools.Outputs[Tools.ReadText] = new StepResult(StepStatus.Ok, Texts: new() { new TextDet("GATE 3", 0.88, new[] { 1.0, 2, 3, 4 }) });
        tools.Outputs[Tools.IdentifyFaces] = new StepResult(StepStatus.Ok, Faces: new()
        {
            new FaceDet(new[] { 1.0, 2, 3, 4 }, 0.9, "p-12", "Dana", 0.71),
            new FaceDet(new[] { 5.0, 6, 7, 8 }, 0.8, null, null, 0.1),
        });
        var (pipe, store, _, sink) = Pipeline(tools);
        store.ApplyPlan("p", P("p", 3, Step("s1", Tools.DetectObjects), Step("s2", Tools.ReadPlate), Step("s3", Tools.ReadText), Step("s4", Tools.IdentifyFaces)));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));
        var f = Frame();
        await pipe.ProcessAsync(f, CancellationToken.None);

        var all = sink.Of("detections");
        Assert.Equal(4, all.Count);
        foreach (var m in all)
            foreach (var n in new[] { "frame_id", "camera_id", "ts", "width", "height", "plan_id", "plan_version", "objects", "plates", "faces", "texts", "timings_ms", "total_ms", "executor_id", "partial", "step_id" })
                Assert.True(m.TryGetProperty(n, out _), n);
        var d0 = all[0];
        Assert.Equal("truck", d0.GetProperty("objects")[0].GetProperty("class").GetString());
        Assert.Equal(4, d0.GetProperty("objects")[0].GetProperty("box").GetArrayLength());
        Assert.Equal("12345678", all[1].GetProperty("plates")[0].GetProperty("plate").GetString());
        Assert.Equal("GATE 3", all[2].GetProperty("texts")[0].GetProperty("text").GetString());
        var d = all[3];
        var face = d.GetProperty("faces")[0];
        foreach (var n in new[] { "box", "confidence", "person_id", "person_name", "score" }) Assert.True(face.TryGetProperty(n, out _), n);
        Assert.Equal(JsonValueKind.Null, d.GetProperty("faces")[1].GetProperty("person_id").ValueKind);

        var recs = sink.Of("recognitions");
        Assert.Equal(3, recs.Count);
        foreach (var r in recs)
            foreach (var n in new[] { "recognition_id", "type", "camera_id", "frame_id", "ts", "value", "person_id", "confidence", "box", "snapshot_key", "plan_id" })
                Assert.True(r.TryGetProperty(n, out _), n);
        Assert.Contains(recs, r => r.GetProperty("type").GetString() == "plate" && r.GetProperty("value").GetString() == "12345678");
        Assert.Contains(recs, r => r.GetProperty("value").GetString() == "Dana" && r.GetProperty("person_id").GetString() == "p-12");
        Assert.Contains(recs, r => r.GetProperty("value").GetString() == "unknown");
        Assert.All(recs, r => Assert.Matches(@"^(plate|face)/20\d{6}/[0-9a-f-]{36}\.jpg$", r.GetProperty("snapshot_key").GetString()!));
        Assert.All(recs, r => Assert.False(r.TryGetProperty("frame_key", out _)));
        Assert.All(sink.Msgs, m => Assert.Equal("cam-001", m.Key));
    }

    [Fact] public async Task Engine_responses_parse_per_contract_7_1()
    {
        static MemoryStream S(string j) => new(System.Text.Encoding.UTF8.GetBytes(j));
        var o = await HttpToolInvoker.ParseAsync(Tools.DetectObjects, S("{\"objects\":[{\"class\":\"car\",\"confidence\":0.8,\"box\":[1,2,3,4]}],\"count\":1,\"ms\":3}"), default);
        Assert.Equal("car", o.Objects![0].Class);
        var p = await HttpToolInvoker.ParseAsync(Tools.ReadPlate, S("{\"plates\":[{\"plate\":\"AB\",\"confidence\":0.9,\"detection_confidence\":0.8,\"box\":[1,2,3,4]}],\"count\":1,\"ms\":3}"), default);
        Assert.Equal("AB", p.Plates![0].Plate);
        var t = await HttpToolInvoker.ParseAsync(Tools.ReadText, S("{\"lines\":[{\"text\":\"hi\",\"confidence\":0.7,\"box\":[1,2,3,4]}],\"count\":1,\"ms\":3}"), default);
        Assert.Equal("hi", t.Texts![0].Text);
        var f = await HttpToolInvoker.ParseAsync(Tools.IdentifyFaces, S("{\"faces\":[{\"box\":[1,2,3,4],\"confidence\":0.9,\"person_id\":\"p1\",\"person_name\":\"A\",\"score\":0.5}],\"count\":1,\"ms\":3}"), default);
        Assert.Equal("A", f.Faces![0].PersonName);
        var df = await HttpToolInvoker.ParseAsync(Tools.DetectFaces, S("{\"faces\":[{\"box\":[1,2,3,4],\"confidence\":0.9}],\"count\":1,\"ms\":3}"), default);
        Assert.Null(df.Faces![0].PersonName);
    }

    [Fact] public void Env_urls_follow_infra_env_md()
    {
        var env = new Dictionary<string, string> { ["DETECTOR_URL"] = "http://x:1/", ["FACE_URL"] = "http://f:2", ["TOOL_CONCURRENCY_READ_TEXT"] = "7", ["MINIO_ENDPOINT"] = "minio:9000", ["MINIO_USE_SSL"] = "false", ["MINIO_SNAPSHOTS_BUCKET"] = "fr", ["KAFKA_BOOTSTRAP"] = "k:1" };
        var o = ExecutorOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("http://x:1/detect_objects", o.ToolUrls[Tools.DetectObjects]);
        Assert.Equal("http://f:2/identify_faces", o.ToolUrls[Tools.IdentifyFaces]);
        Assert.Equal("http://lpr:8102/read_plate", o.ToolUrls[Tools.ReadPlate]);
        Assert.Equal(7, o.ToolConcurrency[Tools.ReadText]);
        Assert.Equal("http://minio:9000", o.MinioEndpoint);
        Assert.Equal("fr", o.SnapshotsBucket);
        Assert.Equal("k:1", o.KafkaBootstrap);
    }

    [Fact] public void SigV4_matches_aws_documented_example()
    {
        var h = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com", ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["x-amz-date"] = "20130524T000000Z",
        };
        var a = SigV4.Authorization("GET", "/test.txt", "", h, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", "20130524T000000Z");
        Assert.EndsWith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", a);
    }
}

public class ConcurrencyTests
{
    sealed class Probe : IToolInvoker
    {
        public int Cur, Max;
        public async ValueTask<StepResult> InvokeAsync(string tool, IReadOnlyDictionary<string, JsonElement>? p, ReadOnlyMemory<byte> i, CancellationToken ct)
        {
            var c = Interlocked.Increment(ref Cur);
            int m; while (c > (m = Volatile.Read(ref Max))) Interlocked.CompareExchange(ref Max, c, m);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref Cur);
            return new StepResult(StepStatus.Ok);
        }
    }

    [Fact] public async Task Per_tool_limit_is_honored_and_tools_are_independent()
    {
        var probe = new Probe();
        var inv = new LimitedToolInvoker(probe, new ToolGate(new Dictionary<string, int> { [Tools.DetectObjects] = 3, [Tools.ReadText] = 1 }));
        var tasks = Enumerable.Range(0, 20).Select(_ => inv.InvokeAsync(Tools.DetectObjects, null, default, default).AsTask()).ToList();
        await Task.WhenAll(tasks);
        Assert.Equal(3, probe.Max);

        probe.Max = 0;
        var ocr = Enumerable.Range(0, 6).Select(_ => inv.InvokeAsync(Tools.ReadText, null, default, default).AsTask());
        await Task.WhenAll(ocr);
        Assert.Equal(1, probe.Max);
    }
}

public class OffsetTests
{
    [Fact] public void Out_of_order_completion_commits_only_contiguous_prefix()
    {
        var t = new OffsetTracker();
        for (var o = 10; o < 14; o++) t.Register(0, o);
        Assert.Null(t.Complete(0, 12));
        Assert.Null(t.Complete(0, 11));
        Assert.Equal(13, t.Complete(0, 10));   // 10,11,12 done; 13 still running -> next offset to commit = 13
    }

    [Fact] public void Contiguous_run_includes_already_completed_followers()
    {
        var t = new OffsetTracker();
        for (var o = 0; o < 5; o++) t.Register(1, o);
        t.Complete(1, 2); t.Complete(1, 1);
        Assert.Equal(3, t.Complete(1, 0));
        Assert.Null(t.Complete(1, 4));
        Assert.Equal(5, t.Complete(1, 3));
        Assert.Equal(0, t.Pending(1));
    }

    [Fact] public void Gaps_in_offsets_and_partitions_are_independent()
    {
        var t = new OffsetTracker();
        t.Register(0, 5); t.Register(0, 9); t.Register(1, 1);
        Assert.Equal(6, t.Complete(0, 5));
        Assert.Equal(2, t.Complete(1, 1));
        Assert.Equal(10, t.Complete(0, 9));
        t.Remove(0);
        Assert.Null(t.Complete(0, 99));
    }
}

public class DispatcherTests
{
    [Fact] public async Task Stale_frames_are_skipped_counted_and_offset_advances()
    {
        var time = new FakeTime();
        var skips = new List<string>(); var commits = new List<(int, long)>(); var handled = 0;
        var d = new WorkDispatcher<FrameMsg>(4, 3000, f => f.Ts, (f, ct) => { Interlocked.Increment(ref handled); return Task.CompletedTask; },
            new OffsetTracker(), (p, o) => { lock (commits) commits.Add((p, o)); }, time, r => skips.Add(r));
        var now = time.Now.ToUnixTimeMilliseconds();
        await d.SubmitAsync(0, 0, Frame(now - 5000), default);   // stale
        await d.SubmitAsync(0, 1, Frame(now - 100), default);    // fresh
        await d.DrainAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(new[] { "stale" }, skips);
        Assert.Equal(1, handled);
        lock (commits) Assert.Equal((0, 2L), commits.Last());
    }

    [Fact] public async Task Inflight_is_bounded_and_commit_waits_for_slowest_earlier_frame()
    {
        var time = new FakeTime();
        var release = new TaskCompletionSource[3];
        for (var i = 0; i < 3; i++) release[i] = new TaskCompletionSource();
        var commits = new List<long>(); int cur = 0, max = 0;
        var d = new WorkDispatcher<FrameMsg>(2, 0, f => f.Ts, async (f, ct) =>
        {
            var c = Interlocked.Increment(ref cur); lock (commits) max = Math.Max(max, c);
            await release[(int)f.Seq].Task;
            Interlocked.Decrement(ref cur);
        }, new OffsetTracker(), (p, o) => { lock (commits) commits.Add(o); }, time);

        await d.SubmitAsync(0, 0, Frame() with { Seq = 0 }, default);
        await d.SubmitAsync(0, 1, Frame() with { Seq = 1 }, default);
        var third = d.SubmitAsync(0, 2, Frame() with { Seq = 2 }, default).AsTask();   // must block: 2 slots busy
        await Task.Delay(100);
        Assert.False(third.IsCompleted);

        release[1].SetResult();                 // later frame finishes first -> nothing committable (offset 0 pending)
        await third;                            // slot freed
        await Task.Delay(50);
        lock (commits) Assert.Empty(commits);
        release[0].SetResult(); release[2].SetResult();
        await d.DrainAsync(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 100; i++) { lock (commits) if (commits.Count > 0 && commits.Last() == 3L) break; await Task.Delay(30); }
        Assert.Equal(2, max);
        lock (commits) Assert.Equal(3L, commits.Last());
    }
}

public class ConflationTests
{
    [Fact] public async Task Only_newest_frame_per_camera_runs_superseded_are_skipped_and_offsets_stay_contiguous()
    {
        var time = new FakeTime();
        var gate = new TaskCompletionSource();
        var handled = new List<long>(); var skips = new List<string>(); var commits = new List<long>();
        var d = new WorkDispatcher<FrameMsg>(1, 0, f => f.Ts, async (f, ct) =>
        {
            lock (handled) handled.Add(f.Seq);
            if (f.Seq == 0) await gate.Task;      // occupies the only slot
        }, new OffsetTracker(), (p, o) => { lock (commits) commits.Add(o); }, time, r => { lock (skips) skips.Add(r); },
            keyOf: f => f.CameraId);

        await d.SubmitAsync(0, 0, Frame() with { Seq = 0 }, default);               // running, blocks
        await d.SubmitAsync(0, 1, Frame() with { Seq = 1 }, default);               // waits
        await d.SubmitAsync(0, 2, Frame() with { Seq = 2 }, default);               // supersedes 1
        await d.SubmitAsync(0, 3, Frame() with { Seq = 3 }, default);               // supersedes 2
        await d.SubmitAsync(0, 4, Frame(cam: "cam-002") with { Seq = 4 }, default); // other camera, own mailbox

        lock (skips) Assert.Equal(new[] { "superseded", "superseded" }, skips);
        lock (commits) Assert.Empty(commits);                                       // offset 0 still running
        gate.SetResult();
        await Task.Delay(100);
        await d.DrainAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        lock (handled) Assert.Equal(new long[] { 0, 3, 4 }, handled.OrderBy(x => x).ToArray());  // newest of each camera always ran
        lock (commits) Assert.Equal(5L, commits.Last());                             // everything done -> contiguous commit
    }

    [Fact] public async Task Same_camera_never_runs_concurrently_and_stale_waiting_frame_is_skipped()
    {
        var time = new FakeTime();
        var cur = 0; var max = 0; var skips = new List<string>();
        var gate = new TaskCompletionSource();
        var d = new WorkDispatcher<FrameMsg>(8, 1500, f => f.Ts, async (f, ct) =>
        {
            var c = Interlocked.Increment(ref cur); lock (skips) max = Math.Max(max, c);
            await gate.Task;
            Interlocked.Decrement(ref cur);
        }, new OffsetTracker(), (p, o) => { }, time, r => { lock (skips) skips.Add(r); }, keyOf: f => f.CameraId);
        var now = time.Now.ToUnixTimeMilliseconds();
        await d.SubmitAsync(0, 0, Frame(now) , default);
        await d.SubmitAsync(0, 1, Frame(now - 5000), default);      // waits behind busy camera (and is superseded/stale)
        gate.SetResult();
        await Task.Delay(100);
        Assert.Equal(1, max);
        lock (skips) Assert.Single(skips);
    }
}

public class SnapshotTests
{
    static StepResult Plate() => new(StepStatus.Ok, Plates: new() { new PlateDet("AAA", 0.9, new[] { 1.0, 2, 3, 4 }) });

    static (FramePipeline, FakeSink, FakeSnaps) Setup(int failFirst)
    {
        var tools = new FakeTools(); tools.Outputs[Tools.ReadPlate] = Plate();
        var snaps = new FakeSnaps { FailFirst = failFirst };
        var (pipe, store, _, sink) = Pipeline(tools, snaps);
        store.ApplyPlan("p", P("p", 1, Step("s1", Tools.ReadPlate)));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));
        return (pipe, sink, snaps);
    }

    [Fact] public async Task Recognition_that_passes_cooldown_puts_snapshot_and_carries_its_key()
    {
        var (pipe, sink, snaps) = Setup(0);
        var f = Frame();
        await pipe.ProcessAsync(f, CancellationToken.None);
        var put = snaps.Puts.Single();
        Assert.Equal(PipelineExt.Jpeg.Length, put.Len);
        var rec = sink.Of("recognitions").Single();
        Assert.Equal(put.Key, rec.GetProperty("snapshot_key").GetString());
        Assert.StartsWith("plate/" + DateTimeOffset.FromUnixTimeMilliseconds(f.Ts).ToString("yyyyMMdd") + "/" + rec.GetProperty("recognition_id").GetString(), put.Key);
        await pipe.ProcessAsync(Frame(), CancellationToken.None);       // suppressed by cooldown -> no second PUT
        Assert.Single(snaps.Puts);
    }

    [Fact] public async Task One_retry_then_success_keeps_key()
    {
        var (pipe, sink, snaps) = Setup(1);
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        Assert.Equal(2, snaps.Attempts);
        Assert.NotNull(sink.Of("recognitions").Single().GetProperty("snapshot_key").GetString());
    }

    [Fact] public async Task Failed_put_publishes_recognition_with_null_key_and_counts_failure()
    {
        var (pipe, sink, snaps) = Setup(5);
        var before = Telemetry.SnapshotFailures.Value;
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        Assert.Equal(2, snaps.Attempts);                                  // original + exactly one retry
        Assert.Equal(JsonValueKind.Null, sink.Of("recognitions").Single().GetProperty("snapshot_key").ValueKind);
        Assert.Equal(1, Telemetry.SnapshotFailures.Value - before);
        Assert.Single(sink.Of("detections"));
    }

    [Fact] public async Task Heavy_job_message_carries_the_jpeg_as_value_and_job_json_in_header()
    {
        var (pipe, store, _, sink) = Pipeline();
        store.ApplyPlan("p", P("p", 1, Step("s1", Tools.DetectObjects), Step("s2", Tools.ReadText, mode: "queue")));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));
        await pipe.ProcessAsync(Frame(), CancellationToken.None);
        var raw = sink.Raw.Single(m => m.Topic == "jobs.heavy");
        Assert.Equal(PipelineExt.Jpeg, raw.Value);
        Assert.True(raw.Headers!.ContainsKey("job"));
    }

    [Fact] public void Pooled_deserializer_copies_value_once_and_dispose_is_idempotent()
    {
        var img = PooledImage.Copy(PipelineExt.Jpeg);
        Assert.Equal(PipelineExt.Jpeg, img.Memory.ToArray());
        img.Dispose(); img.Dispose();
    }

    [Fact] public async Task Discarded_items_are_released_when_superseded_or_stale()
    {
        var time = new FakeTime(); var discarded = 0;
        var gate = new TaskCompletionSource();
        var d = new WorkDispatcher<FrameMsg>(1, 0, f => f.Ts, async (f, ct) => await gate.Task, new OffsetTracker(), (p, o) => { }, time,
            keyOf: f => f.CameraId, onDiscard: _ => Interlocked.Increment(ref discarded));
        await d.SubmitAsync(0, 0, Frame(), default);
        await d.SubmitAsync(0, 1, Frame(), default);
        await d.SubmitAsync(0, 2, Frame(), default);       // supersedes 1
        Assert.Equal(1, discarded);
        gate.SetResult();
    }
}

public class CooldownTests
{
    static Recognition Rec(string type, string value, string cam = "cam-001") =>
        new(Guid.NewGuid().ToString(), type, cam, "f", 0, value, null, 0.9, new[] { 1.0, 2, 3, 4 }, "k", "p");

    [Fact] public void Same_sighting_is_suppressed_until_cooldown_expires_unknown_faces_use_short_per_camera_window()
    {
        var time = new FakeTime();
        var cd = new RecognitionCooldown(30, 10, time);
        var s0 = Telemetry.RecognitionsSuppressed.WithLabels("plate").Value;
        Assert.True(cd.Allow(Rec("plate", "ABC")));
        Assert.False(cd.Allow(Rec("plate", "ABC")));
        Assert.True(cd.Allow(Rec("plate", "XYZ")));                 // other value
        Assert.True(cd.Allow(Rec("plate", "ABC", "cam-002")));      // other camera
        Assert.Equal(1, Telemetry.RecognitionsSuppressed.WithLabels("plate").Value - s0);

        Assert.True(cd.Allow(Rec("face", "unknown")));
        Assert.False(cd.Allow(Rec("face", "unknown")));
        Assert.True(cd.Allow(Rec("face", "Dana")));                 // named face has its own 30 s key
        time.Now = time.Now.AddSeconds(11);
        Assert.True(cd.Allow(Rec("face", "unknown")));              // 10 s elapsed
        Assert.False(cd.Allow(Rec("face", "Dana")));
        Assert.False(cd.Allow(Rec("plate", "ABC")));
        time.Now = time.Now.AddSeconds(31);
        Assert.True(cd.Allow(Rec("plate", "ABC")));
    }

    [Fact] public void Expired_keys_are_evicted()
    {
        var time = new FakeTime();
        var cd = new RecognitionCooldown(30, 10, time);
        for (var i = 0; i < 500; i++) cd.Allow(Rec("plate", "P" + i));
        Assert.Equal(500, cd.Tracked);
        time.Now = time.Now.AddSeconds(60);
        cd.Allow(Rec("plate", "new"));
        Assert.Equal(1, cd.Tracked);
    }

    [Fact] public async Task Pipeline_suppresses_repeated_recognitions_but_keeps_all_detections()
    {
        var tools = new FakeTools();
        tools.Outputs[Tools.ReadPlate] = new StepResult(StepStatus.Ok, Plates: new() { new PlateDet("AAA", 0.9, new[] { 1.0, 2, 3, 4 }) });
        var (pipe, store, _, sink) = Pipeline(tools);
        store.ApplyPlan("p", P("p", 1, Step("s1", Tools.ReadPlate)));
        store.ApplyCamera("cam-001", new CameraConfig("cam-001", true, "p"));
        for (var i = 0; i < 5; i++) await pipe.ProcessAsync(Frame(), CancellationToken.None);
        Assert.Equal(5, sink.Of("detections").Count);
        Assert.Single(sink.Of("recognitions"));
    }
}

public class StatsTests
{
    [Fact] public void Window_percentiles_and_reset()
    {
        var s = new StepStats();
        for (var i = 1; i <= 100; i++) s.Processed("p", "s1", "detect_objects", i);
        s.Skipped("p", "s1", "detect_objects"); s.Error("p", "s1", "detect_objects");
        var m = s.Flush("ex-1", 5, 1).Single();
        Assert.Equal((100, 1, 1), (m.Processed, m.Skipped, m.Errors));
        Assert.Equal(50, m.P50Ms); Assert.Equal(95, m.P95Ms);
        Assert.Empty(s.Flush("ex-1", 5, 2));
    }
}


public class TierTests
{
    static async Task<(List<string> Order, TaskCompletionSource Gate, WorkDispatcher<FrameMsg> D)> Setup(Func<string, bool> watched, int inflight = 1)
    {
        var order = new List<string>();
        var gate = new TaskCompletionSource();
        var d = new WorkDispatcher<FrameMsg>(inflight, 0, f => f.Ts, async (f, ct) =>
        {
            lock (order) order.Add(f.CameraId);
            if (f.Seq == 0) await gate.Task;
        }, new OffsetTracker(), (p, o) => { }, new FakeTime(), keyOf: f => f.CameraId, isWatched: watched);
        await d.SubmitAsync(0, 0, Frame(cam: "blocker") with { Seq = 0 }, default); // occupies the only slot
        return (order, gate, d);
    }

    static async Task Release(TaskCompletionSource gate, WorkDispatcher<FrameMsg> d)
    {
        gate.SetResult();
        await Task.Delay(100);
        await d.DrainAsync(TimeSpan.FromSeconds(2));
    }

    [Fact] public async Task Watched_cameras_run_before_background_ones_round_robin_within_tier()
    {
        var watched = new HashSet<string> { "w1", "w2" };
        var (order, gate, d) = await Setup(watched.Contains);
        long o = 1;
        foreach (var c in new[] { "b1", "b2", "w1", "w2" }) await d.SubmitAsync(0, o++, Frame(cam: c) with { Seq = 1 }, default);
        await Release(gate, d);
        lock (order) Assert.Equal(new[] { "blocker", "w1", "w2", "b1", "b2" }, order);
    }

    [Fact] public async Task Every_fourth_dispatch_goes_to_background_when_it_has_work()
    {
        var (order, gate, d) = await Setup(c => c.StartsWith("w"));
        long o = 1;
        foreach (var c in new[] { "b1", "b2", "w1", "w2", "w3", "w4", "w5", "w6" }) await d.SubmitAsync(0, o++, Frame(cam: c) with { Seq = 1 }, default);
        await Release(gate, d);
        // dispatch #1 = blocker (tier 2, ran before anything else was queued); then 3 watched, background, ...
        lock (order) Assert.Equal(new[] { "blocker", "w1", "w2", "b1", "w3", "w4", "w5", "b2", "w6" }, order);
    }

    [Fact] public async Task Starvation_slot_goes_to_watched_when_background_is_empty()
    {
        var (order, gate, d) = await Setup(_ => true);
        long o = 1;
        foreach (var c in new[] { "w1", "w2", "w3", "w4", "w5" }) await d.SubmitAsync(0, o++, Frame(cam: c) with { Seq = 1 }, default);
        await Release(gate, d);
        lock (order) Assert.Equal(new[] { "blocker", "w1", "w2", "w3", "w4", "w5" }, order);
    }

    [Fact] public async Task Camera_that_becomes_watched_is_promoted_at_dispatch_time()
    {
        var watched = new HashSet<string>();
        var (order, gate, d) = await Setup(c => { lock (watched) return watched.Contains(c); });
        long o = 1;
        foreach (var c in new[] { "b1", "b2", "b3" }) await d.SubmitAsync(0, o++, Frame(cam: c) with { Seq = 1 }, default);
        lock (watched) watched.Add("b1");                  // already queued as background; head, so promoted when popped
        await d.SubmitAsync(0, o++, Frame(cam: "b4") with { Seq = 1 }, default);
        await Release(gate, d);
        lock (order) Assert.Equal("b1", order[1]);
    }

    [Fact] public async Task Camera_that_stops_being_watched_is_demoted()
    {
        var watched = new HashSet<string> { "w1", "w2" };
        var (order, gate, d) = await Setup(c => { lock (watched) return watched.Contains(c); });
        long o = 1;
        foreach (var c in new[] { "w1", "b1", "w2" }) await d.SubmitAsync(0, o++, Frame(cam: c) with { Seq = 1 }, default);
        lock (watched) watched.Remove("w1");
        await Release(gate, d);
        lock (order) Assert.Equal(new[] { "blocker", "w2", "b1", "w1" }, order);
    }

    [Fact] public void Demand_entries_older_than_90s_count_as_zero_and_only_live_viewers_count()
    {
        var time = new FakeTime();
        var now = time.Now.ToUnixTimeMilliseconds();
        var s = new DemandStore(time);
        s.ApplyJson("a", System.Text.Encoding.UTF8.GetBytes($"{{\"camera_id\":\"a\",\"live_viewers\":2,\"frame_viewers\":0,\"ts\":{now}}}"));
        s.ApplyJson("f", System.Text.Encoding.UTF8.GetBytes($"{{\"camera_id\":\"f\",\"live_viewers\":0,\"frame_viewers\":3,\"ts\":{now}}}"));
        Assert.True(s.IsWatched("a"));
        Assert.False(s.IsWatched("f"));
        Assert.False(s.IsWatched("nobody"));
        time.Now = time.Now.AddSeconds(89);
        Assert.True(s.IsWatched("a"));
        time.Now = time.Now.AddSeconds(2);
        Assert.False(s.IsWatched("a"));
        s.ApplyJson("a", System.Text.Encoding.UTF8.GetBytes($"{{\"camera_id\":\"a\",\"live_viewers\":1,\"frame_viewers\":0,\"ts\":{time.Now.ToUnixTimeMilliseconds()}}}"));
        Assert.True(s.IsWatched("a"));
        s.ApplyJson("a", null);
        Assert.False(s.IsWatched("a"));
    }
}
