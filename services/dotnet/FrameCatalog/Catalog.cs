using System.Collections.Concurrent;
using Prometheus;

namespace FrameCatalog;

/// <summary>Class-name and string interning (class names, worker ids, person ids/names): bounded, tiny.</summary>
public static class Interner
{
    private static readonly ConcurrentDictionary<string, string> Strings = new();
    private static readonly object Gate = new();
    private static readonly List<string> Classes = ["?"];
    private static readonly Dictionary<string, ushort> ClassIds = new() { ["?"] = 0 };

    public static string? Str(string? s)
    {
        if (s is null) return null;
        if (Strings.TryGetValue(s, out var v)) return v;
        return Strings.Count < 10_000 ? Strings.GetOrAdd(s, s) : s;
    }

    public static ushort ClassId(string name)
    {
        lock (Gate)
        {
            if (ClassIds.TryGetValue(name, out var id)) return id;
            if (Classes.Count >= ushort.MaxValue) return 0;
            id = (ushort)Classes.Count;
            Classes.Add(name);
            ClassIds[name] = id;
            return id;
        }
    }

    public static string ClassName(ushort id) { lock (Gate) return id < Classes.Count ? Classes[id] : "?"; }
}

internal sealed class Ring
{
    public readonly object Lock = new();
    public FrameRec[] Buf;
    public int Count, Next; // Next = slot of the next write
    public readonly int Cap;
    public (Guid Id, byte[] Jpg)[]? Lru;   // newest JPEGs of this camera: the only place image bytes are retained
    public int LruNext;

    public Ring(int cap) { Cap = cap; Buf = new FrameRec[Math.Min(cap, 32)]; }

    public void CachePut(Guid id, byte[] jpg, int size)
    {
        Lru ??= new (Guid, byte[])[size];
        Lru[LruNext] = (id, jpg);
        LruNext = (LruNext + 1) % Lru.Length;
    }

    public byte[]? CacheGet(Guid id)
    {
        if (Lru is null) return null;
        foreach (var e in Lru) if (e.Jpg is not null && e.Id == id) return e.Jpg;
        return null;
    }

    public int Index(int back) => (Next - 1 - back + Buf.Length * 2) % Buf.Length; // back=0 newest

    public ref FrameRec Newest => ref Buf[Index(0)];
    public ref FrameRec Oldest => ref Buf[Index(Count - 1)];

    /// <returns>true if an old record was overwritten (count unchanged)</returns>
    public bool Add(in FrameRec r)
    {
        var evicted = false;
        if (Count == Buf.Length && Buf.Length < Cap) Grow();
        if (Count == Cap) evicted = true; else Count++;
        Buf[Next] = r;
        Next = (Next + 1) % Buf.Length;
        return evicted;
    }

    private void Grow()
    {
        var nb = new FrameRec[Math.Min(Cap, Buf.Length * 2)];
        for (var i = 0; i < Count; i++) nb[Count - 1 - i] = Buf[Index(i)]; // oldest first
        Buf = nb;
        Next = Count % nb.Length;
    }

    /// <summary>Position (back from newest) of the frame or -1. Scans newest to oldest, at most max entries.</summary>
    public int Find(Guid id, int max = int.MaxValue)
    {
        var n = Math.Min(Count, max);
        for (var i = 0; i < n; i++) if (Buf[Index(i)].Id == id) return i;
        return -1;
    }
}

public sealed record ImageRef(byte[]? Cached, int Partition, long Offset);

public sealed class Catalog
{
    private static readonly Counter Frames = Metrics.CreateCounter("vp_catalog_frames_total", "Frame records added");
    private static readonly Counter Merged = Metrics.CreateCounter("vp_catalog_detections_merged_total", "Detection messages merged into a frame record");
    private static readonly Counter PendingExpired = Metrics.CreateCounter("vp_catalog_pending_expired_total", "Detections dropped without a frame record");
    private static readonly Gauge Records = Metrics.CreateGauge("vp_catalog_records", "Frame records held in memory");
    private static readonly Gauge PendingG = Metrics.CreateGauge("vp_catalog_pending", "Detections waiting for their frame");

    private readonly ConcurrentDictionary<string, Ring> _rings = new();
    private readonly Dictionary<Guid, (Dets D, long Ts, long AddedMs)> _pending = new();
    private readonly object _pendLock = new();
    private readonly CatalogOptions _o;
    private readonly Func<long> _now;
    private long _records;

    public Catalog(CatalogOptions o, Func<long>? now = null)
    {
        _o = o;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public long RecordCount => Interlocked.Read(ref _records);
    public int PendingCount { get { lock (_pendLock) return _pending.Count; } }
    public void UpdateGauges() { Records.Set(RecordCount); PendingG.Set(PendingCount); }

    /// <summary>Adds a frame record. The JPEG is retained only in the per-camera LRU (IMAGE_CACHE_PER_CAMERA newest).</summary>
    public bool AddFrame(FrameMsg f, byte[]? jpeg = null)
    {
        var ring = _rings.GetOrAdd(f.Camera, _ => new Ring(Math.Max(1, _o.FramesPerCamera)));
        var rec = new FrameRec
        {
            Id = f.Id, Ts = f.Ts, Seq = f.Seq, Worker = Interner.Str(f.Worker),
            W = (ushort)Math.Clamp(f.W, 0, ushort.MaxValue), H = (ushort)Math.Clamp(f.H, 0, ushort.MaxValue),
            Partition = f.Partition, Offset = f.Offset,
        };
        (Dets D, long Ts, long AddedMs) pend = default;
        var hasPend = false;
        lock (_pendLock)
        {
            if (_pending.Count > 0 && _pending.Remove(f.Id, out pend)) hasPend = true;
        }
        if (hasPend) { rec.D = pend.D; Merged.Inc(); }
        lock (ring.Lock)
        {
            if (ring.Find(f.Id, 64) >= 0) return false; // redelivery
            if (!ring.Add(rec)) Interlocked.Increment(ref _records);
            if (jpeg is not null && _o.ImageCachePerCamera > 0) ring.CachePut(f.Id, jpeg, _o.ImageCachePerCamera);
        }
        Frames.Inc();
        return true;
    }

    public void MergeDetections(DetMsg d)
    {
        if (_rings.TryGetValue(d.Camera, out var ring))
        {
            lock (ring.Lock)
            {
                var pos = ring.Find(d.Id);
                if (pos >= 0)
                {
                    ref var r = ref ring.Buf[ring.Index(pos)];
                    r.D = Dets.Merge(r.D, d.D);
                    Merged.Inc();
                    return;
                }
                // frame already evicted (older than the oldest held record): useless to keep
                if (ring.Count == ring.Cap && d.Ts < ring.Oldest.Ts) return;
            }
        }
        lock (_pendLock)
        {
            if (_pending.TryGetValue(d.Id, out var p)) _pending[d.Id] = (Dets.Merge(p.D, d.D), p.Ts, p.AddedMs);
            else if (_pending.Count < _o.PendingMax) _pending[d.Id] = (d.D, d.Ts, _now());
            else PendingExpired.Inc();
        }
    }

    public int SweepPending(long? nowMs = null)
    {
        var limit = (nowMs ?? _now()) - _o.PendingTtlMs;
        var removed = 0;
        lock (_pendLock)
        {
            if (_pending.Count == 0) return 0;
            List<Guid>? dead = null;
            foreach (var kv in _pending) if (kv.Value.AddedMs < limit) (dead ??= []).Add(kv.Key);
            if (dead is not null) foreach (var k in dead) { _pending.Remove(k); removed++; }
        }
        if (removed > 0) PendingExpired.Inc(removed);
        return removed;
    }

    // ---------------- queries ----------------
    public FrameList List(string? camera, long? before, int limit)
    {
        limit = Math.Clamp(limit, 1, 200);
        var items = new List<(long Ts, FrameSummary S)>();
        foreach (var (cam, ring) in _rings)
        {
            if (camera is not null && cam != camera) continue;
            lock (ring.Lock)
            {
                var taken = 0;
                for (var i = 0; i < ring.Count && taken < limit; i++)
                {
                    ref var r = ref ring.Buf[ring.Index(i)];
                    if (before is not null && r.Ts >= before) continue;
                    items.Add((r.Ts, Summary(cam, in r)));
                    taken++;
                }
            }
        }
        var res = items.OrderByDescending(x => x.Ts).Take(limit).Select(x => x.S).ToArray();
        return new FrameList(res, res.Length, res.Length == limit ? res[^1].Ts : null);
    }

    /// <summary>Newest record per camera, O(cameras).</summary>
    public FrameSummary[] Latest(IEnumerable<string> cameras)
    {
        var res = new List<FrameSummary>();
        foreach (var cam in cameras.Distinct())
        {
            if (!_rings.TryGetValue(cam, out var ring)) continue;
            lock (ring.Lock) { if (ring.Count > 0) res.Add(Summary(cam, in ring.Newest)); }
        }
        return res.ToArray();
    }

    public FrameDetail? Get(string camera, Guid id)
    {
        if (!_rings.TryGetValue(camera, out var ring)) return null;
        lock (ring.Lock)
        {
            var pos = ring.Find(id);
            return pos < 0 ? null : Detail(camera, in ring.Buf[ring.Index(pos)]);
        }
    }

    /// <summary>Where to get the JPEG: cached bytes or the Kafka location. Null = unknown frame.</summary>
    public ImageRef? ImageRef(string camera, Guid id)
    {
        if (!_rings.TryGetValue(camera, out var ring)) return null;
        lock (ring.Lock)
        {
            var pos = ring.Find(id);
            if (pos < 0) return null;
            ref var r = ref ring.Buf[ring.Index(pos)];
            return new ImageRef(ring.CacheGet(id), r.Partition, r.Offset);
        }
    }

    public CameraStats[] Stats()
    {
        var res = new List<CameraStats>();
        foreach (var (cam, ring) in _rings)
        {
            lock (ring.Lock)
            {
                if (ring.Count == 0) continue;
                long min = long.MaxValue, max = long.MinValue; var an = 0;
                for (var i = 0; i < ring.Count; i++)
                {
                    ref var r = ref ring.Buf[ring.Index(i)];
                    if (r.Ts < min) min = r.Ts;
                    if (r.Ts > max) max = r.Ts;
                    if (r.D is not null) an++;
                }
                var span = (max - min) / 1000.0;
                res.Add(new CameraStats(cam, ring.Count, an, min, max, span > 0 ? Math.Round((ring.Count - 1) / span, 2) : 0));
            }
        }
        return res.OrderBy(s => s.CameraId, StringComparer.Ordinal).ToArray();
    }

    // ---------------- mapping ----------------
    private static double C(ushort v) => v / 10000.0;
    private static int[] B(Box b) => [b.X1, b.Y1, b.X2, b.Y2];

    private static Dictionary<string, int> Counts(Dets? d)
    {
        var m = new Dictionary<string, int>();
        if (d?.Objects is { } o) foreach (var x in o) { var n = Interner.ClassName(x.Cls); m[n] = m.GetValueOrDefault(n) + 1; }
        return m;
    }

    private static double? S(short s) => s == short.MinValue ? null : s / 10000.0;

    private static FrameSummary Summary(string cam, in FrameRec r)
    {
        var d = r.D;
        return new FrameSummary(r.Id.ToString(), cam, r.Ts, r.Seq, r.W, r.H, r.Worker, d is not null, Counts(d),
            d?.Plates?.Select(p => new PlateLite(p.Plate, C(p.Conf))).ToArray() ?? [],
            d?.Faces?.Select(f => new FaceLite(C(f.Conf), f.PersonId, f.PersonName, S(f.Score))).ToArray() ?? [],
            d?.Texts?.Select(t => new TextLite(t.Text, C(t.Conf))).ToArray() ?? [],
            d is null ? null : Math.Round(d.TotalMs, 1));
    }

    private static FrameDetail Detail(string cam, in FrameRec r)
    {
        var d = r.D;
        return new FrameDetail(r.Id.ToString(), cam, r.Ts, r.Seq, r.W, r.H, r.Worker, d is not null, Counts(d),
            d?.Plates?.Select(p => new PlateBox(p.Plate, C(p.Conf), B(p.Box))).ToArray() ?? [],
            d?.Faces?.Select(f => new FaceBox(B(f.Box), C(f.Conf), f.PersonId, f.PersonName, S(f.Score))).ToArray() ?? [],
            d?.Texts?.Select(t => new TextBox(t.Text, C(t.Conf), B(t.Box))).ToArray() ?? [],
            d is null ? null : Math.Round(d.TotalMs, 1),
            d?.Objects?.Select(o => new ObjectBox(Interner.ClassName(o.Cls), C(o.Conf), B(o.Box))).ToArray() ?? []);
    }
}
