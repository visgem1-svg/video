using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Executor;

public interface IToolInvoker
{
    /// <summary>Calls an engine. Throws on failure (the caller marks the step as failed).</summary>
    ValueTask<StepResult> InvokeAsync(string tool, IReadOnlyDictionary<string, JsonElement>? parameters,
        ReadOnlyMemory<byte> image, CancellationToken ct);
}

/// <summary>Per-tool concurrency limits: protects the engines (CPU bound) and keeps slow tools from hogging all slots.</summary>
public sealed class ToolGate
{
    private readonly Dictionary<string, SemaphoreSlim> _sems = new();
    private readonly int _fallback;

    public ToolGate(IReadOnlyDictionary<string, int> limits, int fallback = 4)
    {
        _fallback = fallback;
        foreach (var (k, v) in limits) _sems[k] = new SemaphoreSlim(v, v);
    }

    public SemaphoreSlim For(string tool) => _sems.TryGetValue(tool, out var s) ? s : throw new InvalidOperationException("unknown tool " + tool);
}

public sealed class LimitedToolInvoker : IToolInvoker
{
    private readonly IToolInvoker _inner;
    private readonly ToolGate _gate;
    private readonly Dictionary<string, Prometheus.Histogram.Child> _wait = new();

    public LimitedToolInvoker(IToolInvoker inner, ToolGate gate)
    {
        _inner = inner;
        _gate = gate;
        foreach (var t in Tools.All) _wait[t.Name] = Telemetry.ToolWait.WithLabels(t.Name);
    }

    public async ValueTask<StepResult> InvokeAsync(string tool, IReadOnlyDictionary<string, JsonElement>? parameters,
        ReadOnlyMemory<byte> image, CancellationToken ct)
    {
        var sem = _gate.For(tool);
        var t0 = Stopwatch.GetTimestamp();
        await sem.WaitAsync(ct).ConfigureAwait(false);
        if (_wait.TryGetValue(tool, out var h)) h.Observe(Stopwatch.GetElapsedTime(t0).TotalSeconds);
        try
        {
            return await _inner.InvokeAsync(tool, parameters, image, ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }
}

/// <summary>
/// HTTP implementation. One named HttpClient per tool (pooled connections; the resilience pipeline - attempt timeout,
/// retry, circuit breaker - is configured per client in Program.cs, so each tool has its own breaker).
/// </summary>
public sealed class HttpToolInvoker : IToolInvoker
{
    private readonly IHttpClientFactory _factory;
    private readonly IReadOnlyDictionary<string, string> _urls;
    private readonly Dictionary<string, Prometheus.Histogram.Child> _hist = new();

    public HttpToolInvoker(IHttpClientFactory factory, IReadOnlyDictionary<string, string> urls)
    {
        _factory = factory;
        _urls = urls;
        foreach (var t in Tools.All) _hist[t.Name] = Telemetry.StepDuration.WithLabels(t.Name);
    }

    public static string ClientName(string tool) => "tool:" + tool;

    public async ValueTask<StepResult> InvokeAsync(string tool, IReadOnlyDictionary<string, JsonElement>? parameters,
        ReadOnlyMemory<byte> image, CancellationToken ct)
    {
        if (!_urls.TryGetValue(tool, out var url)) throw new InvalidOperationException("unknown tool " + tool);
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            using var form = new MultipartFormDataContent();
            var img = new ReadOnlyMemoryContent(image); // no copy: the pooled buffer outlives the call
            img.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(img, "image", "frame.jpg");
            if (parameters is not null)
                foreach (var (k, v) in parameters)
                    form.Add(new StringContent(v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()), k);

            using var resp = await _factory.CreateClient(ClientName(tool))
                .PostAsync(url, form, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await ParseAsync(tool, body, ct).ConfigureAwait(false);
        }
        finally
        {
            if (_hist.TryGetValue(tool, out var h)) h.Observe(Stopwatch.GetElapsedTime(t0).TotalSeconds);
        }
    }

    internal static async ValueTask<StepResult> ParseAsync(string tool, Stream body, CancellationToken ct)
    {
        switch (tool)
        {
            case Tools.DetectObjects:
            {
                var r = await JsonSerializer.DeserializeAsync(body, Json.Default.ObjectsResp, ct).ConfigureAwait(false);
                return new StepResult(StepStatus.Ok, Objects: r?.Objects ?? new());
            }
            case Tools.ReadPlate:
            {
                var r = await JsonSerializer.DeserializeAsync(body, Json.Default.PlatesResp, ct).ConfigureAwait(false);
                return new StepResult(StepStatus.Ok, Plates: r?.Plates ?? new());
            }
            case Tools.ReadText:
            {
                var r = await JsonSerializer.DeserializeAsync(body, Json.Default.OcrResp, ct).ConfigureAwait(false);
                return new StepResult(StepStatus.Ok, Texts: r?.Lines ?? new());
            }
            case Tools.DetectFaces:
            {
                var r = await JsonSerializer.DeserializeAsync(body, Json.Default.FacesResp, ct).ConfigureAwait(false);
                // detect_faces has no identity: normalise so the output never carries a stale identity
                var faces = (r?.Faces ?? new()).Select(f => new FaceDet(f.Box, f.Confidence, null, null, null)).ToList();
                return new StepResult(StepStatus.Ok, Faces: faces);
            }
            case Tools.IdentifyFaces:
            {
                var r = await JsonSerializer.DeserializeAsync(body, Json.Default.FacesResp, ct).ConfigureAwait(false);
                return new StepResult(StepStatus.Ok, Faces: r?.Faces ?? new());
            }
            default:
                throw new InvalidOperationException("unknown tool " + tool);
        }
    }
}
