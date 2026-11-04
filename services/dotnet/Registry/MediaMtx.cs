using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Registry;

/// <summary>A configured MediaMTX path as seen by reconcile.</summary>
public sealed record MediaMtxPath(string? Source, bool OnDemand, string? StartTimeout, string? CloseAfter)
{
    /// <summary>True when the path already has the pulled-path on-demand settings of contract 9.9.</summary>
    public bool IsOnDemandPull => OnDemand && StartTimeout == HttpMediaMtxClient.StartTimeout && CloseAfter == HttpMediaMtxClient.CloseAfter;
}

/// <summary>
/// MediaMTX path provisioning (contracts 9.3 / 9.9). All methods throw on failure. Only pulled paths are ever provisioned,
/// so Add/Patch always write the on-demand pull settings. Implementations must never put the source (it may carry
/// credentials) into exception messages.
/// </summary>
public interface IMediaMtxClient
{
    /// <summary>name -> configured path, for every configured path.</summary>
    Task<IReadOnlyDictionary<string, MediaMtxPath>> ListPathsAsync(CancellationToken ct);
    /// <summary>Number of readers currently attached to the path (0 when it is not running).</summary>
    Task<int> ReaderCountAsync(string name, CancellationToken ct);
    /// <summary>POST /v3/config/paths/add/{name}; if the path already exists it is patched instead.</summary>
    Task AddAsync(string name, string source, CancellationToken ct);
    /// <summary>PATCH /v3/config/paths/patch/{name}; if the path does not exist it is added instead.</summary>
    Task PatchAsync(string name, string source, CancellationToken ct);
    /// <summary>DELETE /v3/config/paths/delete/{name}; 404 is success.</summary>
    Task DeleteAsync(string name, CancellationToken ct);
}

public sealed class HttpMediaMtxClient(HttpClient http) : IMediaMtxClient
{
    public const string StartTimeout = "10s";
    public const string CloseAfter = "30s";

    private static object Body(string source) => new
    {
        source, sourceOnDemand = true, sourceOnDemandStartTimeout = StartTimeout, sourceOnDemandCloseAfter = CloseAfter, rtspTransport = "tcp",
    };

    private static string? Str(JsonElement item, string prop) =>
        item.TryGetProperty(prop, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

    public async Task<int> ReaderCountAsync(string name, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"/v3/paths/get/{Uri.EscapeDataString(name)}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return 0;
        await Ensure(resp, ct);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("readers", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
    }

    public async Task<IReadOnlyDictionary<string, MediaMtxPath>> ListPathsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, MediaMtxPath>(StringComparer.Ordinal);
        for (var page = 0; ; page++)
        {
            using var resp = await http.GetAsync($"/v3/config/paths/list?page={page}&itemsPerPage=500", ct);
            await Ensure(resp, ct);
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString();
                if (name is null) continue;
                result[name] = new MediaMtxPath(Str(item, "source"),
                    item.TryGetProperty("sourceOnDemand", out var od) && od.ValueKind == JsonValueKind.True,
                    Str(item, "sourceOnDemandStartTimeout"), Str(item, "sourceOnDemandCloseAfter"));
            }
            var pages = doc.RootElement.TryGetProperty("pageCount", out var pc) ? pc.GetInt32() : 1;
            if (page + 1 >= pages) return result;
        }
    }

    public async Task AddAsync(string name, string source, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync($"/v3/config/paths/add/{Uri.EscapeDataString(name)}", Body(source), ct);
        if (resp.StatusCode == HttpStatusCode.BadRequest && (await resp.Content.ReadAsStringAsync(ct)).Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            await PatchAsync(name, source, ct);
            return;
        }
        await Ensure(resp, ct);
    }

    public async Task PatchAsync(string name, string source, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"/v3/config/paths/patch/{Uri.EscapeDataString(name)}") { Content = JsonContent.Create(Body(source)) };
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            using var add = await http.PostAsJsonAsync($"/v3/config/paths/add/{Uri.EscapeDataString(name)}", Body(source), ct);
            await Ensure(add, ct);
            return;
        }
        await Ensure(resp, ct);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var resp = await http.DeleteAsync($"/v3/config/paths/delete/{Uri.EscapeDataString(name)}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        await Ensure(resp, ct);
    }

    private static async Task Ensure(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"mediamtx {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
    }
}
