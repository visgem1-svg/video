using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ApiGateway;
using Yarp.ReverseProxy.Configuration;

namespace ApiGateway.Tests;

public class RouteTableTests
{
    static (IReadOnlyList<RouteConfig> R, IReadOnlyList<ClusterConfig> C) Build() =>
        Routes.Build(GatewaySettings.FromEnvironment(_ => null));

    static string Dest(IReadOnlyList<ClusterConfig> c, string id) => c.Single(x => x.ClusterId == id).Destinations!["d1"].Address;

    [Theory]
    [InlineData("/api/cameras/{**rest}", "registry", "http://registry:5001/")]
    [InlineData("/api/workers/{**rest}", "registry", "http://registry:5001/")]
    [InlineData("/api/health/{**rest}", "stream-health", "http://stream-health:5002/")]
    [InlineData("/api/recognitions/{**rest}", "results", "http://results:5004/")]
    [InlineData("/api/detections/{**rest}", "results", "http://results:5004/")]
    [InlineData("/api/snapshots/{**rest}", "results", "http://results:5004/")]
    [InlineData("/api/plans/{**rest}", "planner", "http://planner:8010/")]
    [InlineData("/api/tools/{**rest}", "planner", "http://planner:8010/")]
    [InlineData("/hubs/{**rest}", "realtime", "http://realtime-gateway:5005/")]
    [InlineData("/media/{**rest}", "mediamtx", "http://mediamtx:8889/")]
    [InlineData("/api/persons/{**rest}", "face", "http://face:8104/")]
    [InlineData("/api/persons", "face", "http://face:8104/")]
    [InlineData("/api/frames/{**rest}", "frame-catalog", "http://frame-catalog:5006/")]
    [InlineData("/api/frames", "frame-catalog", "http://frame-catalog:5006/")]
    [InlineData("/api/autoscaler/{**rest}", "worker-controller", "http://worker-controller:5007/")]
    [InlineData("/api/autoscaler", "worker-controller", "http://worker-controller:5007/")]
    public void Route_goes_to_contract_7_6_upstream(string path, string cluster, string address)
    {
        var (r, c) = Build();
        var route = r.Single(x => x.Match.Path == path);
        Assert.Equal(cluster, route.ClusterId);
        Assert.Equal(address, Dest(c, cluster));
    }

    [Fact] public void Exactly_the_contract_routes_exist_and_media_strips_prefix()
    {
        var (r, _) = Build();
        Assert.Equal(16, r.Count);
        var media = r.Single(x => x.RouteId == "media");
        Assert.Equal("/media", media.Transforms!.Single()["PathRemovePrefix"]);
    }

    [Theory]
    [InlineData("/api/persons/{**rest}")]
    [InlineData("/api/persons")]
    public void Persons_routes_strip_api_prefix_and_use_face_cluster(string path)
    {
        var (r, _) = Build();
        var route = r.Single(x => x.Match.Path == path);
        Assert.Equal("face", route.ClusterId);
        Assert.Equal("/api", route.Transforms!.Single()["PathRemovePrefix"]);
    }

    [Fact] public void Api_routes_other_than_persons_do_not_strip_prefix()
    {
        var (r, _) = Build();
        Assert.All(r.Where(x => x.Match.Path!.StartsWith("/api/") && !x.Match.Path.StartsWith("/api/persons")),
            x => Assert.Null(x.Transforms));
    }

    [Fact] public void Only_planner_cluster_has_long_activity_timeout()
    {
        var (_, c) = Build();
        Assert.Equal(TimeSpan.FromMinutes(6), c.Single(x => x.ClusterId == "planner").HttpRequest!.ActivityTimeout);
        Assert.All(c.Where(x => x.ClusterId != "planner"), x => Assert.Null(x.HttpRequest));
    }

    [Fact] public void Upstreams_and_cors_come_from_env_md_names()
    {
        var env = new Dictionary<string, string>
        {
            ["REGISTRY_URL"] = "http://r:1/", ["STREAM_HEALTH_URL"] = "http://s:2", ["RESULTS_URL"] = "http://x:3", ["PLANNER_URL"] = "http://p:4",
            ["REALTIME_GATEWAY_URL"] = "http://rt:5", ["MEDIAMTX_WHEP_URL"] = "http://m:6", ["FACE_URL"] = "http://f:7/", ["CORS_ORIGINS"] = "http://a.com, http://b.com",
        };
        var s = GatewaySettings.FromEnvironment(k => env.GetValueOrDefault(k));
        var (_, c) = Routes.Build(s);
        Assert.Equal("http://r:1/", Dest(c, "registry"));
        Assert.Equal("http://rt:5/", Dest(c, "realtime"));
        Assert.Equal("http://m:6/", Dest(c, "mediamtx"));
        Assert.Equal("http://f:7/", Dest(c, "face"));
        Assert.Equal(new[] { "http://a.com", "http://b.com" }, s.CorsOrigins);
    }

    [Fact] public void Face_defaults_to_compose_service_name()
    {
        var (_, c) = Build();
        Assert.Equal("http://face:8104/", Dest(c, "face"));
    }

    [Fact] public void Frame_catalog_and_worker_controller_defaults_and_env_overrides()
    {
        var (_, c) = Build();
        Assert.Equal("http://frame-catalog:5006/", Dest(c, "frame-catalog"));
        Assert.Equal("http://worker-controller:5007/", Dest(c, "worker-controller"));

        var env = new Dictionary<string, string>
        {
            ["FRAME_CATALOG_URL"] = "http://fc:9/", ["WORKER_CONTROLLER_URL"] = "http://wc:8/",
        };
        var (_, c2) = Routes.Build(GatewaySettings.FromEnvironment(k => env.GetValueOrDefault(k)));
        Assert.Equal("http://fc:9/", Dest(c2, "frame-catalog"));
        Assert.Equal("http://wc:8/", Dest(c2, "worker-controller"));
    }

    [Theory]
    [InlineData("/api/frames/{**rest}")]
    [InlineData("/api/frames")]
    [InlineData("/api/autoscaler/{**rest}")]
    [InlineData("/api/autoscaler")]
    public void Frames_and_autoscaler_routes_do_not_transform_path(string path)
    {
        var (r, _) = Build();
        var route = r.Single(x => x.Match.Path == path);
        Assert.Null(route.Transforms);
    }

    [Theory]
    [InlineData("/cam-001/whep/abc", "/media/cam-001/whep/abc")]
    [InlineData("http://mediamtx:8889/cam-001/whep/abc", "/media/cam-001/whep/abc")]
    [InlineData("whep/abc", "whep/abc")]
    [InlineData("http://other:1/x", "http://other:1/x")]
    public void Location_is_rewritten_under_media(string input, string expected) =>
        Assert.Equal(expected, ResponseFixes.RewriteLocation(input, "http://mediamtx:8889"));
}

public class EndToEndTests
{
    [Fact] public async Task Proxies_strips_media_prefix_rewrites_location_and_applies_cors()
    {
        var upBuilder = WebApplication.CreateBuilder();
        upBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        var up = upBuilder.Build();
        var seen = new List<string>();
        up.MapMethods("/{**p}", new[] { "GET", "POST", "PATCH", "DELETE" }, (HttpContext c) =>
        {
            seen.Add(c.Request.Method + " " + c.Request.Path);
            c.Response.Headers["Access-Control-Allow-Origin"] = "*";
            if (c.Request.Method == "POST") { c.Response.StatusCode = 201; c.Response.Headers.Location = "/cam-001/whep/sess1"; }
            return Task.CompletedTask;
        });
        await up.StartAsync();
        var upUrl = up.Urls.First();

        var env = new Dictionary<string, string>
        {
            ["REGISTRY_URL"] = upUrl, ["STREAM_HEALTH_URL"] = upUrl, ["RESULTS_URL"] = upUrl, ["PLANNER_URL"] = upUrl,
            ["REALTIME_GATEWAY_URL"] = upUrl, ["MEDIAMTX_WHEP_URL"] = upUrl,
        };
        var gw = GatewayApp.Create(new[] { "--urls", "http://127.0.0.1:0" }, k => env.GetValueOrDefault(k));
        await gw.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(gw.Urls.First()) };

            var health = await http.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var post = new HttpRequestMessage(HttpMethod.Post, "/media/cam-001/whep") { Content = new StringContent("v=0", System.Text.Encoding.UTF8, "application/sdp") };
            post.Headers.Add("Origin", "http://localhost:5173");
            var resp = await http.SendAsync(post);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            Assert.Equal("/media/cam-001/whep/sess1", resp.Headers.Location!.OriginalString);
            Assert.Equal("http://localhost:5173", resp.Headers.GetValues("Access-Control-Allow-Origin").Single());

            var del = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/media/cam-001/whep/sess1"));
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/cameras")).StatusCode);
            await http.GetAsync("/api/plans/abc");
            await http.GetAsync("/api/tools");

            Assert.Contains("POST /cam-001/whep", seen);          // prefix removed
            Assert.Contains("DELETE /cam-001/whep/sess1", seen);
            Assert.Contains("GET /api/cameras", seen);             // /api paths are NOT stripped
            Assert.Contains("GET /api/tools", seen);

            var pre = new HttpRequestMessage(HttpMethod.Options, "/media/cam-001/whep/sess1");
            pre.Headers.Add("Origin", "http://localhost:5173");
            pre.Headers.Add("Access-Control-Request-Method", "PATCH");
            var pr = await http.SendAsync(pre);
            Assert.Equal("http://localhost:5173", pr.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("PATCH", string.Join(",", pr.Headers.GetValues("Access-Control-Allow-Methods")));

            var evil = new HttpRequestMessage(HttpMethod.Get, "/api/cameras");
            evil.Headers.Add("Origin", "http://evil.example");
            Assert.False((await http.SendAsync(evil)).Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            await gw.StopAsync();
            await up.StopAsync();
        }
    }
}
