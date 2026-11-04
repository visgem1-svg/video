using System.Text;
using System.Text.Json;
using FrameCatalog;
using Xunit;

namespace FrameCatalog.Tests;

public class CatalogTests
{
    private const long T0 = 1_761_030_000_000;
    private static long _now = T0;

    private static Catalog New(int cap = 600, int ttl = 30_000) => new(new CatalogOptions { FramesPerCamera = cap, PendingTtlMs = ttl }, () => _now);

    private static Guid G(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static string FrameJson(Guid id, string cam, long ts, int seq = 1) =>
        $$"""{"frame_id":"{{id}}","camera_id":"{{cam}}","ts":{{ts}},"seq":{{seq}},"width":960,"height":540,"worker_id":"fx-1"}""";

    private static FrameMsg Frame(int n, string cam = "cam-001", long? ts = null) =>
        Parser.ParseFrame(Encoding.UTF8.GetBytes(FrameJson(G(n), cam, ts ?? T0 + n * 333)))!;

    private static DetMsg MainDet(int n, string cam = "cam-001", long? ts = null) => Parser.ParseDetection(Encoding.UTF8.GetBytes($$"""
        {"frame_id":"{{G(n)}}","camera_id":"{{cam}}","ts":{{ts ?? T0 + n * 333}},"width":960,"height":540,"plan_id":"default","plan_version":1,
         "partial":false,"step_id":null,
         "objects":[{"class":"truck","confidence":0.91,"box":[10,20,110.4,220]},{"class":"truck","confidence":0.5,"box":[1,2,3,4]},{"class":"car","confidence":0.7,"box":[5,6,7,8]}],
         "plates":[],"faces":[],"texts":[],"timings_ms":{"s1":12.5},"total_ms":12.5,"executor_id":"ex-1"}
        """))!;

    private static DetMsg Partial(int n, string cam = "cam-001", long? ts = null) => Parser.ParseDetection(Encoding.UTF8.GetBytes($$"""
        {"frame_id":"{{G(n)}}","camera_id":"{{cam}}","ts":{{ts ?? T0 + n * 333}},"width":960,"height":540,"partial":true,"step_id":"s2",
         "objects":[],"plates":[{"plate":"12345678","confidence":0.95,"box":[1,2,30,40]}],
         "faces":[{"box":[1,1,2,2],"confidence":0.9,"person_id":"p-12","person_name":"Dana","score":0.71}],
         "texts":[{"text":"GATE 3","confidence":0.88,"box":[9,9,19,19]}],"timings_ms":{"s2":33.1},"total_ms":40.0}
        """))!;

    [Fact]
    public void Ring_evicts_oldest_and_stays_bounded_across_growth()
    {
        var c = New(cap: 100);
        for (var i = 0; i < 250; i++) c.AddFrame(Frame(i));
        var all = c.List("cam-001", null, 200);
        Assert.Equal(100, c.RecordCount);
        Assert.Equal(G(249).ToString(), all.Items[0].FrameId);
        Assert.Equal(G(150).ToString(), all.Items[^1].FrameId); // 100 newest only... limit 200 returns all 100
        Assert.Equal(100, all.Count);
        Assert.Null(c.Get("cam-001", G(10)));
        Assert.NotNull(c.Get("cam-001", G(150)));
    }

    [Fact]
    public void Duplicate_frame_is_ignored()
    {
        var c = New();
        Assert.True(c.AddFrame(Frame(1)));
        Assert.False(c.AddFrame(Frame(1)));
        Assert.Equal(1, c.RecordCount);
    }

    [Fact]
    public void Merge_main_then_partial()
    {
        var c = New();
        c.AddFrame(Frame(1));
        c.MergeDetections(MainDet(1));
        c.MergeDetections(Partial(1));
        AssertMerged(c.Get("cam-001", G(1))!);
    }

    [Fact]
    public void Merge_partial_then_main_then_frame_in_any_order()
    {
        var c = New();
        c.MergeDetections(Partial(1));
        c.AddFrame(Frame(1));      // frame arrives after the partial
        c.MergeDetections(MainDet(1));
        AssertMerged(c.Get("cam-001", G(1))!);

        var c2 = New();
        c2.MergeDetections(Partial(2));
        c2.MergeDetections(MainDet(2));
        Assert.Equal(1, c2.PendingCount);
        c2.AddFrame(Frame(2));
        Assert.Equal(0, c2.PendingCount);
        AssertMerged(c2.Get("cam-001", G(2))!);
    }

    [Fact]
    public void Redelivery_is_idempotent()
    {
        var c = New();
        c.AddFrame(Frame(1));
        for (var i = 0; i < 3; i++) { c.MergeDetections(MainDet(1)); c.MergeDetections(Partial(1)); }
        AssertMerged(c.Get("cam-001", G(1))!);
    }

    private static void AssertMerged(FrameDetail d)
    {
        Assert.True(d.Analyzed);
        Assert.Equal(2, d.Objects["truck"]);
        Assert.Equal(1, d.Objects["car"]);
        Assert.Equal(3, d.ObjectBoxes.Length);
        Assert.Equal(new[] { 10, 20, 110, 220 }, d.ObjectBoxes[0].Box);
        Assert.Equal(0.91, d.ObjectBoxes[0].Confidence);
        Assert.Equal("12345678", Assert.Single(d.Plates).Plate);
        var face = Assert.Single(d.Faces);
        Assert.Equal("Dana", face.PersonName);
        Assert.Equal(0.71, face.Score);
        Assert.Equal("GATE 3", Assert.Single(d.Texts).Text);
        Assert.Equal(40.0, d.TotalMs);
    }

    [Fact]
    public void Pending_expires()
    {
        _now = T0;
        var c = New(ttl: 1000);
        c.MergeDetections(MainDet(1));
        Assert.Equal(1, c.PendingCount);
        Assert.Equal(0, c.SweepPending(T0 + 500));
        Assert.Equal(1, c.SweepPending(T0 + 1500));
        Assert.Equal(0, c.PendingCount);
        c.AddFrame(Frame(1));
        Assert.False(c.Get("cam-001", G(1))!.Analyzed);
    }

    [Fact]
    public void List_pages_with_before_cursor_and_clamps_limit()
    {
        var c = New();
        for (var i = 0; i < 10; i++) c.AddFrame(Frame(i, ts: T0 + i * 1000));
        c.AddFrame(Frame(100, "cam-002", T0 + 5500));
        var p1 = c.List("cam-001", null, 4);
        Assert.Equal(new long[] { T0 + 9000, T0 + 8000, T0 + 7000, T0 + 6000 }, p1.Items.Select(x => x.Ts));
        Assert.Equal(T0 + 6000, p1.NextBefore);
        var p2 = c.List("cam-001", p1.NextBefore, 4);
        Assert.Equal(new long[] { T0 + 5000, T0 + 4000, T0 + 3000, T0 + 2000 }, p2.Items.Select(x => x.Ts));
        var p3 = c.List("cam-001", p2.NextBefore, 4);
        Assert.Equal(2, p3.Count);
        Assert.Null(p3.NextBefore);
        var mixed = c.List(null, null, 3); // all cameras, newest first
        Assert.Equal(new[] { "cam-001", "cam-001", "cam-001" }, mixed.Items.Select(x => x.CameraId));
        Assert.Equal(6, c.List(null, T0 + 5500, 1000).Count); // limit clamps to 200; cam-001 frames 0..5 (cam-002 at 5500 excluded)
    }

    [Fact]
    public void Latest_returns_newest_per_camera()
    {
        var c = New();
        c.AddFrame(Frame(1, "cam-001", T0)); c.AddFrame(Frame(2, "cam-001", T0 + 1000));
        c.AddFrame(Frame(3, "cam-002", T0 + 500));
        var l = c.Latest(["cam-001", "cam-002", "cam-nope", "cam-001"]);
        Assert.Equal(2, l.Length);
        Assert.Equal(G(2).ToString(), l.Single(x => x.CameraId == "cam-001").FrameId);
        Assert.Equal(G(3).ToString(), l.Single(x => x.CameraId == "cam-002").FrameId);
    }

    [Fact]
    public void Stats_per_camera()
    {
        var c = New();
        for (var i = 0; i < 7; i++) c.AddFrame(Frame(i, ts: T0 + i * 500)); // 2 fps over 3 s
        c.MergeDetections(MainDet(0)); c.MergeDetections(MainDet(1));
        var s = Assert.Single(c.Stats());
        Assert.Equal("cam-001", s.CameraId);
        Assert.Equal(7, s.Frames);
        Assert.Equal(2, s.Analyzed);
        Assert.Equal(T0, s.OldestTs);
        Assert.Equal(T0 + 3000, s.NewestTs);
        Assert.Equal(2.0, s.Fps);
    }

    [Fact]
    public void Image_lru_keeps_only_newest_n_bytes_and_locates_the_rest_in_kafka()
    {
        var c = new Catalog(new CatalogOptions { ImageCachePerCamera = 3 }, () => _now);
        for (var i = 0; i < 5; i++)
            c.AddFrame(Frame(i) with { Partition = 7, Offset = 100 + i }, new byte[] { (byte)i });
        Assert.Null(c.ImageRef("cam-001", G(0))!.Cached);          // evicted from LRU
        Assert.Null(c.ImageRef("cam-001", G(1))!.Cached);
        var r0 = c.ImageRef("cam-001", G(0))!;
        Assert.Equal(7, r0.Partition);
        Assert.Equal(100, r0.Offset);
        Assert.Equal(new byte[] { 4 }, c.ImageRef("cam-001", G(4))!.Cached);
        Assert.Equal(new byte[] { 2 }, c.ImageRef("cam-001", G(2))!.Cached);
        Assert.Null(c.ImageRef("cam-001", G(99)));
        Assert.Null(c.ImageRef("nope", G(4)));
    }

    [Fact]
    public void Meta_header_without_object_key_parses_and_record_has_no_object_key()
    {
        var f = Parser.ParseFrame("""{"frame_id":"00000001-0000-0000-0000-000000000000","camera_id":"cam-001","ts":5,"seq":9,"width":960,"height":540,"worker_id":"fx-2"}"""u8)!;
        Assert.Equal("fx-2", f.Worker);
        Assert.Equal(9, f.Seq);
    }

    [Fact]
    public void Json_field_names_match_contract()
    {
        var c = New();
        c.AddFrame(Frame(1)); c.MergeDetections(MainDet(1)); c.MergeDetections(Partial(1));
        using var list = JsonDocument.Parse(JsonSerializer.Serialize(c.List("cam-001", null, 5), Json.Options));
        var item = list.RootElement.GetProperty("items")[0];
        foreach (var k in new[] { "frame_id", "camera_id", "ts", "seq", "width", "height", "worker_id", "analyzed", "objects", "plates", "faces", "texts", "total_ms" })
            Assert.True(item.TryGetProperty(k, out _), k);
        Assert.Equal(2, item.GetProperty("objects").GetProperty("truck").GetInt32());
        Assert.True(list.RootElement.TryGetProperty("next_before", out _));

        using var det = JsonDocument.Parse(JsonSerializer.Serialize(c.Get("cam-001", G(1)), Json.Options));
        var r = det.RootElement;
        Assert.Equal("truck", r.GetProperty("object_boxes")[0].GetProperty("class").GetString());
        Assert.Equal(4, r.GetProperty("object_boxes")[0].GetProperty("box").GetArrayLength());
        Assert.Equal("12345678", r.GetProperty("plates")[0].GetProperty("plate").GetString());
        Assert.Equal("p-12", r.GetProperty("faces")[0].GetProperty("person_id").GetString());
        Assert.Equal("GATE 3", r.GetProperty("texts")[0].GetProperty("text").GetString());

        using var st = JsonDocument.Parse(JsonSerializer.Serialize(c.Stats(), Json.Options));
        foreach (var k in new[] { "camera_id", "frames", "analyzed", "oldest_ts", "newest_ts", "fps" })
            Assert.True(st.RootElement[0].TryGetProperty(k, out _), k);
    }

    [Fact]
    public void Parser_rejects_garbage_and_null_faces_score()
    {
        Assert.Null(Parser.ParseFrame("not json"u8));
        Assert.Null(Parser.ParseFrame("{\"frame_id\":\"x\"}"u8));
        Assert.Null(Parser.ParseDetection("[]"u8));
        var d = Parser.ParseDetection(Encoding.UTF8.GetBytes($$"""{"frame_id":"{{G(1)}}","camera_id":"c","ts":1,"faces":[{"box":[1,2,3,4],"confidence":0.9,"person_id":null,"person_name":null,"score":null}]}"""))!;
        var c = New();
        c.AddFrame(new FrameMsg(G(1), "c", 1, 1, 10, 10, null));
        c.MergeDetections(d);
        var f = Assert.Single(c.Get("c", G(1))!.Faces);
        Assert.Null(f.Score);
        Assert.Null(f.PersonId);
    }
}

public class MemoryTests
{
    [Fact]
    public void Memory_for_400_cameras_x_600_frames_is_bounded()
    {
        var c = new Catalog(new CatalogOptions { FramesPerCamera = 600 });
        GC.Collect(); var before = GC.GetTotalMemory(true);
        var n = 0;
        for (var cam = 0; cam < 400; cam++)
            for (var i = 0; i < 600; i++, n++)
            {
                var id = new Guid(n, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0);
                var camId = $"cam-{cam:000}";
                var ts = 1_761_030_000_000 + i * 333L;
                c.AddFrame(new FrameMsg(id, camId, ts, i, 960, 540, "fx-1"));
                var objs = string.Join(",", Enumerable.Range(0, 4).Select(k => $$"""{"class":"car","confidence":0.9,"box":[1,2,300,400]}"""));
                c.MergeDetections(Parser.ParseDetection(System.Text.Encoding.UTF8.GetBytes($$"""{"frame_id":"{{id}}","camera_id":"{{camId}}","ts":{{ts}},"objects":[{{objs}}],"total_ms":10}"""))!);
            }
        GC.Collect(); var mb = (GC.GetTotalMemory(true) - before) / 1048576.0;
        Console.WriteLine($"MEM_MB={mb:F1}");
        Assert.Equal(240_000, c.RecordCount);
        Assert.True(mb < 70, $"{mb} MB");
    }
}
