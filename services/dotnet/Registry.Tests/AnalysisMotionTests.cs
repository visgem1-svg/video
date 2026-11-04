using System.Text.Json;
using Registry;
using Registry.Assignment;
using Xunit;

namespace Registry.Tests;

/// <summary>Contract 9.9: analysis_stream_url, motion, on-demand pulled paths, duplicates, heartbeat payload, backward compatibility.</summary>
public class AnalysisMotionTests
{
    private readonly FakeStore _store = new();
    private readonly FakePublisher _pub = new();
    private readonly FakeMediaMtx _mtx = new();
    private readonly CameraService _svc;
    public AnalysisMotionTests() { _svc = new CameraService(_store, _pub, new FakeClock(), _mtx); }

    private static CameraInput In(string id, string? stream = "rtsp://10.0.0.5/main", string? analysis = null, MotionInput? motion = null) =>
        new(id, "Gate", null, 3, true, null, null, stream, analysis, motion);

    // ---- validation -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://h/x.m3u8")]
    [InlineData("publisher")]
    [InlineData("not a url")]
    [InlineData("rtmp://h/x")]
    public async Task Invalid_analysis_stream_url_is_rejected_with_field_name(string url)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _svc.CreateAsync(In("gate-1", analysis: url), default));
        Assert.Contains("analysis_stream_url", ex.Message);
    }

    [Theory]
    [InlineData("extreme", 5, "motion.sensitivity")]
    [InlineData("low", 0.5, "motion.heartbeat_s")]
    [InlineData("low", 61, "motion.heartbeat_s")]
    [InlineData("low", double.NaN, "motion.heartbeat_s")]
    public async Task Invalid_motion_is_rejected_with_field_name(string sens, double hb, string field)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _svc.CreateAsync(In("gate-1", motion: new MotionInput(true, sens, hb)), default));
        Assert.Contains(field, ex.Message);
        Assert.Empty(_store.Cameras);
    }

    [Fact]
    public async Task Both_motion_errors_are_reported_together()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _svc.CreateAsync(In("gate-1", motion: new MotionInput(true, "x", 0)), default));
        Assert.Contains("motion.sensitivity", ex.Message);
        Assert.Contains("motion.heartbeat_s", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(2.5)]
    public async Task Motion_bounds_are_inclusive(double hb) =>
        Assert.Equal(hb, (await _svc.CreateAsync(In("gate-1", motion: new MotionInput(true, "HIGH", hb)), default))!.Motion.HeartbeatS);

    [Fact]
    public async Task Camera_id_ending_with_analysis_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _svc.CreateAsync(In("gate-analysis"), default));
        Assert.Contains("camera_id", ex.Message);
    }

    // ---- defaults, derived field, backward compat ----------------------------------------------------------------

    [Fact]
    public async Task Defaults_when_fields_are_missing_and_json_shape()
    {
        var c = (await _svc.CreateAsync(In("gate-1"), default))!;
        Assert.Equal(new MotionConfig(false, "medium", 5), c.Motion);
        Assert.Null(c.AnalysisStreamUrl);
        Assert.Null(c.AnalysisSourceUrl);
        var j = JsonDocument.Parse(JsonSerializer.Serialize(c, Json.Options)).RootElement;
        Assert.Equal(JsonValueKind.Null, j.GetProperty("analysis_stream_url").ValueKind);
        Assert.Equal(JsonValueKind.Null, j.GetProperty("analysis_source_url").ValueKind);
        Assert.False(j.GetProperty("motion").GetProperty("enabled").GetBoolean());
        Assert.Equal("medium", j.GetProperty("motion").GetProperty("sensitivity").GetString());
        Assert.Equal(5, j.GetProperty("motion").GetProperty("heartbeat_s").GetDouble());
    }

    [Fact]
    public async Task Analysis_source_url_is_derived_and_clients_cannot_set_it()
    {
        var c = (await _svc.CreateAsync(In("gate-1", analysis: " rtsp://10.0.0.5/sub "), default))!;
        Assert.Equal("rtsp://10.0.0.5/sub", c.AnalysisStreamUrl);
        Assert.Equal("rtsp://mediamtx:8554/gate-1-analysis", c.AnalysisSourceUrl);
        var json = JsonSerializer.Serialize(c, Json.Options);
        Assert.Contains("\"analysis_source_url\":\"rtsp://mediamtx:8554/gate-1-analysis\"", json);
        // round trip through the wire format keeps it derived (no stored copy)
        Assert.Equal("rtsp://mediamtx:8554/gate-1-analysis", JsonSerializer.Deserialize<CameraConfig>(json, Json.Options)!.AnalysisSourceUrl);
    }

    [Fact]
    public async Task Existing_camera_json_without_new_fields_deserializes_to_defaults()
    {
        var old = JsonSerializer.Deserialize<CameraConfig>(
            """{"camera_id":"cam-001","name":"n","source_url":"rtsp://mediamtx:8554/cam-001","analysis_fps":3,"enabled":true,"plan_id":null,"tags":[],"updated_at":1,"stream_url":"publisher"}""", Json.Options)!;
        Assert.Equal(MotionConfig.Default, old.Motion);
        Assert.Null(old.AnalysisSourceUrl);
        var input = JsonSerializer.Deserialize<CameraInput>("""{"camera_id":"gate-1","stream_url":"rtsp://h/x"}""", Json.Options)!;
        var c = await _svc.CreateAsync(input, default);
        Assert.Equal(MotionConfig.Default, c!.Motion);
        Assert.Equal(new[] { "ADD gate-1 rtsp://h/x" }, _mtx.Calls);   // no analysis path for an old-style camera
    }

    [Fact]
    public async Task Motion_is_stored_published_and_partial_input_uses_defaults()
    {
        var c = (await _svc.CreateAsync(In("gate-1", motion: new MotionInput(true, "low", null)), default))!;
        Assert.Equal(new MotionConfig(true, "low", 5), c.Motion);
        Assert.Equal(c.Motion, _store.Cameras["gate-1"].Motion);
        Assert.Equal(c.Motion, _pub.Messages[^1].Value!.Motion);
    }

    [Fact]
    public async Task Update_keeps_analysis_and_motion_when_omitted_merges_partial_motion_and_clears_with_empty_string()
    {
        await _svc.CreateAsync(In("gate-1", analysis: "rtsp://10.0.0.5/sub", motion: new MotionInput(true, "high", 10)), default);
        var kept = (await _svc.UpdateAsync("gate-1", In("gate-1", null), default))!;
        Assert.Equal("rtsp://10.0.0.5/sub", kept.AnalysisStreamUrl);
        Assert.Equal(new MotionConfig(true, "high", 10), kept.Motion);
        var merged = (await _svc.UpdateAsync("gate-1", In("gate-1", null, motion: new MotionInput(null, "low", null)), default))!;
        Assert.Equal(new MotionConfig(true, "low", 10), merged.Motion);
        var cleared = (await _svc.UpdateAsync("gate-1", In("gate-1", null, analysis: ""), default))!;
        Assert.Null(cleared.AnalysisStreamUrl);
        Assert.Null(cleared.AnalysisSourceUrl);
    }

    [Fact]
    public async Task Motion_change_is_published_without_touching_mediamtx()
    {
        await _svc.CreateAsync(In("gate-1"), default);
        _mtx.Calls.Clear(); _pub.Messages.Clear();
        await _svc.UpdateAsync("gate-1", In("gate-1", null, motion: new MotionInput(true, null, null)), default);
        Assert.Empty(_mtx.Calls);
        Assert.True(_pub.Messages.Last(m => m.Key == "gate-1").Value!.Motion.Enabled);
    }

    // ---- provisioning -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_provisions_main_and_analysis_paths()
    {
        var c = (await _svc.CreateAsync(In("gate-1", analysis: "rtsp://u:p@10.0.0.5/sub"), default))!;
        Assert.Equal(new[] { "ADD gate-1 rtsp://10.0.0.5/main", "ADD gate-1-analysis rtsp://u:p@10.0.0.5/sub" }, _mtx.Calls);
        Assert.Null(c.ProvisionError);
    }

    [Fact]
    public async Task Publisher_camera_without_analysis_url_is_never_provisioned_and_with_one_only_the_analysis_path()
    {
        await _svc.CreateAsync(In("cam-001", "publisher"), default);
        Assert.Empty(_mtx.Calls);
        await _svc.CreateAsync(In("cam-002", "publisher", "rtsp://h/sub"), default);
        Assert.Equal(new[] { "ADD cam-002-analysis rtsp://h/sub" }, _mtx.Calls);
    }

    [Fact]
    public async Task Update_adds_patches_and_removes_the_analysis_path_only_when_it_changes()
    {
        await _svc.CreateAsync(In("gate-1"), default);
        _mtx.Calls.Clear();
        await _svc.UpdateAsync("gate-1", In("gate-1", null, "rtsp://h/sub1"), default);
        Assert.Equal(new[] { "ADD gate-1-analysis rtsp://h/sub1" }, _mtx.Calls);
        _mtx.Calls.Clear();
        await _svc.UpdateAsync("gate-1", In("gate-1", null, "rtsp://h/sub1"), default);
        Assert.Empty(_mtx.Calls);
        await _svc.UpdateAsync("gate-1", In("gate-1", null, "rtsp://h/sub2"), default);
        Assert.Equal(new[] { "PATCH gate-1-analysis rtsp://h/sub2" }, _mtx.Calls);
        _mtx.Calls.Clear();
        await _svc.UpdateAsync("gate-1", In("gate-1", null, ""), default);
        Assert.Equal(new[] { "DELETE gate-1-analysis" }, _mtx.Calls);
        Assert.DoesNotContain("gate-1-analysis", _mtx.Paths.Keys);
        Assert.Contains("gate-1", _mtx.Paths.Keys);
    }

    [Fact]
    public async Task Delete_removes_both_paths_and_a_main_only_camera_stays_a_single_call()
    {
        await _svc.CreateAsync(In("gate-1", analysis: "rtsp://h/sub"), default);
        await _svc.CreateAsync(In("gate-2"), default);
        _mtx.Calls.Clear();
        Assert.True(await _svc.DeleteAsync("gate-1", default));
        Assert.Equal(new[] { "DELETE gate-1", "DELETE gate-1-analysis" }, _mtx.Calls);
        _mtx.Calls.Clear();
        Assert.True(await _svc.DeleteAsync("gate-2", default));
        Assert.Equal(new[] { "DELETE gate-2" }, _mtx.Calls);
        Assert.Empty(_mtx.Paths);
    }

    [Fact]
    public async Task Failure_on_one_path_does_not_skip_the_other_and_never_leaks_credentials()
    {
        var failing = new FailingMtx();
        var svc = new CameraService(_store, _pub, new FakeClock(), failing);
        var c = (await svc.CreateAsync(In("gate-1", "rtsp://admin:s3cret@10.0.0.5/main", "rtsp://admin:s3cret@10.0.0.5/sub"), default))!;
        Assert.Equal(2, failing.Attempts);
        Assert.NotNull(c.ProvisionError);
        Assert.DoesNotContain("s3cret", c.ProvisionError);
        Assert.DoesNotContain("s3cret", _store.Cameras["gate-1"].ProvisionError);
        Assert.Equal("a://***@h/x", CameraService.Redact("a://user:pw@h/x"));
    }

    private sealed class FailingMtx : IMediaMtxClient
    {
        public int Attempts;
        public Task<IReadOnlyDictionary<string, MediaMtxPath>> ListPathsAsync(CancellationToken ct) => throw new HttpRequestException("rtsp://admin:s3cret@x/y refused");
        public Task<int> ReaderCountAsync(string name, CancellationToken ct) => Task.FromResult(0);
        public Task AddAsync(string name, string source, CancellationToken ct) { Attempts++; throw new HttpRequestException($"mediamtx 400: bad source {source}"); }
        public Task PatchAsync(string name, string source, CancellationToken ct) => AddAsync(name, source, ct);
        public Task DeleteAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Failed_analysis_provisioning_is_retried_by_reconcile()
    {
        _mtx.Fail = true;
        await _svc.CreateAsync(In("gate-1", analysis: "rtsp://h/sub"), default);
        _mtx.Fail = false;
        await _svc.ReconcileAsync(default);
        Assert.Equal("rtsp://h/sub", _mtx.Paths["gate-1-analysis"]);
        Assert.Equal("rtsp://10.0.0.5/main", _mtx.Paths["gate-1"]);
        Assert.Null(_store.Cameras["gate-1"].ProvisionError);
    }

    // ---- reconcile: existing (pre-9.9) paths -------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_removes_leftover_analysis_path_but_ignores_unknown_ones()
    {
        await _svc.CreateAsync(In("gate-1"), default);
        _mtx.Paths["gate-1-analysis"] = "rtsp://stale"; _mtx.OnDemand.Add("gate-1-analysis");
        _mtx.Paths["other-analysis"] = "rtsp://x";
        _mtx.Calls.Clear();
        await _svc.ReconcileAsync(default);
        Assert.Equal(new[] { "LIST", "DELETE gate-1-analysis" }, _mtx.Calls);
        Assert.Contains("other-analysis", _mtx.Paths.Keys);
    }

    [Fact]
    public async Task Reconcile_upgrades_an_idle_always_on_path_to_on_demand_but_leaves_a_busy_one_alone()
    {
        await _svc.CreateAsync(In("gate-1", "rtsp://a/1"), default);
        await _svc.CreateAsync(In("gate-2", "rtsp://a/2"), default);
        _mtx.OnDemand.Clear();                       // simulate paths provisioned before 9.9 (sourceOnDemand=false)
        _mtx.Readers["gate-2"] = 1;
        _mtx.Calls.Clear();
        await _svc.ReconcileAsync(default);
        Assert.Equal(new[] { "LIST", "READERS gate-1", "PATCH gate-1 rtsp://a/1", "READERS gate-2" }, _mtx.Calls);
        _mtx.Calls.Clear();
        await _svc.ReconcileAsync(default);          // gate-2 stays pending, gate-1 is now stable (no extra calls)
        Assert.Equal(new[] { "LIST", "READERS gate-2" }, _mtx.Calls);
        // a real change of a busy path applies the on-demand settings at once
        await _svc.UpdateAsync("gate-2", In("gate-2", "rtsp://a/2b"), default);
        Assert.Contains("gate-2", _mtx.OnDemand);
    }

    [Fact]
    public async Task Reconcile_is_quiet_when_everything_matches_and_publishers_cost_nothing()
    {
        await _svc.CreateAsync(In("gate-1", analysis: "rtsp://h/sub"), default);
        await _svc.CreateAsync(In("cam-001", "publisher"), default);
        _pub.Messages.Clear(); _mtx.Calls.Clear();
        await _svc.ReconcileAsync(default);
        Assert.Equal(new[] { "LIST" }, _mtx.Calls);
        Assert.Empty(_pub.Messages);
    }

    // ---- duplicates --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Main_and_sub_of_the_same_camera_are_not_duplicates_nor_sub_equal_to_another_cameras_main()
    {
        await _svc.CreateAsync(In("cam-a1", "rtsp://h/main", "rtsp://h/sub"), default);
        await _svc.CreateAsync(In("cam-b1", "rtsp://h/sub"), default);   // its MAIN equals a1's sub: different roles
        Assert.All(await _svc.ListAsync(default), c => Assert.Empty(c.DuplicateOf));
    }

    [Fact]
    public async Task Same_analysis_url_on_two_cameras_is_a_duplicate_and_the_other_is_republished()
    {
        await _svc.CreateAsync(In("cam-a1", "rtsp://h/m1", "rtsp://h/sub"), default);
        _pub.Messages.Clear();
        var b = (await _svc.CreateAsync(In("cam-b1", "rtsp://h/m2", "RTSP://h:554/sub/"), default))!;
        Assert.Equal(new[] { "cam-a1" }, b.DuplicateOf);
        Assert.Equal(new[] { "cam-b1" }, _pub.Messages.Last(m => m.Key == "cam-a1").Value!.DuplicateOf);
    }

    [Fact]
    public void Duplicate_union_is_sorted_and_excludes_self()
    {
        CameraConfig C(string id, string main, string? sub) => new(id, id, "x", 3, true, null, [], 1, main) { AnalysisStreamUrl = sub };
        var d = Duplicates.Compute([C("c3", "rtsp://h/m", null), C("c1", "rtsp://h/m", "rtsp://h/s"), C("c2", "rtsp://h/o", "rtsp://h/s")]);
        Assert.Equal(new[] { "c2", "c3" }, d["c1"]);
        Assert.Equal(new[] { "c1" }, d["c2"]);
        Assert.Equal(new[] { "c1" }, d["c3"]);
    }

    // ---- heartbeat ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_assigned_items_carry_motion_and_analysis_source_url()
    {
        await _svc.CreateAsync(In("gate-1", analysis: "rtsp://h/sub", motion: new MotionInput(true, "high", 8)), default);
        await _svc.CreateAsync(In("gate-2"), default);
        var assigned = await new AssignmentService(_store, new FakeClock(), 15).HeartbeatAsync("w1", 10, default);
        var j = JsonDocument.Parse(JsonSerializer.Serialize(new HeartbeatResponse(assigned, 15), Json.Options)).RootElement.GetProperty("assigned");
        var g1 = j.EnumerateArray().Single(x => x.GetProperty("camera_id").GetString() == "gate-1");
        Assert.Equal("rtsp://mediamtx:8554/gate-1-analysis", g1.GetProperty("analysis_source_url").GetString());
        Assert.Equal("high", g1.GetProperty("motion").GetProperty("sensitivity").GetString());
        Assert.True(g1.GetProperty("motion").GetProperty("enabled").GetBoolean());
        Assert.Equal(8, g1.GetProperty("motion").GetProperty("heartbeat_s").GetDouble());
        var g2 = j.EnumerateArray().Single(x => x.GetProperty("camera_id").GetString() == "gate-2");
        Assert.Equal(JsonValueKind.Null, g2.GetProperty("analysis_source_url").ValueKind);
        Assert.False(g2.GetProperty("motion").GetProperty("enabled").GetBoolean());
        Assert.Equal("rtsp://mediamtx:8554/gate-2", g2.GetProperty("source_url").GetString());   // unchanged main source
    }
}
