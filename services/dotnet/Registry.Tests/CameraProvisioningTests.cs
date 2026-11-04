using Registry;
using Xunit;

namespace Registry.Tests;

public sealed class FakeMediaMtx : IMediaMtxClient
{
    public readonly Dictionary<string, string?> Paths = new();
    /// <summary>Paths configured with the 9.9 on-demand pull settings (everything this fake adds/patches).</summary>
    public readonly HashSet<string> OnDemand = new();
    public readonly Dictionary<string, int> Readers = new();
    public readonly List<string> Calls = new();
    public bool Fail;

    private void Check() { if (Fail) throw new HttpRequestException("connection refused"); }
    public Task<IReadOnlyDictionary<string, MediaMtxPath>> ListPathsAsync(CancellationToken ct)
    {
        Calls.Add("LIST"); Check();
        return Task.FromResult<IReadOnlyDictionary<string, MediaMtxPath>>(Paths.ToDictionary(kv => kv.Key, kv => OnDemand.Contains(kv.Key)
            ? new MediaMtxPath(kv.Value, true, HttpMediaMtxClient.StartTimeout, HttpMediaMtxClient.CloseAfter)
            : new MediaMtxPath(kv.Value, false, "10s", "10s")));
    }
    public Task<int> ReaderCountAsync(string name, CancellationToken ct) { Calls.Add($"READERS {name}"); Check(); return Task.FromResult(Readers.GetValueOrDefault(name)); }
    public Task AddAsync(string name, string source, CancellationToken ct) { Calls.Add($"ADD {name} {source}"); Check(); Paths[name] = source; OnDemand.Add(name); return Task.CompletedTask; }
    public Task PatchAsync(string name, string source, CancellationToken ct) { Calls.Add($"PATCH {name} {source}"); Check(); Paths[name] = source; OnDemand.Add(name); return Task.CompletedTask; }
    public Task DeleteAsync(string name, CancellationToken ct) { Calls.Add($"DELETE {name}"); Check(); Paths.Remove(name); OnDemand.Remove(name); return Task.CompletedTask; }
}

public class CameraProvisioningTests
{
    private readonly FakeStore _store = new();
    private readonly FakePublisher _pub = new();
    private readonly FakeMediaMtx _mtx = new();
    private readonly CameraService _svc;
    public CameraProvisioningTests() { _svc = new CameraService(_store, _pub, new FakeClock(), _mtx); }

    private static CameraInput In(string id, string? stream, string name = "Gate") => new(id, name, null, 3, true, null, null, stream);

    [Fact]
    public async Task Create_provisions_path_and_sets_source_url()
    {
        var c = await _svc.CreateAsync(In("gate-1", "rtsp://10.0.0.5/live"), default);
        Assert.Equal("rtsp://mediamtx:8554/gate-1", c!.SourceUrl);
        Assert.Equal("rtsp://10.0.0.5/live", c.StreamUrl);
        Assert.Null(c.ProvisionError);
        Assert.Equal(new[] { "ADD gate-1 rtsp://10.0.0.5/live" }, _mtx.Calls);
        Assert.Equal("gate-1", _pub.Messages[^1].Key);
    }

    [Fact]
    public async Task Client_supplied_source_url_is_ignored()
    {
        var c = await _svc.CreateAsync(new CameraInput("gate-1", "x", "rtsp://evil/x", 3, true, null, null, "rtsp://10.0.0.5/live"), default);
        Assert.Equal("rtsp://mediamtx:8554/gate-1", c!.SourceUrl);
    }

    [Fact]
    public async Task Publisher_cameras_are_not_provisioned()
    {
        var c = await _svc.CreateAsync(In("cam-001", "publisher"), default);
        Assert.Empty(_mtx.Calls);
        Assert.Null(c!.ProvisionError);
    }

    [Fact]
    public async Task Update_of_stream_url_patches_and_unchanged_url_makes_no_call()
    {
        await _svc.CreateAsync(In("gate-1", "rtsp://a/1"), default);
        _mtx.Calls.Clear();
        await _svc.UpdateAsync("gate-1", In("gate-1", "rtsp://a/1", "Renamed"), default);
        Assert.Empty(_mtx.Calls);
        await _svc.UpdateAsync("gate-1", In("gate-1", "rtsp://b/2"), default);
        Assert.Equal(new[] { "PATCH gate-1 rtsp://b/2" }, _mtx.Calls);
    }

    [Fact]
    public async Task Update_without_stream_url_keeps_the_existing_one()
    {
        await _svc.CreateAsync(In("gate-1", "rtsp://a/1"), default);
        var c = await _svc.UpdateAsync("gate-1", new CameraInput(null, "n", null, 3, true, "plan-9", null, null), default);
        Assert.Equal("rtsp://a/1", c!.StreamUrl);
        Assert.Equal("plan-9", c.PlanId);
    }

    [Fact]
    public async Task Switching_between_publisher_and_url_adds_or_deletes_the_path()
    {
        await _svc.CreateAsync(In("gate-1", "rtsp://a/1"), default);
        _mtx.Calls.Clear();
        await _svc.UpdateAsync("gate-1", In("gate-1", "publisher"), default);
        Assert.Equal(new[] { "DELETE gate-1" }, _mtx.Calls);
        _mtx.Calls.Clear();
        await _svc.UpdateAsync("gate-1", In("gate-1", "rtsp://c/3"), default);
        Assert.Equal(new[] { "ADD gate-1 rtsp://c/3" }, _mtx.Calls);
    }

    [Fact]
    public async Task Delete_removes_path_and_publishes_tombstone()
    {
        await _svc.CreateAsync(In("gate-1", "rtsp://a/1"), default);
        Assert.True(await _svc.DeleteAsync("gate-1", default));
        Assert.DoesNotContain("gate-1", _mtx.Paths.Keys);
        Assert.Null(_pub.Messages[^1].Value);
    }

    [Fact]
    public async Task Provisioning_failure_does_not_block_write_and_is_reported()
    {
        _mtx.Fail = true;
        var c = await _svc.CreateAsync(In("gate-1", "rtsp://a/1"), default);
        Assert.NotNull(c);
        Assert.Contains("mediamtx provisioning failed", c!.ProvisionError);
        Assert.NotNull(_store.Cameras["gate-1"].ProvisionError);
        Assert.NotNull(_pub.Messages[^1].Value!.ProvisionError);
        Assert.True(await _svc.DeleteAsync("gate-1", default));
    }

    [Fact]
    public async Task Reconcile_retries_failed_provisioning_and_clears_error()
    {
        _mtx.Fail = true;
        await _svc.CreateAsync(In("gate-1", "rtsp://a/1"), default);
        _mtx.Fail = false;
        _pub.Messages.Clear();
        await _svc.ReconcileAsync(default);
        Assert.Equal("rtsp://a/1", _mtx.Paths["gate-1"]);
        Assert.Null(_store.Cameras["gate-1"].ProvisionError);
        Assert.Null(Assert.Single(_pub.Messages).Value!.ProvisionError);
        _pub.Messages.Clear();
        await _svc.ReconcileAsync(default);
        Assert.Empty(_pub.Messages);
    }

    [Fact]
    public async Task Reconcile_restores_paths_after_mediamtx_restart_and_fixes_drift_but_skips_publishers()
    {
        await _svc.CreateAsync(In("gate-1", "rtsp://a/1"), default);
        await _svc.CreateAsync(In("gate-2", "rtsp://a/2"), default);
        await _svc.CreateAsync(In("cam-001", "publisher"), default);
        _mtx.Paths.Clear();
        _mtx.Paths["gate-2"] = "rtsp://old";
        _mtx.Calls.Clear();
        await _svc.ReconcileAsync(default);
        Assert.Equal(new[] { "LIST", "ADD gate-1 rtsp://a/1", "PATCH gate-2 rtsp://a/2" }, _mtx.Calls);
    }

    [Fact]
    public async Task Reconcile_marks_cameras_when_mediamtx_unreachable()
    {
        await _svc.CreateAsync(In("gate-1", "rtsp://a/1"), default);
        _mtx.Fail = true;
        await _svc.ReconcileAsync(default);
        Assert.Contains("unreachable", _store.Cameras["gate-1"].ProvisionError);
    }

    [Theory]
    [InlineData("Cam-001")]
    [InlineData("ab")]
    [InlineData("-abc")]
    [InlineData("cam_001")]
    [InlineData("cam 001")]
    public async Task Invalid_camera_ids_are_rejected_with_clear_message(string id)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _svc.CreateAsync(In(id, "publisher"), default));
        Assert.Contains("camera_id", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://x/y")]
    [InlineData("not a url")]
    public async Task Invalid_stream_urls_are_rejected(string? url)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _svc.CreateAsync(In("gate-1", url), default));
    }

    [Theory]
    [InlineData("rtsps://h/x")]
    [InlineData("rtmp://h/live/x")]
    [InlineData("https://h/stream.m3u8")]
    [InlineData("srt://h:9000?streamid=x")]
    public async Task Supported_schemes_are_accepted(string url)
    {
        Assert.NotNull(await _svc.CreateAsync(In("gate-1", url), default));
    }

    [Fact]
    public async Task Seed_creates_20_publisher_cameras_once_and_republishes_all()
    {
        await _svc.SeedAndRepublishAsync(20, default);
        Assert.Equal(20, _store.Cameras.Count);
        var c = _store.Cameras["cam-007"];
        Assert.Equal("rtsp://mediamtx:8554/cam-007", c.SourceUrl);
        Assert.Equal("publisher", c.StreamUrl);
        Assert.Equal("Camera 007", c.Name);
        Assert.Equal(20, _pub.Messages.Count);
        Assert.DoesNotContain(_mtx.Calls, x => x.StartsWith("ADD"));
        await _svc.SeedAndRepublishAsync(20, default);
        Assert.Equal(40, _pub.Messages.Count);
    }
}

public class DuplicateTests
{
    [Theory]
    [InlineData("RTSP://Cam.Example.COM:554/Live/", "rtsp://cam.example.com/Live")]
    [InlineData("rtsp://admin:secret@cam.example.com/Live", "rtsp://cam.example.com/Live")]
    [InlineData("rtsp://cam.example.com:8554/Live", "rtsp://cam.example.com:8554/Live")]
    [InlineData("rtmp://h:1935/app/key/", "rtmp://h/app/key")]
    [InlineData("http://h:80/a.m3u8", "http://h/a.m3u8")]
    [InlineData("HTTPS://H:443/a.m3u8", "https://h/a.m3u8")]
    [InlineData("rtsp://h", "rtsp://h")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, Duplicates.Normalize(input));

    [Theory]
    [InlineData("publisher")]
    [InlineData("")]
    [InlineData("garbage")]
    public void Normalize_ignores_publisher_and_invalid(string input) => Assert.Null(Duplicates.Normalize(input));

    private readonly FakeStore _store = new();
    private readonly FakePublisher _pub = new();
    private readonly CameraService _svc;
    public DuplicateTests() { _svc = new CameraService(_store, _pub, new FakeClock(), new FakeMediaMtx()); }
    private static CameraInput In(string id, string url) => new(id, id, null, 3, true, null, null, url);

    [Fact]
    public async Task Add_flags_both_cameras_and_republishes_the_existing_one()
    {
        await _svc.CreateAsync(In("cam-a1", "rtsp://h.example/live"), default);
        _pub.Messages.Clear();
        var b = await _svc.CreateAsync(In("cam-b1", "RTSP://admin:pw@H.example:554/live/"), default);
        Assert.Equal(new[] { "cam-a1" }, b!.DuplicateOf);
        var a = _pub.Messages.Single(m => m.Key == "cam-a1").Value!;
        Assert.Equal(new[] { "cam-b1" }, a.DuplicateOf);
        Assert.Equal(new[] { "cam-b1" }, (await _svc.GetAsync("cam-a1", default))!.DuplicateOf);
    }

    [Fact]
    public async Task Three_way_group_and_removal_recompute()
    {
        foreach (var id in new[] { "cam-a1", "cam-b1", "cam-c1" }) await _svc.CreateAsync(In(id, "rtsp://h/x"), default);
        Assert.Equal(new[] { "cam-b1", "cam-c1" }, (await _svc.GetAsync("cam-a1", default))!.DuplicateOf);
        _pub.Messages.Clear();
        await _svc.DeleteAsync("cam-c1", default);
        Assert.Null(_pub.Messages.Single(m => m.Key == "cam-c1").Value);       // tombstone
        Assert.Equal(new[] { "cam-b1" }, _pub.Messages.Last(m => m.Key == "cam-a1").Value!.DuplicateOf);
        Assert.Equal(new[] { "cam-a1" }, _pub.Messages.Last(m => m.Key == "cam-b1").Value!.DuplicateOf);
        _pub.Messages.Clear();
        await _svc.DeleteAsync("cam-b1", default);
        Assert.Empty(_pub.Messages.Last(m => m.Key == "cam-a1").Value!.DuplicateOf);
    }

    [Fact]
    public async Task Update_to_and_from_duplicate_recomputes_and_publisher_is_never_duplicate()
    {
        await _svc.CreateAsync(In("cam-a1", "rtsp://h/x"), default);
        await _svc.CreateAsync(In("cam-b1", "rtsp://h/y"), default);
        await _svc.CreateAsync(In("cam-p1", "publisher"), default);
        await _svc.CreateAsync(In("cam-p2", "publisher"), default);
        Assert.All(await _svc.ListAsync(default), c => Assert.Empty(c.DuplicateOf));
        _pub.Messages.Clear();
        await _svc.UpdateAsync("cam-b1", In("cam-b1", "rtsp://H:554/x/"), default);
        Assert.Equal(new[] { "cam-b1" }, _pub.Messages.Last(m => m.Key == "cam-a1").Value!.DuplicateOf);
        _pub.Messages.Clear();
        await _svc.UpdateAsync("cam-b1", In("cam-b1", "rtsp://h/y"), default);
        Assert.Empty(_pub.Messages.Last(m => m.Key == "cam-a1").Value!.DuplicateOf);
    }

    [Fact]
    public void Json_field_is_duplicate_of()
    {
        var c = new CameraConfig("cam-a1", "n", "s", 3, true, null, [], 1, "rtsp://h/x", null) { DuplicateOf = ["cam-b1"] };
        var json = System.Text.Json.JsonSerializer.Serialize(c, Json.Options);
        Assert.Contains("\"duplicate_of\":[\"cam-b1\"]", json);
    }
}
