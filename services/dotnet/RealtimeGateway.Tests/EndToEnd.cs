using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RealtimeGateway;

namespace RealtimeGateway.Tests;

public class EndToEndTests
{
    [Fact] public async Task All_detections_at_3fps_reach_a_subscribed_client_through_real_signalr()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.AddSingleton<LiveState>();
        b.Services.AddSingleton(new DetectionThrottler<EventRouter.Boxed>(3));
        b.Services.AddSingleton<EventRouter>();
        b.Services.AddSignalR();
        b.Services.AddHostedService<ThrottleFlusher>();
        var app = b.Build();
        app.MapHub<LiveHub>("/hubs/live");
        await app.StartAsync();
        try
        {
            var got = new ConcurrentBag<string>();
            var conn = new HubConnectionBuilder().WithUrl(app.Urls.First() + "/hubs/live").Build();
            conn.On<System.Text.Json.JsonElement>("detections", e => got.Add(e.GetProperty("camera_id").GetString() + "#" + e.GetProperty("n").GetInt32()));
            await conn.StartAsync();
            var cams = Enumerable.Range(1, 9).Select(i => $"cam-{i:000}").ToArray();
            await conn.InvokeAsync("SubscribeCameras", (object)cams);

            var router = app.Services.GetRequiredService<EventRouter>();
            const int M = 12;
            for (var n = 0; n < M; n++)
            {
                foreach (var c in cams)
                    await router.OnDetectionAsync(c, Encoding.UTF8.GetBytes($"{{\"camera_id\":\"{c}\",\"n\":{n}}}"));
                await Task.Delay(300);
            }
            await Task.Delay(500);
            Assert.Equal(cams.Length * M, got.Count);
            await conn.DisposeAsync();
        }
        finally { await app.StopAsync(); }
    }
}
