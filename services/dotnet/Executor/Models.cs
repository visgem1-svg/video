using System.Text.Json;
using System.Text.Json.Serialization;

namespace Executor;

// ---- inbound ---------------------------------------------------------------------------------

/// <summary>frames.raw meta header (contract 5.2 / 9.7): no object_key any more; the JPEG is the message value.</summary>
public sealed record FrameMsg(string FrameId, string CameraId, long Ts, long Seq, int Width, int Height,
    string? ObjectKey, string? WorkerId);

/// <summary>camera.config (contract 5.1). Only the fields the executor needs.</summary>
public sealed record CameraConfig(string CameraId, bool Enabled, string? PlanId);

/// <summary>camera.demand (contract 9.8).</summary>
public sealed record CameraDemand(string CameraId, int LiveViewers, int FrameViewers, long Ts);

public sealed record When(string Type, string? Class, double? MinConfidence, int? Gt, string? Step);

public sealed record PlanStep(string Id, string Tool, string Mode, Dictionary<string, JsonElement>? Params, When? When);

/// <summary>pipeline.plans (plan.schema.json).</summary>
public sealed record Plan(string PlanId, int Version, string Name, string? Query, List<string>? CameraIds,
    string Status, long CreatedAt, List<PlanStep> Steps)
{
}

// ---- tool outputs / outbound ------------------------------------------------------------------

public sealed record ObjectDet(string Class, double Confidence, double[] Box);
public sealed record PlateDet(string Plate, double Confidence, double[] Box);
public sealed record FaceDet(double[] Box, double Confidence, string? PersonId, string? PersonName, double? Score);
public sealed record TextDet(string Text, double Confidence, double[] Box);

// raw engine responses (contract 7.1)
public sealed record ObjectsResp(List<ObjectDet>? Objects);
public sealed record PlatesResp(List<PlateDet>? Plates);
public sealed record OcrResp(List<TextDet>? Lines);
public sealed record FacesResp(List<FaceDet>? Faces);

/// <summary>detections (contract 5.3).</summary>
public sealed record Detection(string FrameId, string CameraId, long Ts, int Width, int Height, string PlanId,
    int PlanVersion, List<ObjectDet> Objects, List<PlateDet> Plates, List<FaceDet> Faces, List<TextDet> Texts,
    Dictionary<string, double> TimingsMs, double TotalMs, string ExecutorId, bool Partial = false, string? StepId = null);

/// <summary>recognitions (contract 5.4).</summary>
public sealed record Recognition(string RecognitionId, string Type, string CameraId, string FrameId, long Ts,
    string Value, string? PersonId, double Confidence, double[] Box, string? SnapshotKey, string PlanId);

/// <summary>pipeline.metrics (contract 5.8).</summary>
public sealed record PipelineMetric(string PlanId, string StepId, string Tool, int WindowS, int Processed, int Skipped,
    int Errors, double P50Ms, double P95Ms, string ExecutorId, long Ts);

public static class StepStatus
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Queued = "queued";
}

/// <summary>Result of one step; also the shape of jobs.heavy "context" values.</summary>
public sealed record StepResult(string Status, List<ObjectDet>? Objects = null, List<PlateDet>? Plates = null,
    List<FaceDet>? Faces = null, List<TextDet>? Texts = null);

/// <summary>jobs.heavy (contract 5.9).</summary>
public sealed record JobMsg(string JobId, FrameMsg Frame, string PlanId, int PlanVersion, string StepId,
    Dictionary<string, StepResult>? Context);

public static class Tools
{
    public const string DetectObjects = "detect_objects";
    public const string ReadPlate = "read_plate";
    public const string ReadText = "read_text";
    public const string DetectFaces = "detect_faces";
    public const string IdentifyFaces = "identify_faces";

    /// <summary>name, default URL (from tools.json), default concurrency, default per-attempt timeout ms.</summary>
    public static readonly (string Name, string Url, int Concurrency, int TimeoutMs)[] All =
    {
        (DetectObjects, "http://detector:8101/detect_objects", 8, 4000),
        (ReadPlate, "http://lpr:8102/read_plate", 4, 5000),
        (ReadText, "http://ocr:8103/read_text", 2, 10000),
        (DetectFaces, "http://face:8104/detect_faces", 4, 4000),
        (IdentifyFaces, "http://face:8104/identify_faces", 4, 5000),
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true, UseStringEnumConverter = false)]
[JsonSerializable(typeof(FrameMsg))]
[JsonSerializable(typeof(CameraDemand))]
[JsonSerializable(typeof(CameraConfig))]
[JsonSerializable(typeof(Plan))]
[JsonSerializable(typeof(ObjectsResp))]
[JsonSerializable(typeof(PlatesResp))]
[JsonSerializable(typeof(OcrResp))]
[JsonSerializable(typeof(FacesResp))]
[JsonSerializable(typeof(Detection))]
[JsonSerializable(typeof(Recognition))]
[JsonSerializable(typeof(PipelineMetric))]
[JsonSerializable(typeof(JobMsg))]
internal partial class Json : JsonSerializerContext
{
}
