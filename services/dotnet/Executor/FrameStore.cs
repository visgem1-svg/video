using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Executor;

/// <summary>A message value (JPEG) held in a pooled buffer. Dispose returns the buffer to the ArrayPool.</summary>
public sealed class PooledImage : IDisposable
{
    private byte[]? _buf;
    private readonly int _len;

    public PooledImage(byte[] pooledBuffer, int length)
    {
        _buf = pooledBuffer;
        _len = length;
    }

    public ReadOnlyMemory<byte> Memory => _buf is null ? throw new ObjectDisposedException(nameof(PooledImage)) : new ReadOnlyMemory<byte>(_buf, 0, _len);

    public static PooledImage FromBytes(byte[] data) => Copy(data);

    /// <summary>The single copy of a message value out of librdkafka's buffer into a pooled array.</summary>
    public static PooledImage Copy(ReadOnlySpan<byte> data)
    {
        var b = ArrayPool<byte>.Shared.Rent(Math.Max(1, data.Length));
        data.CopyTo(b);
        return new PooledImage(b, data.Length);
    }

    public void Dispose()
    {
        var b = Interlocked.Exchange(ref _buf, null);
        if (b is not null) ArrayPool<byte>.Shared.Return(b);
    }
}

public interface ISnapshotStore
{
    /// <summary>PUT a JPEG into the snapshots bucket (key = "{type}/{yyyyMMdd}/{recognition_id}.jpg").</summary>
    ValueTask PutAsync(string key, ReadOnlyMemory<byte> jpeg, CancellationToken ct);
}

/// <summary>
/// Minimal path-style S3 PUT (AWS SigV4) over a pooled HttpClient. Only recognitions that pass the cooldown are written
/// (a handful per minute), so the object store is off the hot path (contract 9.7).
/// </summary>
public sealed class S3SnapshotStore : ISnapshotStore
{
    public const string HttpClientName = "minio";
    private readonly IHttpClientFactory _factory;
    private readonly ExecutorOptions _o;
    private readonly Uri _endpoint;

    public S3SnapshotStore(IHttpClientFactory factory, ExecutorOptions o)
    {
        _factory = factory;
        _o = o;
        _endpoint = new Uri(o.MinioEndpoint);
    }

    public async ValueTask PutAsync(string key, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        var path = "/" + _o.SnapshotsBucket + "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        var amzDate = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var hash = Convert.ToHexString(SHA256.HashData(jpeg.Span)).ToLowerInvariant();
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = _endpoint.Authority,
            ["x-amz-content-sha256"] = hash,
            ["x-amz-date"] = amzDate,
        };
        var auth = SigV4.Authorization("PUT", path, "", headers, hash, _o.MinioAccessKey, _o.MinioSecretKey,
            _o.MinioRegion, "s3", amzDate);

        using var req = new HttpRequestMessage(HttpMethod.Put, new Uri(_endpoint, path));
        var content = new ReadOnlyMemoryContent(jpeg);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        req.Content = content;
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", hash);
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        using var resp = await _factory.CreateClient(HttpClientName).SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }
}

public static class SigV4
{
    /// <summary>Builds the Authorization header value. <paramref name="headers"/> = lowercase names, sorted, all signed.</summary>
    public static string Authorization(string method, string canonicalUri, string canonicalQuery,
        SortedDictionary<string, string> headers, string payloadHash, string accessKey, string secret, string region,
        string service, string amzDate)
    {
        var date = amzDate[..8];
        var signedHeaders = string.Join(';', headers.Keys);
        var canon = new StringBuilder()
            .Append(method).Append('\n').Append(canonicalUri).Append('\n').Append(canonicalQuery).Append('\n');
        foreach (var (k, v) in headers) canon.Append(k).Append(':').Append(v.Trim()).Append('\n');
        canon.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);

        var scope = $"{date}/{region}/{service}/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canon.ToString())))}";

        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        var kSigning = Hmac(kService, "aws4_request");
        var sig = Hex(Hmac(kSigning, toSign));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={sig}";
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
