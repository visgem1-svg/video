using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamHealth;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>Contract 5.1 (only the fields stream-health needs; unknown fields are ignored).</summary>
public sealed record CameraConfig(
    string CameraId,
    string? Name,
    double AnalysisFps,
    bool Enabled,
    MotionCfg? Motion = null);   // contract 9.9; missing = disabled

public sealed record MotionCfg(bool Enabled = false, double HeartbeatS = 5);

/// <summary>Contract 5.5.</summary>
public sealed record CameraStatus(
    string CameraId,
    string? WorkerId,
    string State,
    double FpsActual,
    long? LastFrameTs,
    int Reconnects,
    string? Error,
    long Ts,
    string? Mode = null,          // contract 9.8: full|idle; missing = full
    double? FpsTarget = null,     // contract 9.8; missing = camera analysis_fps
    string? ErrorClass = null,    // contract 9.9: auth_failed|not_found|codec_unsupported|unreachable|stalled|decode_error|eof; missing = null
    double? MotionSkippedPct = null);  // contract 9.9: rolling 60 s, 0-100; null when motion disabled/idle/unknown

/// <summary>Contract 5.6.</summary>
public sealed record CameraHealth(
    string CameraId,
    string? Name,
    string State,
    string? WorkerId,
    double FpsActual,
    double FpsTarget,
    long? LastFrameTs,
    [property: JsonPropertyName("reconnects_1h")] int Reconnects1h,
    [property: JsonPropertyName("uptime_pct_1h")] double UptimePct1h,
    string? LastError,
    long Ts,
    string Mode = "full",
    string? ErrorClass = null,
    string? ErrorHint = null,
    double? MotionSkippedPct = null);

/// <summary>ByErrorClass lists only classes with a non-zero count (contract 9.9).</summary>
public sealed record HealthSummary(int Total, int Online, int Degraded, int Offline, int Unassigned, int Idle = 0,
    IReadOnlyDictionary<string, int>? ByErrorClass = null)
{
    public IReadOnlyDictionary<string, int> ByErrorClass { get; init; } = ByErrorClass ?? new Dictionary<string, int>();
}

/// <summary>RTSP error classes of contract 9.9 and their human hints for the UI.</summary>
public static class ErrorClasses
{
    /// <summary>Terminal until the camera config changes: the camera is offline, never degraded.</summary>
    public static bool IsTerminal(string? c) => c is "auth_failed" or "not_found" or "codec_unsupported";

    public static string? Hint(string? c) => c switch
    {
        "auth_failed" => "Wrong credentials",
        "not_found" => "Stream path not found",
        "codec_unsupported" => "Codec not supported",
        "unreachable" => "Camera unreachable",
        "stalled" => "No frames received",
        "decode_error" => "Stream decode errors",
        "eof" => "Stream ended",
        _ => null,
    };
}
