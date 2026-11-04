using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace Results;

public sealed class MinioSnapshotStore : ISnapshotStore
{
    private readonly IMinioClient _client;
    private readonly string _snapshots;

    public MinioSnapshotStore(ResultsOptions o)
    {
        _snapshots = o.SnapshotsBucket;
        _client = new MinioClient()
            .WithEndpoint(o.MinioEndpoint)
            .WithCredentials(o.MinioAccessKey, o.MinioSecretKey)
            .WithSSL(o.MinioUseSsl)
            .Build();
    }

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (!await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_snapshots), ct))
            await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_snapshots), ct);
    }

    public async Task<bool> ReadAsync(string snapshotKey, Stream destination, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectAsync(new GetObjectArgs().WithBucket(_snapshots).WithObject(snapshotKey)
                .WithCallbackStream(async (s, token) => await s.CopyToAsync(destination, token)), ct);
            return true;
        }
        catch (ObjectNotFoundException) { return false; }
    }
}
