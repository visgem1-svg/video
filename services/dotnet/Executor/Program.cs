using System.Text.Json;
using Confluent.Kafka;
using Executor;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ");
if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_HTTP_PORTS"]))
    builder.WebHost.UseUrls("http://+:5003");
builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(30));

var opts = ExecutorOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<PlanStore>();
builder.Services.AddSingleton<StepStats>();
builder.Services.AddSingleton<DemandStore>();

// One named client per tool: pooled connections + own retry / circuit breaker / attempt timeout.
foreach (var t in Tools.All)
{
    var attempt = TimeSpan.FromMilliseconds(opts.ToolTimeoutMs[t.Name]);
    var conc = opts.ToolConcurrency[t.Name];
    builder.Services.AddHttpClient(HttpToolInvoker.ClientName(t.Name))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = conc + 2,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        .AddResilienceHandler("tool-" + t.Name, p =>
        {
            p.AddTimeout(new HttpTimeoutStrategyOptions { Timeout = attempt * (opts.ToolRetries + 1) + TimeSpan.FromSeconds(1) });
            if (opts.ToolRetries > 0)
                p.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = opts.ToolRetries,
                    Delay = TimeSpan.FromMilliseconds(100),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                });
            p.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                // 429 is engine back-pressure, not a broken engine: only exceptions/timeouts and 5xx trip the breaker.
                ShouldHandle = args => ValueTask.FromResult(
                    ToolResilience.TripsBreaker(args.Outcome.Exception, args.Outcome.Result?.StatusCode)),
                SamplingDuration = TimeSpan.FromSeconds(Math.Max(10, attempt.TotalSeconds * 2)),
                FailureRatio = 0.5,
                MinimumThroughput = 20,
                BreakDuration = TimeSpan.FromSeconds(5),
            });
            p.AddTimeout(new HttpTimeoutStrategyOptions { Timeout = attempt });
        });
}
builder.Services.AddHttpClient(S3SnapshotStore.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = opts.SnapshotConcurrency + 2,
    })
    .AddResilienceHandler("minio", p => p.AddTimeout(new HttpTimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(5) })); // the single retry is in FramePipeline

builder.Services.AddSingleton<ISnapshotStore, S3SnapshotStore>();
builder.Services.AddSingleton(sp => new ToolGate(opts.ToolConcurrency));
builder.Services.AddSingleton<IToolInvoker>(sp => new LimitedToolInvoker(
    new HttpToolInvoker(sp.GetRequiredService<IHttpClientFactory>(), opts.ToolUrls), sp.GetRequiredService<ToolGate>()));
builder.Services.AddSingleton<KafkaSink>();
builder.Services.AddSingleton<IResultSink>(sp => sp.GetRequiredService<KafkaSink>());
builder.Services.AddSingleton<PlanRunner>();
builder.Services.AddSingleton(sp => new FramePipeline(sp.GetRequiredService<PlanStore>(), sp.GetRequiredService<ISnapshotStore>(),
    sp.GetRequiredService<PlanRunner>(), sp.GetRequiredService<IResultSink>(), opts,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("pipeline"),
    isWatched: sp.GetRequiredService<DemandStore>().IsWatched));

builder.Services.AddHostedService<CompactedStateService>();
builder.Services.AddHostedService<MetricsFlushService>();
builder.Services.AddHostedService(sp =>
{
    var pipe = sp.GetRequiredService<FramePipeline>();
    var demand = sp.GetRequiredService<DemandStore>();
    return new KafkaWorkService<FrameIn>(Topics.FramesRaw, "executor", opts, sp.GetRequiredService<PlanStore>(),
        (value, headers) =>
        {
            // contract 9.7: key = camera_id, value = JPEG, header "meta" = frame metadata JSON
            if (!headers.TryGetLastBytes("meta", out var meta)) return null;
            var m = JsonSerializer.Deserialize(meta, Json.Default.FrameMsg);
            return m is null || string.IsNullOrEmpty(m.CameraId) ? null : new FrameIn(m, value);
        },
        f => f.Meta.Ts,
        async (f, ct) => { try { await pipe.ProcessAsync(f, ct); } finally { f.Dispose(); } },
        opts.MaxInflightFrames, opts.MaxFrameAgeMs,
        reason => Telemetry.FramesSkipped.WithLabels(reason).Inc(), n => Telemetry.Inflight.Set(n),
        AutoOffsetReset.Latest, sp.GetRequiredService<ILoggerFactory>().CreateLogger("frames"),
        keyOf: f => f.Meta.CameraId, // conflation: newest frame per camera only
        onDiscard: f => f.Dispose(),
        isWatched: demand.IsWatched,
        onDequeue: (f, ageMs) => Telemetry.DispatchAge.WithLabels(demand.IsWatched(f.Meta.CameraId) ? "watched" : "background").Observe(ageMs / 1000.0));
});
builder.Services.AddHostedService(sp =>
{
    var pipe = sp.GetRequiredService<FramePipeline>();
    return new KafkaWorkService<JobIn>(Topics.JobsHeavy, "executor-heavy", opts, sp.GetRequiredService<PlanStore>(),
        (value, headers) =>
        {
            if (!headers.TryGetLastBytes(FramePipeline.JobHeader, out var jb)) return null;
            var j = JsonSerializer.Deserialize(jb, Json.Default.JobMsg);
            return j is null ? null : new JobIn(j, value);
        },
        j => j.Job.Frame.Ts,
        async (j, ct) => { try { await pipe.ProcessJobAsync(j, ct); } finally { j.Dispose(); } },
        opts.HeavyConcurrency, opts.HeavyMaxAgeMs,
        reason => Telemetry.Jobs.WithLabels("skipped_" + reason).Inc(), n => Telemetry.HeavyInflight.Set(n),
        AutoOffsetReset.Earliest, sp.GetRequiredService<ILoggerFactory>().CreateLogger("heavy"),
        onDiscard: j => j.Dispose());
});

var app = builder.Build();
var store = app.Services.GetRequiredService<PlanStore>();
app.MapGet("/health", () => store.IsReady ? Results.Text("ok") : Results.Text("loading", statusCode: 503));
app.MapMetrics();
app.Run();
