using Microsoft.Extensions.Primitives;
using Prometheus;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace ApiGateway;

public sealed record GatewaySettings(
    string RegistryUrl, string StreamHealthUrl, string ResultsUrl, string PlannerUrl, string RealtimeUrl,
    string MediaUrl, string FaceUrl, string FrameCatalogUrl, string WorkerControllerUrl, string[] CorsOrigins)
{
    public const string MediaPrefix = "/media";

    public static GatewaySettings FromEnvironment(Func<string, string?> get)
    {
        string S(string k, string d) => string.IsNullOrWhiteSpace(get(k)) ? d : get(k)!.TrimEnd('/');
        return new GatewaySettings(
            S("REGISTRY_URL", "http://registry:5001"),
            S("STREAM_HEALTH_URL", "http://stream-health:5002"),
            S("RESULTS_URL", "http://results:5004"),
            S("PLANNER_URL", "http://planner:8010"),
            S("REALTIME_GATEWAY_URL", "http://realtime-gateway:5005"),
            S("MEDIAMTX_WHEP_URL", "http://mediamtx:8889"),
            S("FACE_URL", "http://face:8104"),
            S("FRAME_CATALOG_URL", "http://frame-catalog:5006"),
            S("WORKER_CONTROLLER_URL", "http://worker-controller:5007"),
            (string.IsNullOrWhiteSpace(get("CORS_ORIGINS")) ? "http://localhost:5173" : get("CORS_ORIGINS")!)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}

/// <summary>Route table of contract 7.6.</summary>
public static class Routes
{
    public static readonly TimeSpan PlannerActivityTimeout = TimeSpan.FromMinutes(6);

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(GatewaySettings s)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        void Cluster(string id, string url) => clusters.Add(new ClusterConfig
        {
            ClusterId = id,
            // planner drafts (local CPU LLM) take 2-3 min; YARP's default activity timeout is 100 s. Planner only.
            HttpRequest = id == "planner" ? new ForwarderRequestConfig { ActivityTimeout = PlannerActivityTimeout } : null,
            Destinations = new Dictionary<string, DestinationConfig> { ["d1"] = new() { Address = url + "/" } },
        });

        void Route(string id, string cluster, string path) => routes.Add(new RouteConfig
        {
            RouteId = id,
            ClusterId = cluster,
            Match = new RouteMatch { Path = path },
        });

        Cluster("registry", s.RegistryUrl);
        Cluster("stream-health", s.StreamHealthUrl);
        Cluster("results", s.ResultsUrl);
        Cluster("planner", s.PlannerUrl);
        Cluster("realtime", s.RealtimeUrl);
        Cluster("mediamtx", s.MediaUrl);
        Cluster("face", s.FaceUrl);
        Cluster("frame-catalog", s.FrameCatalogUrl);
        Cluster("worker-controller", s.WorkerControllerUrl);

        Route("cameras", "registry", "/api/cameras/{**rest}");
        Route("workers", "registry", "/api/workers/{**rest}");
        Route("health", "stream-health", "/api/health/{**rest}");
        Route("recognitions", "results", "/api/recognitions/{**rest}");
        Route("detections", "results", "/api/detections/{**rest}");
        Route("snapshots", "results", "/api/snapshots/{**rest}");
        Route("plans", "planner", "/api/plans/{**rest}");
        Route("tools", "planner", "/api/tools/{**rest}");
        Route("hubs", "realtime", "/hubs/{**rest}");   // SignalR: negotiate (POST) + WebSocket upgrade; YARP proxies upgrades natively

        routes.Add(new RouteConfig
        {
            RouteId = "media",
            ClusterId = "mediamtx",
            Match = new RouteMatch { Path = GatewaySettings.MediaPrefix + "/{**rest}" },
            Transforms = new[] { new Dictionary<string, string> { ["PathRemovePrefix"] = GatewaySettings.MediaPrefix } },
        });

        // Known-people API (contract 9.4): /api/persons/** -> face:/persons/** (the /api prefix is removed).
        // Kestrel's default request body limit (30 MB) covers multipart enrolment uploads; the gateway sets none lower.
        void StrippedRoute(string id, string path) => routes.Add(new RouteConfig
        {
            RouteId = id,
            ClusterId = "face",
            Match = new RouteMatch { Path = path },
            Transforms = new[] { new Dictionary<string, string> { ["PathRemovePrefix"] = "/api" } },
        });
        StrippedRoute("persons", "/api/persons/{**rest}");
        StrippedRoute("persons-root", "/api/persons");

        // Frame catalog (contract 9.5): /api/frames/** -> frame-catalog:/api/frames/** (no path transform; image responses stream).
        Route("frames", "frame-catalog", "/api/frames/{**rest}");
        Route("frames-root", "frame-catalog", "/api/frames");

        // Worker controller / autoscaler (contract 9.5): /api/autoscaler/** -> worker-controller:/api/autoscaler/** (no path transform).
        Route("autoscaler", "worker-controller", "/api/autoscaler/{**rest}");
        Route("autoscaler-root", "worker-controller", "/api/autoscaler");
        return (routes, clusters);
    }
}

public static class ResponseFixes
{
    /// <summary>
    /// MediaMTX answers WHEP POST with 201 + Location (relative "/cam/whep/id" or absolute on its own host).
    /// The browser must keep talking to the gateway for PATCH/DELETE, so the location is moved under /media.
    /// </summary>
    public static string RewriteLocation(string location, string upstreamBase)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var abs))
        {
            var up = new Uri(upstreamBase);
            return abs.Authority.Equals(up.Authority, StringComparison.OrdinalIgnoreCase)
                ? GatewaySettings.MediaPrefix + abs.PathAndQuery
                : location;
        }
        return location.StartsWith('/') ? GatewaySettings.MediaPrefix + location : location; // "whep/id" resolves under /media/... already
    }

    /// <summary>Upstreams (MediaMTX) send their own CORS headers; replace them with the gateway's single policy.</summary>
    public static void NormalizeCors(HttpContext ctx, string[] allowedOrigins)
    {
        var h = ctx.Response.Headers;
        h.Remove("Access-Control-Allow-Origin");
        h.Remove("Access-Control-Allow-Headers");
        h.Remove("Access-Control-Allow-Methods");
        h.Remove("Access-Control-Allow-Credentials");
        h.Remove("Access-Control-Expose-Headers");
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            h["Access-Control-Allow-Origin"] = origin;
            h["Access-Control-Allow-Credentials"] = "true";
            h["Access-Control-Expose-Headers"] = "Location, Link, ETag, Accept-Patch";
            h.Append("Vary", "Origin");
        }
    }
}

public static class GatewayApp
{
    public const string CorsPolicy = "ui";

    public static WebApplication Create(string[] args, Func<string, string?> env)
    {
        var s = GatewaySettings.FromEnvironment(env);
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ");
        if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]) && string.IsNullOrEmpty(builder.Configuration["urls"])
            && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_HTTP_PORTS"]))
            builder.WebHost.UseUrls("http://+:8000");
        builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(10));

        builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
            .WithOrigins(s.CorsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()                 // includes PATCH / DELETE used by WHEP
            .AllowCredentials()               // SignalR negotiate sends credentials
            .WithExposedHeaders("Location", "Link", "ETag", "Accept-Patch")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        var (routes, clusters) = Routes.Build(s);
        builder.Services.AddReverseProxy()
            .LoadFromMemory(routes, clusters)
            .ConfigureHttpClient((ctx, h) =>
            {
                h.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
                h.ConnectTimeout = TimeSpan.FromSeconds(3);
            })
            .AddTransforms(t =>
            {
                var isMedia = t.Route.RouteId == "media";
                t.AddResponseTransform(ctx =>
                {
                    ResponseFixes.NormalizeCors(ctx.HttpContext, s.CorsOrigins);
                    if (isMedia && ctx.HttpContext.Response.Headers.TryGetValue("Location", out var loc) && loc.Count > 0)
                        ctx.HttpContext.Response.Headers["Location"] = new StringValues(ResponseFixes.RewriteLocation(loc[0]!, s.MediaUrl));
                    return ValueTask.CompletedTask;
                });
            });

        var app = builder.Build();
        app.UseCors(CorsPolicy);
        app.UseWebSockets();
        app.MapGet("/health", () => Results.Text("ok"));     // gateway itself only
        app.MapMetrics();
        app.MapReverseProxy();
        return app;
    }
}
