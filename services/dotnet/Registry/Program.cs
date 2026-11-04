using Npgsql;
using Prometheus;
using Registry;
using Registry.Assignment;
using Registry.Data;

// Container healthcheck mode (chiseled image has no curl): `Registry --healthcheck`
if (args.Contains("--healthcheck"))
{
    try
    {
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:5001").Split(':').Last().TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        return (await http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))) builder.WebHost.UseUrls("http://+:5001");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
});
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

string? env(string k) => Environment.GetEnvironmentVariable(k);
var opts = new RegistryOptions
{
    KafkaBootstrap = env("KAFKA_BOOTSTRAP") ?? "redpanda:9092",
    CameraConfigTopic = env("TOPIC_CAMERA_CONFIG") ?? "camera.config",
    LeaseSeconds = int.TryParse(env("LEASE_S"), out var ls) ? ls : 15,
    SeedCameras = int.TryParse(env("SEED_CAMERAS"), out var sc) ? sc : 0,
    SeedRtspBase = env("SEED_RTSP_BASE") ?? "rtsp://mediamtx:8554",
    MediaMtxApiUrl = env("MEDIAMTX_API_URL") ?? "http://mediamtx:9997",
    DemandTopic = env("TOPIC_CAMERA_DEMAND") ?? "camera.demand",
};
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(opts));
var pg = env("POSTGRES_CONNECTION") ?? "Host=postgres;Port=5432;Database=registry;Username=vp;Password=vp";
builder.Services.AddSingleton(NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(pg) { MaxPoolSize = 10 }.ConnectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICameraStore, PgCameraStore>();
builder.Services.AddSingleton<IAssignmentStore, PgAssignmentStore>();
builder.Services.AddSingleton<ICameraPublisher, KafkaCameraPublisher>();
builder.Services.AddSingleton<IMediaMtxClient>(_ => new HttpMediaMtxClient(new HttpClient { BaseAddress = new Uri(opts.MediaMtxApiUrl), Timeout = TimeSpan.FromSeconds(5) }));
builder.Services.AddSingleton(sp => new ExtractModeEngine(sp.GetRequiredService<TimeProvider>(),
    idleWeight: double.TryParse(env("IDLE_WEIGHT"), System.Globalization.CultureInfo.InvariantCulture, out var iw) && iw is >= 0 and <= 1 ? iw : 0.1));
builder.Services.AddSingleton(sp => new CameraService(sp.GetRequiredService<ICameraStore>(), sp.GetRequiredService<ICameraPublisher>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IMediaMtxClient>(), sp.GetRequiredService<ILogger<CameraService>>(), opts.SeedRtspBase, sp.GetRequiredService<ExtractModeEngine>()));
builder.Services.AddSingleton(sp => new AssignmentService(sp.GetRequiredService<IAssignmentStore>(), sp.GetRequiredService<TimeProvider>(), opts.LeaseSeconds, sp.GetRequiredService<ExtractModeEngine>()));
builder.Services.AddSingleton<ReadyState>();
builder.Services.AddHostedService<StartupService>();
builder.Services.AddHostedService<LeaseSweeper>();
builder.Services.AddHostedService<DemandConsumer>();
builder.Services.AddHostedService<MediaMtxReconciler>();

var app = builder.Build();

// Schema migrations before serving (retry while Postgres boots).
{
    var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; ; attempt++)
    {
        try { await Migrator.RunAsync(ds, typeof(Program).Assembly, CancellationToken.None); break; }
        catch (Exception ex) when (attempt < 60)
        {
            log.LogWarning("migration attempt {Attempt} failed: {Error}", attempt, ex.Message);
            await Task.Delay(2000);
        }
    }
}

app.UseHttpMetrics();
app.MapMetrics();
app.MapGet("/health", (ReadyState s) => (IResult)(s.Ready ? Results.Ok(new { status = "ok" }) : Results.Json(new { status = "starting" }, statusCode: 503)));


app.MapGet("/api/cameras", (CameraService svc, CancellationToken ct) => svc.ListApiAsync(ct));
app.MapGet("/api/cameras/{id}", async (string id, CameraService svc, CancellationToken ct) =>
    await svc.GetAsync(id, ct) is { } c ? Results.Ok(c) : Results.NotFound());
app.MapPost("/api/cameras", async (CameraInput body, CameraService svc, CancellationToken ct) =>
{
    try
    {
        return await svc.CreateAsync(body, ct) is { } c ? Results.Created($"/api/cameras/{c.CameraId}", c) : Results.Conflict(new { error = "camera exists" });
    }
    catch (ValidationException e) { return Results.BadRequest(new { error = e.Message }); }
});
app.MapPut("/api/cameras/{id}", async (string id, CameraInput body, CameraService svc, CancellationToken ct) =>
{
    try { return await svc.UpdateAsync(id, body, ct) is { } c ? Results.Ok(c) : Results.NotFound(); }
    catch (ValidationException e) { return Results.BadRequest(new { error = e.Message }); }
});
app.MapDelete("/api/cameras/{id}", async (string id, CameraService svc, CancellationToken ct) =>
    await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

app.MapPost("/api/workers/{workerId}/heartbeat", async (string workerId, HeartbeatRequest body, AssignmentService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 128) return Results.BadRequest(new { error = "invalid worker_id" });
    if (body.Capacity is < 0 or > 100_000) return Results.BadRequest(new { error = "capacity out of range" });
    var assigned = await svc.HeartbeatAsync(workerId, body.Capacity, ct);
    RegistryMetrics.Heartbeats.Inc();
    return Results.Ok(new HeartbeatResponse(assigned, svc.LeaseSeconds));
});
app.MapGet("/api/workers", (AssignmentService svc, CancellationToken ct) => svc.ListWorkersAsync(ct));

// ---- contract 9.5 worker operations -------------------------------------------------------
app.MapPost("/api/workers/{workerId}/drain", async (string workerId, AssignmentService svc, CancellationToken ct) =>
    await svc.DrainAsync(workerId, true, ct) ? Results.Ok(new { worker_id = workerId, draining = true }) : Results.NotFound(new { error = "unknown worker" }));
app.MapPost("/api/workers/{workerId}/undrain", async (string workerId, AssignmentService svc, CancellationToken ct) =>
    await svc.DrainAsync(workerId, false, ct) ? Results.Ok(new { worker_id = workerId, draining = false }) : Results.NotFound(new { error = "unknown worker" }));
app.MapPut("/api/workers/{workerId}/capacity", async (string workerId, CapacityRequest body, AssignmentService svc, CancellationToken ct) =>
{
    if (body.Capacity is < 0 or > 100_000) return Results.BadRequest(new { error = "capacity out of range" });
    return await svc.SetCapacityOverrideAsync(workerId, body.Capacity, ct)
        ? Results.Ok(new { worker_id = workerId, capacity_override = body.Capacity }) : Results.NotFound(new { error = "unknown worker" });
});
app.MapPost("/api/cameras/{id}/pin", async (string id, PinRequest body, CameraService cams, AssignmentService assign, CancellationToken ct) =>
{
    try
    {
        if (await cams.PinAsync(id, body.WorkerId, ct) is null) return Results.NotFound();
        await assign.SweepAsync(ct); // apply now instead of waiting for the next heartbeat
        return Results.Ok(await cams.GetAsync(id, ct));
    }
    catch (ValidationException e) { return Results.BadRequest(new { error = e.Message }); }
});
app.MapPost("/api/cameras/{id}/restart", async (string id, CameraService cams, CancellationToken ct) =>
    await cams.RestartAsync(id, ct) is { } c ? Results.Ok(c) : Results.NotFound());

await app.RunAsync();
return 0;

public partial class Program;
