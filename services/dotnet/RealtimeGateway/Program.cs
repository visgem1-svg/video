using System.Text.Json;
using Prometheus;
using RealtimeGateway;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ");
if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_HTTP_PORTS"]))
    builder.WebHost.UseUrls("http://+:5005");
builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(15));

var opts = GatewayOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<LiveState>();
builder.Services.AddSingleton(new DetectionThrottler<EventRouter.Boxed>(opts.MaxPerSecond));
builder.Services.AddSingleton<EventRouter>();

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 64 * 1024;
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
}).AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; // payloads are JsonElements anyway
    o.PayloadSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddHostedService<CompactedFeed>();
builder.Services.AddHostedService<LiveFeed>();
builder.Services.AddHostedService<ThrottleFlusher>();
builder.Services.AddHostedService<DemandPublisher>();
Telemetry.WatchedCameras.WithLabels("live"); Telemetry.WatchedCameras.WithLabels("frames");

foreach (var m in new[] { "detections", "recognition", "pipelineMetrics", "cameraHealth", "planChanged" }) Telemetry.Sent.WithLabels(m);
foreach (var r in new[] { "no_subscribers", "throttled", "parse_error" }) Telemetry.Dropped.WithLabels(r);
foreach (var t in new[] { "detections", "recognitions", "pipeline.metrics", "camera.health", "pipeline.plans" }) Telemetry.In.WithLabels(t);

var app = builder.Build();
var state = app.Services.GetRequiredService<LiveState>();
app.MapGet("/health", () => state.IsReady ? Results.Text("ok") : Results.Text("loading", statusCode: 503));
app.MapMetrics();
app.MapHub<LiveHub>("/hubs/live");
app.Run();
