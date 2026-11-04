using System.Globalization;

namespace Executor;

public sealed class ExecutorOptions
{
    public string ExecutorId { get; init; } = "ex-local";
    public string KafkaBootstrap { get; init; } = "redpanda:9092";
    public int MaxInflightFrames { get; init; } = 32;
    public long MaxFrameAgeMs { get; init; } = 1500;
    public int FrameTimeoutMs { get; init; } = 15000;
    public int HeavyConcurrency { get; init; } = 4;
    public long HeavyMaxAgeMs { get; init; } = 60000;
    public int ToolRetries { get; init; } = 1;
    public int MetricsWindowS { get; init; } = 5;
    public int RecognitionCooldownS { get; init; } = 30;
    public int UnknownFaceCooldownS { get; init; } = 10;
    public int DrainTimeoutMs { get; init; } = 10000;

    public string MinioEndpoint { get; init; } = "http://minio:9000";
    public string MinioAccessKey { get; init; } = "minio";
    public string MinioSecretKey { get; init; } = "minio12345";
    public string SnapshotsBucket { get; init; } = "snapshots";
    public int SnapshotConcurrency { get; init; } = 8;
    public string MinioRegion { get; init; } = "us-east-1";

    public Dictionary<string, string> ToolUrls { get; init; } = new();
    public Dictionary<string, int> ToolConcurrency { get; init; } = new();
    public Dictionary<string, int> ToolTimeoutMs { get; init; } = new();

    public static string EngineBaseVar(string tool) => tool switch
    {
        Tools.DetectObjects => "DETECTOR_URL",
        Tools.ReadPlate => "LPR_URL",
        Tools.ReadText => "OCR_URL",
        _ => "FACE_URL",
    };

    public static ExecutorOptions FromEnvironment(Func<string, string?> get)
    {
        string S(string k, string d) => string.IsNullOrWhiteSpace(get(k)) ? d : get(k)!;
        int I(string k, int d) => int.TryParse(get(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : d;

        var urls = new Dictionary<string, string>();
        var conc = new Dictionary<string, int>();
        var tmo = new Dictionary<string, int>();
        foreach (var t in Tools.All)
        {
            var u = t.Name.ToUpperInvariant();
            var baseUrl = get(EngineBaseVar(t.Name));
            var dflt = string.IsNullOrWhiteSpace(baseUrl) ? t.Url : baseUrl.TrimEnd('/') + "/" + t.Name;
            urls[t.Name] = S("TOOL_URL_" + u, dflt);
            conc[t.Name] = I("TOOL_CONCURRENCY_" + u, t.Concurrency);
            tmo[t.Name] = I("TOOL_TIMEOUT_MS_" + u, t.TimeoutMs);
        }

        return new ExecutorOptions
        {
            ExecutorId = S("EXECUTOR_ID", "ex-" + Environment.MachineName.ToLowerInvariant()),
            KafkaBootstrap = S("KAFKA_BOOTSTRAP", "redpanda:9092"),
            MaxInflightFrames = I("MAX_INFLIGHT_FRAMES", 32),
            MaxFrameAgeMs = I("MAX_FRAME_AGE_MS", 1500),
            FrameTimeoutMs = I("FRAME_TIMEOUT_MS", 15000),
            HeavyConcurrency = I("HEAVY_CONCURRENCY", 4),
            HeavyMaxAgeMs = I("HEAVY_MAX_AGE_MS", 60000),
            ToolRetries = int.TryParse(get("TOOL_RETRIES"), out var r) && r >= 0 ? r : 1,
            MetricsWindowS = I("METRICS_WINDOW_S", 5),
            RecognitionCooldownS = int.TryParse(get("RECOGNITION_COOLDOWN_S"), out var rc) && rc >= 0 ? rc : 30,
            UnknownFaceCooldownS = int.TryParse(get("UNKNOWN_FACE_COOLDOWN_S"), out var uc) && uc >= 0 ? uc : 10,
            DrainTimeoutMs = I("DRAIN_TIMEOUT_MS", 10000),
            MinioEndpoint = NormalizeEndpoint(S("MINIO_ENDPOINT", "minio:9000"), get("MINIO_USE_SSL")),
            MinioAccessKey = S("MINIO_ACCESS_KEY", "minio"),
            MinioSecretKey = S("MINIO_SECRET_KEY", "minio12345"),
            SnapshotsBucket = S("MINIO_SNAPSHOTS_BUCKET", "snapshots"),
            SnapshotConcurrency = I("SNAPSHOT_CONCURRENCY", 8),
            MinioRegion = S("MINIO_REGION", "us-east-1"),
            ToolUrls = urls,
            ToolConcurrency = conc,
            ToolTimeoutMs = tmo,
        };
    }

    internal static string NormalizeEndpoint(string ep, string? useSsl)
    {
        ep = ep.TrimEnd('/');
        if (ep.Contains("://")) return ep;
        return (string.Equals(useSsl, "true", StringComparison.OrdinalIgnoreCase) ? "https://" : "http://") + ep;
    }
}
