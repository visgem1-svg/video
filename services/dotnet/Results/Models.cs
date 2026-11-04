using System.Text.Json;
using System.Text.Json.Serialization;

namespace Results;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

// ---- contract 5.3 detections -------------------------------------------------------------
public sealed record ObjectDet([property: JsonPropertyName("class")] string Class, double Confidence, double[] Box);
public sealed record PlateDet(string Plate, double Confidence, double[] Box);
public sealed record FaceDet(double[] Box, double Confidence, string? PersonId, string? PersonName, double? Score);
public sealed record TextDet(string Text, double Confidence, double[] Box);

public sealed record Detection(
    string FrameId, string CameraId, long Ts, int Width, int Height,
    string? PlanId, int? PlanVersion,
    List<ObjectDet>? Objects, List<PlateDet>? Plates, List<FaceDet>? Faces, List<TextDet>? Texts,
    Dictionary<string, double>? TimingsMs, double? TotalMs, string? ExecutorId,
    bool? Partial = null, string? StepId = null);

// ---- contract 5.4 recognitions -----------------------------------------------------------
public sealed record Recognition(
    string RecognitionId, string Type, string CameraId, string FrameId, long Ts, string Value,
    string? PersonId, double Confidence, double[]? Box, string? SnapshotKey, string? PlanId);

/// <summary>Recognition as stored (snapshot_key filled in after the copy).</summary>
public sealed record StoredRecognition(Recognition R, string? SnapshotKey);

// ---- API (7.4) ---------------------------------------------------------------------------
public sealed record RecognitionDto(
    string RecognitionId, string Type, string CameraId, string FrameId, long Ts, string Value,
    string? PersonId, double Confidence, double[]? Box, string? PlanId, string? SnapshotKey, string? SnapshotUrl);

public sealed record DetectionStat(string CameraId, [property: JsonPropertyName("class")] string Class, long MinuteTs, long Count);

// ---- internals ---------------------------------------------------------------------------
public readonly record struct TP(string Topic, int Partition);
public readonly record struct AggKey(string CameraId, string Class, long MinuteTs);
public sealed record InMessage(string Topic, int Partition, long Offset, string? Value);

/// <summary>For one partition: first kept offset in the batch and the next offset to consume afterwards.</summary>
public sealed record OffsetRange(long First, long Next);

public sealed record BatchWrite(
    IReadOnlyList<StoredRecognition> Recognitions,
    IReadOnlyDictionary<AggKey, long> Aggregates,
    IReadOnlyDictionary<TP, OffsetRange> Offsets);

public sealed class OffsetFencedException(string message) : Exception(message);

public sealed class ResultsOptions
{
    public string KafkaBootstrap { get; set; } = "redpanda:9092";
    public string DetectionsTopic { get; set; } = "detections";
    public string RecognitionsTopic { get; set; } = "recognitions";
    public string MinioEndpoint { get; set; } = "minio:9000";
    public string MinioAccessKey { get; set; } = "minio";
    public string MinioSecretKey { get; set; } = "minio12345";
    public bool MinioUseSsl { get; set; }
    public string SnapshotsBucket { get; set; } = "snapshots";
    public int BatchMax { get; set; } = 500;
    public int BatchFlushMs { get; set; } = 500;
    public int RecognitionRetentionDays { get; set; } = 7;
}
