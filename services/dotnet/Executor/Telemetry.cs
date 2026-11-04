using System.Collections.Concurrent;
using Prometheus;

namespace Executor;

/// <summary>Prometheus metrics (contract 10).</summary>
public static class Telemetry
{
    public static readonly Histogram StepDuration = Metrics.CreateHistogram("vp_executor_step_duration_seconds",
        "Duration of one engine call (excludes time waiting for the per-tool concurrency slot).",
        new HistogramConfiguration { LabelNames = new[] { "tool" }, Buckets = Histogram.ExponentialBuckets(0.005, 2, 12) });

    public static readonly Histogram ToolWait = Metrics.CreateHistogram("vp_executor_tool_wait_seconds",
        "Time a step waited for a per-tool concurrency slot.",
        new HistogramConfiguration { LabelNames = new[] { "tool" }, Buckets = Histogram.ExponentialBuckets(0.001, 2, 12) });

    public static readonly Counter Frames = Metrics.CreateCounter("vp_executor_frames_total",
        "Frames fully handled by the executor.", new CounterConfiguration { LabelNames = new[] { "result", "tier" } });

    public static readonly Counter FramesSkipped = Metrics.CreateCounter("vp_executor_frames_skipped_total",
        "Frames not processed.", new CounterConfiguration { LabelNames = new[] { "reason" } });

    public static readonly Histogram DetectionAge = Metrics.CreateHistogram("vp_executor_detection_age_seconds",
        "Publish time minus frame ts. stage=first: first detections message of a frame; stage=final: plan finished.",
        new HistogramConfiguration { LabelNames = new[] { "stage" }, Buckets = new[] { 0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 5, 10, 30 } });

    public static readonly Counter RecognitionsSuppressed = Metrics.CreateCounter("vp_executor_recognitions_suppressed_total",
        "Recognition sightings suppressed by the cooldown.", new CounterConfiguration { LabelNames = new[] { "type" } });

    public static readonly Counter SnapshotFailures = Metrics.CreateCounter("vp_executor_snapshot_failures_total",
        "Snapshot PUTs that failed after one retry (recognition published with snapshot_key null).");

    public static readonly Histogram DispatchAge = Metrics.CreateHistogram("vp_executor_frame_age_at_dispatch_seconds",
        "Frame age (now - frame ts) when the conflation queue hands it to a worker (before the stale check).",
        new HistogramConfiguration { LabelNames = new[] { "tier" }, Buckets = new[] { 0.05, 0.1, 0.25, 0.5, 1, 1.5, 2, 3, 5, 10 } });

    public static readonly Gauge Inflight = Metrics.CreateGauge("vp_executor_inflight", "Frames being processed now.");

    public static readonly Counter Jobs = Metrics.CreateCounter("vp_executor_heavy_jobs_total",
        "jobs.heavy handled.", new CounterConfiguration { LabelNames = new[] { "result" } });

    public static readonly Gauge HeavyInflight = Metrics.CreateGauge("vp_executor_heavy_inflight", "Heavy jobs running now.");
}

/// <summary>Per (plan, step) window statistics flushed to pipeline.metrics every N seconds (contract 5.8).</summary>
public sealed class StepStats
{
    private const int MaxSamples = 2048;

    private sealed class Window
    {
        public readonly object Lock = new();
        public string Tool = "";
        public int Processed, Skipped, Errors, N;
        public readonly float[] Samples = new float[MaxSamples];
    }

    private readonly ConcurrentDictionary<(string, string), Window> _w = new();

    private Window Get(string plan, string step, string tool)
    {
        var w = _w.GetOrAdd((plan, step), _ => new Window());
        w.Tool = tool;
        return w;
    }

    public void Processed(string plan, string step, string tool, double ms)
    {
        var w = Get(plan, step, tool);
        lock (w.Lock)
        {
            w.Processed++;
            if (w.N < MaxSamples) w.Samples[w.N++] = (float)ms; // first-N sampling is enough for a 5 s window
        }
    }

    public void Skipped(string plan, string step, string tool)
    {
        var w = Get(plan, step, tool);
        lock (w.Lock) w.Skipped++;
    }

    public void Error(string plan, string step, string tool)
    {
        var w = Get(plan, step, tool);
        lock (w.Lock) w.Errors++;
    }

    /// <summary>Returns the windows with activity and resets them.</summary>
    public List<PipelineMetric> Flush(string executorId, int windowS, long nowMs)
    {
        var res = new List<PipelineMetric>();
        foreach (var (key, w) in _w)
        {
            float[] snap;
            int processed, skipped, errors;
            string tool;
            lock (w.Lock)
            {
                if (w.Processed == 0 && w.Skipped == 0 && w.Errors == 0) continue;
                processed = w.Processed; skipped = w.Skipped; errors = w.Errors; tool = w.Tool;
                snap = new float[w.N];
                Array.Copy(w.Samples, snap, w.N);
                w.Processed = w.Skipped = w.Errors = w.N = 0;
            }
            Array.Sort(snap);
            res.Add(new PipelineMetric(key.Item1, key.Item2, tool, windowS, processed, skipped, errors,
                Math.Round(Pct(snap, 0.50), 2), Math.Round(Pct(snap, 0.95), 2), executorId, nowMs));
        }
        return res;
    }

    internal static double Pct(float[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
