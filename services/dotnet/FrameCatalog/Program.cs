using FrameCatalog;
using Prometheus;
using R = Microsoft.AspNetCore.Http.Results;

if (args.Contains("--healthcheck"))
{
    try
    {
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:5006").Split(':').Last().TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        return (await http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

string? env(string k) => Environment.GetEnvironmentVariable(k);
int envInt(string k, int d) => int.TryParse(env(k), out var v) ? v : d;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(env("ASPNETCORE_URLS"))) builder.WebHost.UseUrls("http://+:5006");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
});
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

var opts = new CatalogOptions
{
    KafkaBootstrap = env("KAFKA_BOOTSTRAP") ?? "redpanda:9092",
    FramesPerCamera = Math.Clamp(envInt("FRAMES_PER_CAMERA", 600), 1, 100_000),
    ImageCachePerCamera = Math.Clamp(envInt("IMAGE_CACHE_PER_CAMERA", 3), 0, 50),
    FetchTimeoutMs = envInt("FETCH_TIMEOUT_MS", 2000),
    PendingTtlMs = envInt("PENDING_TTL_MS", 30_000),
    PendingMax = envInt("PENDING_MAX", 20_000),
};
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(sp => new Catalog(opts));
builder.Services.AddSingleton<IFrameFetcher, KafkaFrameFetcher>();
builder.Services.AddSingleton<ReadyState>();
builder.Services.AddHostedService<ConsumerService>();

var app = builder.Build();
var imageRequests = Metrics.CreateCounter("vp_catalog_image_requests_total", "Image requests", "result");
foreach (var r in new[] { "cache", "fetched", "expired", "unknown", "busy", "error" }) imageRequests.WithLabels(r);

app.UseHttpMetrics();
app.MapMetrics();
app.MapGet("/health", (ReadyState s) => (IResult)(s.Ready ? R.Ok(new { status = "ok" }) : R.Json(new { status = "starting" }, statusCode: 503)));

app.MapGet("/api/frames", (Catalog c, string? camera_id, long? before, int? limit) =>
    R.Ok(c.List(string.IsNullOrWhiteSpace(camera_id) ? null : camera_id, before, limit ?? 60)));

app.MapGet("/api/frames/latest", (Catalog c, string? camera_ids) =>
    R.Ok(c.Latest((camera_ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(1000))));

app.MapGet("/api/frames/stats", (Catalog c) => R.Ok(c.Stats()));

app.MapGet("/api/frames/{camera_id}/{frame_id}", (Catalog c, string camera_id, string frame_id) =>
    Guid.TryParse(frame_id, out var id) && c.Get(camera_id, id) is { } d ? R.Ok(d) : R.NotFound());

app.MapGet("/api/frames/{camera_id}/{frame_id}/image", async (Catalog c, IFrameFetcher fetcher, string camera_id, string frame_id, HttpContext http, CancellationToken ct) =>
{
    if (!Guid.TryParse(frame_id, out var id) || c.ImageRef(camera_id, id) is not { } loc)
    {
        imageRequests.WithLabels("unknown").Inc();
        return R.NotFound();
    }
    var jpg = loc.Cached;
    var result = "cache";
    if (jpg is null)
    {
        try
        {
            jpg = await fetcher.FetchAsync(loc.Partition, loc.Offset, ct);
            result = "fetched";
        }
        catch (OperationCanceledException) { throw; }
        catch (FetchBusyException) { imageRequests.WithLabels("busy").Inc(); return R.StatusCode(503); }
        catch (Exception ex)
        {
            imageRequests.WithLabels("error").Inc();
            app.Logger.LogWarning("image fetch failed: {Error}", ex.Message);
            return R.StatusCode(502);
        }
        if (jpg is null) { imageRequests.WithLabels("expired").Inc(); return R.NotFound(); }
    }
    imageRequests.WithLabels(result).Inc();
    http.Response.Headers.CacheControl = "private, max-age=600";
    return R.Bytes(jpg, "image/jpeg");
});

await app.RunAsync();
return 0;

public partial class Program;
