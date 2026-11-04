using System.Text.Json;

namespace FrameCatalog;

public static class Parser
{
    /// <summary>Parses the <c>meta</c> header of a frames.raw message.</summary>
    public static FrameMsg? ParseFrame(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (!e.TryGetProperty("frame_id", out var fid) || !fid.TryGetGuid(out var id)) return null;
            var cam = Str(e, "camera_id");
            if (cam is null || cam.Length > 64 || !e.TryGetProperty("ts", out var ts) || !ts.TryGetInt64(out var t)) return null;
            return new FrameMsg(id, Interner.Str(cam)!, t, Int(e, "seq"), Int(e, "width"), Int(e, "height"), Str(e, "worker_id"));
        }
        catch (JsonException) { return null; }
    }

    public static DetMsg? ParseDetection(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (!e.TryGetProperty("frame_id", out var fid) || !fid.TryGetGuid(out var id)) return null;
            var cam = Str(e, "camera_id");
            if (cam is null || cam.Length > 64 || !e.TryGetProperty("ts", out var ts) || !ts.TryGetInt64(out var t)) return null;
            var d = new Dets { TotalMs = e.TryGetProperty("total_ms", out var tm) && tm.ValueKind == JsonValueKind.Number ? (float)tm.GetDouble() : 0 };
            d.Objects = Arr(e, "objects", x => new ObjDet(Interner.ClassId(Str(x, "class") ?? "?"), Conf(x), ReadBox(x)));
            d.Plates = Arr(e, "plates", x => new PlateDet(Str(x, "plate") ?? "", Conf(x), ReadBox(x)));
            d.Texts = Arr(e, "texts", x => new TextDet(Str(x, "text") ?? "", Conf(x), ReadBox(x)));
            d.Faces = Arr(e, "faces", x => new FaceDet(ReadBox(x), Conf(x), Scaled(x, "score"), Interner.Str(Str(x, "person_id")), Interner.Str(Str(x, "person_name"))));
            var partial = e.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
            return new DetMsg(id, Interner.Str(cam)!, t, partial, Str(e, "step_id"), d);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    private static double Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static ushort Conf(JsonElement x) => (ushort)Math.Clamp(Math.Round(Num(x, "confidence") * 10000), 0, 10000);
    private static short Scaled(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (short)Math.Clamp(Math.Round(v.GetDouble() * 10000), -30000, 30000) : short.MinValue;

    private static Box ReadBox(JsonElement x)
    {
        if (!x.TryGetProperty("box", out var b) || b.ValueKind != JsonValueKind.Array || b.GetArrayLength() < 4) return default;
        Span<ushort> v = stackalloc ushort[4];
        var i = 0;
        foreach (var n in b.EnumerateArray())
        {
            if (i == 4) break;
            v[i++] = n.ValueKind == JsonValueKind.Number ? (ushort)Math.Clamp(Math.Round(n.GetDouble()), 0, ushort.MaxValue) : (ushort)0;
        }
        return new Box(v[0], v[1], v[2], v[3]);
    }

    private static T[]? Arr<T>(JsonElement e, string n, Func<JsonElement, T> f)
    {
        if (!e.TryGetProperty(n, out var a) || a.ValueKind != JsonValueKind.Array) return null;
        var len = a.GetArrayLength();
        if (len == 0) return null;
        var res = new T[len];
        var i = 0;
        foreach (var x in a.EnumerateArray()) res[i++] = f(x);
        return res;
    }
}
