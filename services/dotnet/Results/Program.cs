using Npgsql;
using Prometheus;
using Results;
using R = Microsoft.AspNetCore.Http.Results;

if (args.Contains("--healthcheck"))
{
    try
    {
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:5004").Split(':').Last().TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        return (await http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

string? env(string k) => Environment.GetEnvironmentVariable(k);
int envInt(string k, int d) => int.TryParse(env(k), out var v) ? v : d;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(env("ASPNETCORE_URLS"))) builder.WebHost.UseUrls("http://+:5004");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
});
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));

var opts = new ResultsOptions
{
    KafkaBootstrap = env("KAFKA_BOOTSTRAP") ?? "redpanda:9092",
    MinioEndpoint = env("MINIO_ENDPOINT") ?? "minio:9000",
    MinioAccessKey = env("MINIO_ACCESS_KEY") ?? "minio",
    MinioSecretKey = env("MINIO_SECRET_KEY") ?? "minio12345",
    MinioUseSsl = string.Equals(env("MINIO_USE_SSL"), "true", StringComparison.OrdinalIgnoreCase),
    SnapshotsBucket = env("MINIO_SNAPSHOTS_BUCKET") ?? "snapshots",
    BatchMax = envInt("BATCH_MAX", 500),
    BatchFlushMs = envInt("BATCH_FLUSH_MS", 500),
    RecognitionRetentionDays = envInt("RECOGNITION_RETENTION_DAYS", 7),
};
var pg = env("POSTGRES_CONNECTION") ?? "Host=postgres;Port=5432;Database=results;Username=vp;Password=vp;Maximum Pool Size=10";
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(NpgsqlDataSource.Create(pg));
builder.Services.AddSingleton<IResultsStore, PgResultsStore>();
builder.Services.AddSingleton<MinioSnapshotStore>();
builder.Services.AddSingleton<ISnapshotStore>(sp => sp.GetRequiredService<MinioSnapshotStore>());
builder.Services.AddSingleton(sp => new ResultsBatcher(sp.GetRequiredService<IResultsStore>(),
    sp.GetRequiredService<ILogger<ResultsBatcher>>(), opts.DetectionsTopic));
builder.Services.AddSingleton<ReadyState>();
builder.Services.AddHostedService<ConsumerService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RetentionJob>();
builder.Services.AddHostedService<RetentionService>();

var app = builder.Build();
{
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
    for (var attempt = 1; ; attempt++)
    {
        try { await Migrator.RunAsync(ds, typeof(Program).Assembly, CancellationToken.None); break; }
        catch (Exception ex) when (attempt < 60) { log.LogWarning("migration attempt {Attempt} failed: {Error}", attempt, ex.Message); await Task.Delay(2000); }
    }
    try { await app.Services.GetRequiredService<MinioSnapshotStore>().EnsureBucketAsync(CancellationToken.None); }
    catch (Exception ex) { log.LogWarning("snapshots bucket check failed: {Error}", ex.Message); }
}

app.UseHttpMetrics();
app.MapMetrics();
app.MapGet("/health", (ReadyState s) => (IResult)(s.Ready ? R.Ok(new { status = "ok" }) : R.Json(new { status = "starting" }, statusCode: 503)));

app.MapGet("/api/recognitions", async (IResultsStore store, string? camera_id, string? type, long? since, int? limit, string? person_id, CancellationToken ct) =>
    R.Ok(await store.QueryRecognitionsAsync(camera_id, type, since, Math.Clamp(limit ?? 50, 1, 500), ct, string.IsNullOrWhiteSpace(person_id) ? null : person_id)));

app.MapGet("/api/detections/stats", async (IResultsStore store, string? camera_id, int? minutes, CancellationToken ct) =>
{
    var from = DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(minutes ?? 60, 1, 24 * 60)).ToUnixTimeMilliseconds() / 60_000 * 60_000;
    return R.Ok(await store.QueryStatsAsync(camera_id, from, ct));
});

app.MapGet("/api/snapshots/{type}/{date}/{file}", async (string type, string date, string file, ISnapshotStore snaps, HttpContext http, CancellationToken ct) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(type, "^[a-z]{1,16}$") || !System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{8}$")
        || !System.Text.RegularExpressions.Regex.IsMatch(file, @"^[A-Za-z0-9_-]{1,64}\.jpg$"))
        return R.BadRequest();
    http.Response.ContentType = "image/jpeg";
    http.Response.Headers.CacheControl = "public, max-age=31536000, immutable"; // snapshots are immutable
    if (await snaps.ReadAsync($"{type}/{date}/{file}", http.Response.Body, ct)) return R.Empty;
    http.Response.Headers.CacheControl = "no-store";
    return R.NotFound();
});

await app.RunAsync();
return 0;

public partial class Program;
