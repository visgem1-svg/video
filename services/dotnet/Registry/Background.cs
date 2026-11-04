using Registry.Assignment;

namespace Registry;

public sealed class ReadyState { public volatile bool Ready; }

public static class RegistryMetrics
{
    private static readonly Prometheus.Gauge WorkerCapacity =
        Prometheus.Metrics.CreateGauge("vp_registry_worker_capacity", "Effective capacity (override or reported) of live workers", "worker_id");
    private static readonly Prometheus.Gauge WorkerAssigned =
        Prometheus.Metrics.CreateGauge("vp_registry_worker_assigned", "Cameras assigned to a live worker", "worker_id");
    private static readonly Prometheus.Gauge WorkerLoad =
        Prometheus.Metrics.CreateGauge("vp_registry_worker_load", "Weighted load of live workers (full = 1.0, idle = IDLE_WEIGHT)", "worker_id");
    private static readonly Prometheus.Gauge ByMode =
        Prometheus.Metrics.CreateGauge("vp_registry_cameras_by_mode", "Enabled cameras by extraction mode", "mode");
    private static readonly HashSet<string> Known = new();

    public static void PublishModes(IReadOnlyDictionary<string, Registry.Assignment.Extract> modes)
    {
        var full = 0;
        foreach (var e in modes.Values) if (e.Mode == Registry.Assignment.Extract.Full) full++;
        ByMode.WithLabels("full").Set(full);
        ByMode.WithLabels("idle").Set(modes.Count - full);
    }

    /// <summary>Per-worker gauges after a rebalance; labels of dead/purged workers are removed.</summary>
    public static void PublishWorkers(IEnumerable<Registry.Assignment.WorkerRow> workers, Registry.Assignment.RebalanceResult r, long now, long leaseMs,
        IReadOnlyDictionary<string, double> loads)
    {
        lock (Known)
        {
            var counts = r.Assignments.GroupBy(a => a.Value).ToDictionary(g => g.Key, g => g.Count());
            var alive = new HashSet<string>();
            foreach (var w in workers)
            {
                if (now - w.LastSeenMs >= leaseMs) continue;
                alive.Add(w.WorkerId);
                WorkerCapacity.WithLabels(w.WorkerId).Set(w.EffectiveCapacity);
                WorkerAssigned.WithLabels(w.WorkerId).Set(counts.GetValueOrDefault(w.WorkerId));
                WorkerLoad.WithLabels(w.WorkerId).Set(loads.GetValueOrDefault(w.WorkerId));
            }
            foreach (var id in Known.Where(k => !alive.Contains(k)).ToList())
            {
                WorkerCapacity.RemoveLabelled(id); WorkerAssigned.RemoveLabelled(id); WorkerLoad.RemoveLabelled(id); Known.Remove(id);
            }
            foreach (var id in alive) Known.Add(id);
        }
    }

    public static readonly Prometheus.Counter Heartbeats =
        Prometheus.Metrics.CreateCounter("vp_registry_heartbeats_total", "Worker heartbeats handled");
    public static readonly Prometheus.Gauge Duplicates =
        Prometheus.Metrics.CreateGauge("vp_registry_duplicate_cameras", "Cameras that have at least one duplicate (same normalized stream_url)");
    public static readonly Prometheus.Gauge Cameras =
        Prometheus.Metrics.CreateGauge("vp_registry_cameras", "Cameras republished at startup");
}

/// <summary>Seeds cameras (optional) and republishes all cameras to the compacted topic, then flips readiness.</summary>
public sealed class StartupService(CameraService cameras, Microsoft.Extensions.Options.IOptions<RegistryOptions> opts,
    ReadyState ready, ILogger<StartupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await cameras.SeedAndRepublishAsync(opts.Value.SeedCameras, ct);
                var n = (await cameras.ListAsync(ct)).Count;
                RegistryMetrics.Cameras.Set(n);
                log.LogInformation("republished {Count} cameras to camera.config", n);
                ready.Ready = true;
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogWarning("startup republish attempt {Attempt} failed: {Error}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 15)), ct);
            }
        }
    }
}

/// <summary>Expires dead workers and rebalances even when no heartbeat arrives.</summary>
public sealed class LeaseSweeper(AssignmentService svc, ILogger<LeaseSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { await svc.SweepAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogWarning("lease sweep failed: {Error}", ex.Message); }
        }
    }
}

/// <summary>MediaMTX config is not persistent: re-provision every 30 s (also retries failed provisioning).</summary>
public sealed class MediaMtxReconciler(CameraService cameras, ReadyState ready, ILogger<MediaMtxReconciler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (!ready.Ready) continue;
            try { await cameras.ReconcileAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogWarning("mediamtx reconcile failed: {Error}", ex.Message); }
        }
    }
}
