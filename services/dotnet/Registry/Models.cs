using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry;

/// <summary>JSON options shared by HTTP and Kafka: snake_case (contract rule 1.7).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>Contract 5.1 camera.config value.</summary>
public sealed record CameraConfig(
    string CameraId,
    string Name,
    string SourceUrl,
    double AnalysisFps,
    bool Enabled,
    string? PlanId,
    string[] Tags,
    long UpdatedAt,
    string StreamUrl = "publisher",
    string? ProvisionError = null)
{
    /// <summary>Other cameras pulling the same normalized stream_url (contract 9.4); computed, never stored.</summary>
    public string[] DuplicateOf { get; init; } = [];

    /// <summary>Operator pin (contract 9.5); null = none. Persisted.</summary>
    public string? PinnedWorker { get; init; }

    /// <summary>Bumped by POST /api/cameras/{id}/restart; extractors restart the camera when it changes. Persisted.</summary>
    public int Generation { get; init; }

    /// <summary>Computed: worker currently owning the camera (null = unassigned).</summary>
    public string? AssignedWorker { get; init; }

    /// <summary>Computed: pinned, but not running on the pinned worker (dead / draining / full).</summary>
    public bool PinFallback { get; init; }

    /// <summary>Optional real RTSP URL of the camera's substream used for analysis (contract 9.9). Persisted.</summary>
    public string? AnalysisStreamUrl { get; init; }

    /// <summary>Read-only, derived: rtsp://mediamtx:8554/{camera_id}-analysis, null when there is no analysis_stream_url.</summary>
    public string? AnalysisSourceUrl => AnalysisStreamUrl is null ? null : SourceUrl + CameraService.AnalysisSuffix;

    /// <summary>Motion gating settings (contract 9.9). Persisted; missing = defaults (disabled).</summary>
    public MotionConfig Motion { get; init; } = MotionConfig.Default;

    /// <summary>Computed runtime extraction mode (contract 9.8). Never stored, never published to camera.config.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Registry.Assignment.Extract? Extract { get; init; }
}

/// <summary>Contract 9.9 camera.motion.</summary>
public sealed record MotionConfig(bool Enabled, string Sensitivity, double HeartbeatS)
{
    public static readonly MotionConfig Default = new(false, "medium", 5);
}

/// <summary>Motion as sent by clients; missing members keep the previous value (PUT) or the default (POST).</summary>
public sealed record MotionInput(bool? Enabled, string? Sensitivity, double? HeartbeatS);

/// <summary>Body of POST/PUT (5.1 without updated_at). Missing optional fields get defaults.</summary>
public sealed record CameraInput(
    string? CameraId,
    string? Name,
    string? SourceUrl,
    double? AnalysisFps,
    bool? Enabled,
    string? PlanId,
    string[]? Tags,
    string? StreamUrl = null,
    string? AnalysisStreamUrl = null,
    MotionInput? Motion = null);

public sealed record HeartbeatRequest(int Capacity, string[]? Running);

public sealed record HeartbeatResponse(IReadOnlyList<CameraConfig> Assigned, int LeaseS);

public sealed record WorkerInfo(string WorkerId, int Capacity, IReadOnlyList<string> Assigned, long LastSeen,
    int? CapacityOverride, bool Draining, string State, string ManagedBy, double Load = 0);

public sealed record CapacityRequest(int? Capacity);
public sealed record PinRequest(string? WorkerId);

public static class Duplicates
{
    private static readonly Dictionary<string, int> DefaultPorts = new()
    {
        ["rtsp"] = 554, ["rtsps"] = 322, ["rtmp"] = 1935, ["rtmps"] = 443, ["http"] = 80, ["https"] = 443,
    };

    /// <summary>Lowercase scheme/host, default port, credentials, fragment and trailing '/' removed. Null for publisher/invalid.</summary>
    public static string? Normalize(string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl) || streamUrl == "publisher") return null;
        if (!Uri.TryCreate(streamUrl.Trim(), UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
        var scheme = u.Scheme.ToLowerInvariant();
        var port = u.Port;
        var portPart = port < 0 || (DefaultPorts.TryGetValue(scheme, out var d) && d == port) ? "" : ":" + port;
        var path = u.AbsolutePath.TrimEnd('/');
        return $"{scheme}://{u.Host.ToLowerInvariant()}{portPart}{path}{u.Query}";
    }

    /// <summary>
    /// camera_id -> sorted ids of the other cameras with the same normalized stream_url, or the same normalized
    /// analysis_stream_url (empty when unique). A camera's own main + sub URLs, or one camera's sub equal to another's
    /// main, are never duplicates: main is compared with main, analysis with analysis.
    /// </summary>
    public static Dictionary<string, string[]> Compute(IEnumerable<CameraConfig> cameras)
    {
        var list = cameras.ToList();
        var main = Group(list, c => c.StreamUrl);
        var sub = Group(list, c => c.AnalysisStreamUrl);
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var c in list)
        {
            var d = new SortedSet<string>(StringComparer.Ordinal);
            if (Normalize(c.StreamUrl) is { } k && main[k].Length > 1) d.UnionWith(main[k]);
            if (Normalize(c.AnalysisStreamUrl) is { } a && sub[a].Length > 1) d.UnionWith(sub[a]);
            d.Remove(c.CameraId);
            result[c.CameraId] = [.. d];
        }
        return result;
    }

    private static Dictionary<string, string[]> Group(List<CameraConfig> list, Func<CameraConfig, string?> url) =>
        list.Select(c => (c.CameraId, Key: Normalize(url(c)))).Where(x => x.Key is not null)
            .GroupBy(x => x.Key!).ToDictionary(g => g.Key, g => g.Select(x => x.CameraId).ToArray());
}
