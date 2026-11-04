using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Executor;

public interface IResultSink
{
    /// <summary>Publishes and completes when the broker acknowledged the message.</summary>
    ValueTask PublishAsync(string topic, string key, byte[] value, CancellationToken ct,
        IReadOnlyDictionary<string, byte[]>? headers = null);
}

public static class Topics
{
    public const string FramesRaw = "frames.raw";
    public const string CameraConfig = "camera.config";
    public const string CameraDemand = "camera.demand";
    public const string PipelinePlans = "pipeline.plans";
    public const string Detections = "detections";
    public const string Recognitions = "recognitions";
    public const string PipelineMetrics = "pipeline.metrics";
    public const string JobsHeavy = "jobs.heavy";
}

/// <summary>A consumed frames.raw message: meta (header) + JPEG (pooled copy of the value). Disposing returns the buffer.</summary>
public sealed class FrameIn : IDisposable
{
    public FrameMsg Meta { get; }
    public PooledImage Image { get; }
    public FrameIn(FrameMsg meta, PooledImage image) { Meta = meta; Image = image; }
    public void Dispose() => Image.Dispose();
}

/// <summary>A consumed jobs.heavy message: value = JPEG, header "job" = JobMsg JSON.</summary>
public sealed class JobIn : IDisposable
{
    public JobMsg Job { get; }
    public PooledImage Image { get; }
    public JobIn(JobMsg job, PooledImage image) { Job = job; Image = image; }
    public void Dispose() => Image.Dispose();
}

/// <summary>Run plan on the in-message JPEG -> publish detections/recognitions/jobs for one frame.</summary>
public sealed class FramePipeline
{
    public const string JobHeader = "job";

    private readonly PlanStore _plans;
    private readonly ISnapshotStore _snaps;
    private readonly PlanRunner _runner;
    private readonly IResultSink _sink;
    private readonly ExecutorOptions _o;
    private readonly ILogger _log;
    private readonly RecognitionCooldown _cooldown;
    private readonly SemaphoreSlim _snapSlots;
    private readonly Func<string, bool> _isWatched;
    private string Tier(string cam) => _isWatched(cam) ? "watched" : "background";

    public FramePipeline(PlanStore plans, ISnapshotStore snaps, PlanRunner runner, IResultSink sink, ExecutorOptions o,
        ILogger log, RecognitionCooldown? cooldown = null, Func<string, bool>? isWatched = null)
    {
        _isWatched = isWatched ?? (_ => false);
        _plans = plans;
        _snaps = snaps;
        _runner = runner;
        _sink = sink;
        _o = o;
        _log = log;
        _cooldown = cooldown ?? new RecognitionCooldown(o.RecognitionCooldownS, o.UnknownFaceCooldownS);
        _snapSlots = new SemaphoreSlim(o.SnapshotConcurrency, o.SnapshotConcurrency);
    }

    public Task ProcessAsync(FrameIn f, CancellationToken ct) => ProcessAsync(f.Meta, f.Image.Memory, ct);

    public async Task ProcessAsync(FrameMsg frame, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        // The plan is resolved ONCE here and the immutable instance is used until the frame completes.
        var (plan, skip) = _plans.Resolve(frame.CameraId);
        if (plan is null)
        {
            Telemetry.FramesSkipped.WithLabels(skip ?? "no_plan").Inc();
            return;
        }

        var t0 = Stopwatch.GetTimestamp();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_o.FrameTimeoutMs);
        try
        {
            // Progressive publishing (contract 9.5): one detections message per completed inline step, sent while the
            // next step runs. Publishes are not awaited between steps; all are awaited before the frame counts as done,
            // so offsets still commit only after results are out (and the JPEG buffer stays valid for snapshot PUTs).
            var pending = new List<Task>(8);
            var firstDone = false;
            void Emit(Detection det)
            {
                pending.Add(_sink.PublishAsync(Topics.Detections, frame.CameraId, JsonSerializer.SerializeToUtf8Bytes(det, Json.Default.Detection), cts.Token).AsTask());
                foreach (var r in DetectionBuilder.Recognitions(frame, det).Where(_cooldown.Allow))
                    pending.Add(EmitRecognitionAsync(frame, jpeg, r, cts.Token));
            }
            double Elapsed() => Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            var run = await _runner.RunAsync(frame, plan, jpeg, cts.Token, (step, res, ms) =>
            {
                var partial = firstDone;
                firstDone = true;
                Emit(DetectionBuilder.Build(frame, plan.PlanId, plan.Version, new[] { res },
                    new Dictionary<string, double> { [step.Id] = ms }, Elapsed(), _o.ExecutorId) with
                { Partial = partial, StepId = partial ? step.Id : null });
                if (!partial) Telemetry.DetectionAge.WithLabels("first").Observe(AgeSeconds(frame));
            }).ConfigureAwait(false);

            if (!firstDone)
            {
                // nothing completed inline (all skipped / failed / queue-only): still tell consumers the frame was analyzed
                Emit(DetectionBuilder.Build(frame, plan.PlanId, plan.Version, Array.Empty<StepResult>(), run.TimingsMs, Elapsed(), _o.ExecutorId));
                Telemetry.DetectionAge.WithLabels("first").Observe(AgeSeconds(frame));
            }
            Telemetry.DetectionAge.WithLabels("final").Observe(AgeSeconds(frame));

            // jobs.heavy is self-contained: value = the JPEG, header "job" = job JSON (no dependency on frames.raw retention)
            foreach (var j in run.Jobs)
                pending.Add(_sink.PublishAsync(Topics.JobsHeavy, frame.CameraId, jpeg.ToArray(), cts.Token,
                    new Dictionary<string, byte[]> { [JobHeader] = JsonSerializer.SerializeToUtf8Bytes(j, Json.Default.JobMsg) }).AsTask());
            await Task.WhenAll(pending).ConfigureAwait(false);
            Telemetry.Frames.WithLabels("ok", Tier(frame.CameraId)).Inc();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Telemetry.Frames.WithLabels("error", Tier(frame.CameraId)).Inc();
            _log.LogWarning(ex, "frame {FrameId} ({Camera}) failed: {Message}", frame.FrameId, frame.CameraId, ex.Message);
        }
    }

    private static double AgeSeconds(FrameMsg f) => Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - f.Ts) / 1000.0);

    public static string SnapshotKey(Recognition r, FrameMsg f) =>
        $"{r.Type}/{DateTimeOffset.FromUnixTimeMilliseconds(f.Ts):yyyyMMdd}/{r.RecognitionId}.jpg";

    /// <summary>
    /// PUT the frame (bounded concurrency, one retry), then publish the recognition with snapshot_key. If the PUT fails
    /// the recognition is still published with snapshot_key = null and the failure is counted.
    /// </summary>
    private async Task EmitRecognitionAsync(FrameMsg frame, ReadOnlyMemory<byte> jpeg, Recognition r, CancellationToken ct)
    {
        string? key = SnapshotKey(r, frame);
        await _snapSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await _snaps.PutAsync(key, jpeg, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    await _snaps.PutAsync(key, jpeg, ct).ConfigureAwait(false);
                }
                catch (Exception ex2) when (!ct.IsCancellationRequested)
                {
                    key = null;
                    Telemetry.SnapshotFailures.Inc();
                    _log.LogWarning("snapshot PUT failed for {Id}: {First} / {Second}", r.RecognitionId, ex.Message, ex2.Message);
                }
            }
        }
        finally
        {
            _snapSlots.Release();
        }
        await _sink.PublishAsync(Topics.Recognitions, frame.CameraId,
            JsonSerializer.SerializeToUtf8Bytes(r with { SnapshotKey = key }, Json.Default.Recognition), ct).ConfigureAwait(false);
    }

    /// <summary>Executes one jobs.heavy step and emits a partial detection (+ recognitions) carrying only that step's output.</summary>
    public Task ProcessJobAsync(JobIn j, CancellationToken ct) => ProcessJobAsync(j.Job, j.Image.Memory, ct);

    public async Task ProcessJobAsync(JobMsg job, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        var plan = _plans.GetVersion(job.PlanId, job.PlanVersion);
        var step = plan?.Steps.FirstOrDefault(s => s.Id == job.StepId);
        if (plan is null || step is null)
        {
            Telemetry.Jobs.WithLabels("unknown_plan").Inc();
            return;
        }

        var t0 = Stopwatch.GetTimestamp();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_o.FrameTimeoutMs);
        try
        {
            var one = new Plan(plan.PlanId, plan.Version, plan.Name, plan.Query, plan.CameraIds, plan.Status, plan.CreatedAt,
                new List<PlanStep> { step with { Mode = "inline", When = null } });
            var run = await _runner.RunAsync(job.Frame, one, jpeg, cts.Token).ConfigureAwait(false);
            var det = DetectionBuilder.Build(job.Frame, plan.PlanId, plan.Version, run.Results.Values, run.TimingsMs,
                Stopwatch.GetElapsedTime(t0).TotalMilliseconds, _o.ExecutorId) with { Partial = true, StepId = job.StepId };
            var tasks = new List<Task>
            {
                _sink.PublishAsync(Topics.Detections, job.Frame.CameraId, JsonSerializer.SerializeToUtf8Bytes(det, Json.Default.Detection), cts.Token).AsTask(),
            };
            foreach (var r in DetectionBuilder.Recognitions(job.Frame, det).Where(_cooldown.Allow))
                tasks.Add(EmitRecognitionAsync(job.Frame, jpeg, r, cts.Token));
            await Task.WhenAll(tasks).ConfigureAwait(false);
            Telemetry.Jobs.WithLabels(run.Results[step.Id].Status == StepStatus.Ok ? "ok" : "failed").Inc();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Telemetry.Jobs.WithLabels("error").Inc();
            _log.LogWarning(ex, "job {JobId} failed: {Message}", job.JobId, ex.Message);
        }
    }
}
