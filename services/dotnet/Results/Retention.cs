using Prometheus;

namespace Results;

/// <summary>Deletes recognition rows older than the retention horizon in small batches (contract 9.6).</summary>
public sealed class RetentionJob(IResultsStore store, ResultsOptions o, TimeProvider clock, ILogger<RetentionJob> log)
{
    public const int BatchSize = 1000;
    private static readonly Counter Deleted = Metrics.CreateCounter("vp_results_recognitions_deleted_total", "Recognition rows deleted by retention");

    /// <returns>rows deleted (0 when retention is disabled, RECOGNITION_RETENTION_DAYS &lt;= 0).</returns>
    public async Task<long> RunOnceAsync(CancellationToken ct, TimeSpan? pauseBetweenBatches = null)
    {
        if (o.RecognitionRetentionDays <= 0) return 0;
        var cutoff = clock.GetUtcNow().AddDays(-o.RecognitionRetentionDays).ToUnixTimeMilliseconds();
        long total = 0;
        while (!ct.IsCancellationRequested)
        {
            var n = await store.DeleteRecognitionsOlderThanAsync(cutoff, BatchSize, ct);
            total += n;
            Deleted.Inc(n);
            if (n < BatchSize) break;
            await Task.Delay(pauseBetweenBatches ?? TimeSpan.FromMilliseconds(100), ct); // be gentle with the DB
        }
        if (total > 0) log.LogInformation("retention deleted {Count} recognitions older than {Days} days", total, o.RecognitionRetentionDays);
        return total;
    }
}

public sealed class RetentionService(RetentionJob job, ILogger<RetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await job.RunOnceAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogWarning("retention run failed: {Error}", ex.Message); }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }
}
