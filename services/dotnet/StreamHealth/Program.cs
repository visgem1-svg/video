using Prometheus;
using StreamHealth;

if (args.Contains("--healthcheck"))
{
    try
    {
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:5002").Split(':').Last().TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        return (await http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

string? env(string k) => Environment.GetEnvironmentVariable(k);

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(env("ASPNETCORE_URLS"))) builder.WebHost.UseUrls("http://+:5002");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
});
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

var opts = new HealthOptions
{
    KafkaBootstrap = env("KAFKA_BOOTSTRAP") ?? "redpanda:9092",
    RegistryUrl = env("REGISTRY_URL"),
};
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HealthEngine>();
builder.Services.AddSingleton<ReadyState>();
builder.Services.AddSingleton<IHealthPublisher, KafkaHealthPublisher>();
builder.Services.AddHostedService<ConfigConsumer>();
builder.Services.AddHostedService<StatusConsumer>();
builder.Services.AddHostedService<HealthTicker>();
builder.Services.AddHostedService<RegistryPoller>();

var app = builder.Build();
app.UseHttpMetrics();
app.MapMetrics();
app.MapGet("/health", (ReadyState s) => (IResult)(s.Ready ? Results.Ok(new { status = "ok" }) : Results.Json(new { status = "starting" }, statusCode: 503)));
app.MapGet("/api/health/cameras", (HealthEngine e) => e.Snapshot());
app.MapGet("/api/health/summary", (HealthEngine e) => e.Summary());
app.MapGet("/api/health/cameras/{id}", (string id, HealthEngine e) => e.GetHealth(id) is { } h ? Results.Ok(h) : Results.NotFound());
await app.RunAsync();
return 0;

public partial class Program;
