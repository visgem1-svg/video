using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameCatalog;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

public sealed class CatalogOptions
{
    public string KafkaBootstrap { get; init; } = "redpanda:9092";
    public int ImageCachePerCamera { get; init; } = 3;
    public int FetchTimeoutMs { get; init; } = 2000;
    public int FramesPerCamera { get; init; } = 600;
    public int PendingTtlMs { get; init; } = 30_000;
    public int PendingMax { get; init; } = 20_000;
}

// ---- compact storage (no per-detection class instances) ----
public readonly record struct Box(ushort X1, ushort Y1, ushort X2, ushort Y2);                        // 8 B
public readonly record struct ObjDet(ushort Cls, ushort Conf, Box Box);                                // 12 B (conf x10000)
public readonly record struct PlateDet(string Plate, ushort Conf, Box Box);
public readonly record struct TextDet(string Text, ushort Conf, Box Box);
public readonly record struct FaceDet(Box Box, ushort Conf, short Score, string? PersonId, string? PersonName); // Score: x10000, short.MinValue = null

/// <summary>Detections of a frame. Immutable; a merge builds a new instance (rare).</summary>
public sealed class Dets
{
    public ObjDet[]? Objects;
    public PlateDet[]? Plates;
    public FaceDet[]? Faces;
    public TextDet[]? Texts;
    public float TotalMs;

    /// <summary>Per category: a non-empty incoming array replaces the old one (idempotent on redelivery).</summary>
    public static Dets Merge(Dets? a, Dets b) => new()
    {
        Objects = b.Objects is { Length: > 0 } ? b.Objects : a?.Objects,
        Plates = b.Plates is { Length: > 0 } ? b.Plates : a?.Plates,
        Faces = b.Faces is { Length: > 0 } ? b.Faces : a?.Faces,
        Texts = b.Texts is { Length: > 0 } ? b.Texts : a?.Texts,
        TotalMs = Math.Max(a?.TotalMs ?? 0, b.TotalMs),
    };
}

/// <summary>Ring slot: 16+8+8+8+8+4+4+2+2 = 60 -> 64 B.</summary>
public struct FrameRec
{
    public Guid Id;
    public long Ts;
    public long Offset;           // frames.raw offset (the JPEG is re-fetched from Kafka)
    public int Partition;
    public string? Worker;        // interned
    public Dets? D;               // null = not analyzed (yet)
    public int Seq;
    public ushort W, H;
}

public sealed record FrameMsg(Guid Id, string Camera, long Ts, int Seq, int W, int H, string? Worker, int Partition = 0, long Offset = 0);
public sealed record DetMsg(Guid Id, string Camera, long Ts, bool Partial, string? StepId, Dets D);

// ---- API DTOs ----
public sealed record PlateLite(string Plate, double Confidence);
public sealed record FaceLite(double Confidence, string? PersonId, string? PersonName, double? Score);
public sealed record TextLite(string Text, double Confidence);
public sealed record FrameSummary(string FrameId, string CameraId, long Ts, int Seq, int Width, int Height, string? WorkerId,
    bool Analyzed, Dictionary<string, int> Objects, PlateLite[] Plates, FaceLite[] Faces, TextLite[] Texts, double? TotalMs);

public sealed record ObjectBox(string Class, double Confidence, int[] Box);
public sealed record PlateBox(string Plate, double Confidence, int[] Box);
public sealed record FaceBox(int[] Box, double Confidence, string? PersonId, string? PersonName, double? Score);
public sealed record TextBox(string Text, double Confidence, int[] Box);
/// <summary>Single record: same fields as the list item (objects = counts) plus the boxes.</summary>
public sealed record FrameDetail(string FrameId, string CameraId, long Ts, int Seq, int Width, int Height, string? WorkerId,
    bool Analyzed, Dictionary<string, int> Objects, PlateBox[] Plates, FaceBox[] Faces, TextBox[] Texts, double? TotalMs, ObjectBox[] ObjectBoxes);

public sealed record FrameList(FrameSummary[] Items, int Count, long? NextBefore);
public sealed record CameraStats(string CameraId, int Frames, int Analyzed, long OldestTs, long NewestTs, double Fps);
