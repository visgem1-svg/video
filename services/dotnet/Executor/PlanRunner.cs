using System.Diagnostics;

namespace Executor;

public sealed class PlanRunResult
{
    public Dictionary<string, StepResult> Results { get; } = new();
    public Dictionary<string, double> TimingsMs { get; } = new();
    /// <summary>Steps with mode=queue: jobs to publish on jobs.heavy.</summary>
    public List<JobMsg> Jobs { get; } = new();
}

/// <summary>Executes plan steps in array order for one frame (contract 6).</summary>
public sealed class PlanRunner
{
    private readonly IToolInvoker _tools;
    private readonly StepStats _stats;

    public PlanRunner(IToolInvoker tools, StepStats stats)
    {
        _tools = tools;
        _stats = stats;
    }

    /// <param name="onStep">Called right after each successful inline step (progressive publishing).</param>
    public async Task<PlanRunResult> RunAsync(FrameMsg frame, Plan plan, ReadOnlyMemory<byte> image, CancellationToken ct,
        Action<PlanStep, StepResult, double>? onStep = null)
    {
        var run = new PlanRunResult();
        foreach (var step in plan.Steps)
        {
            if (!Conditions.Evaluate(step.When, run.Results))
            {
                run.Results[step.Id] = new StepResult(StepStatus.Skipped);
                _stats.Skipped(plan.PlanId, step.Id, step.Tool);
                continue;
            }

            if (step.Mode == "queue")
            {
                // The heavy consumer executes it later and emits its own detection/recognitions for this frame.
                // Later steps cannot depend on its output (status "queued" matches no condition).
                run.Results[step.Id] = new StepResult(StepStatus.Queued);
                run.Jobs.Add(new JobMsg(Guid.NewGuid().ToString(), frame, plan.PlanId, plan.Version, step.Id,
                    new Dictionary<string, StepResult>(run.Results)));
                continue;
            }

            var t0 = Stopwatch.GetTimestamp();
            try
            {
                run.Results[step.Id] = await _tools.InvokeAsync(step.Tool, step.Params, image, ct).ConfigureAwait(false);
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                run.TimingsMs[step.Id] = Math.Round(ms, 1);
                _stats.Processed(plan.PlanId, step.Id, step.Tool, ms);
                onStep?.Invoke(step, run.Results[step.Id], run.TimingsMs[step.Id]);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutdown / frame deadline: the whole frame is abandoned
            }
            catch (Exception)
            {
                run.Results[step.Id] = new StepResult(StepStatus.Failed);
                run.TimingsMs[step.Id] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, 1);
                _stats.Error(plan.PlanId, step.Id, step.Tool);
            }
        }
        return run;
    }
}

public static class DetectionBuilder
{
    public static Detection Build(FrameMsg f, string planId, int planVersion, IEnumerable<StepResult> ordered,
        Dictionary<string, double> timings, double totalMs, string executorId)
    {
        var objects = new List<ObjectDet>();
        var plates = new List<PlateDet>();
        var faces = new List<FaceDet>();
        var texts = new List<TextDet>();
        foreach (var r in ordered)
        {
            if (r.Status != StepStatus.Ok) continue;
            if (r.Objects is not null) objects.AddRange(r.Objects);
            if (r.Plates is not null) plates.AddRange(r.Plates);
            if (r.Faces is not null) faces.AddRange(r.Faces);
            if (r.Texts is not null) texts.AddRange(r.Texts);
        }
        return new Detection(f.FrameId, f.CameraId, f.Ts, f.Width, f.Height, planId, planVersion, objects, plates, faces,
            texts, timings, Math.Round(totalMs, 1), executorId);
    }

    /// <summary>One recognition per plate and per face (identified -> person name, otherwise "unknown").</summary>
    public static List<Recognition> Recognitions(FrameMsg f, Detection d)
    {
        var list = new List<Recognition>(d.Plates.Count + d.Faces.Count);
        foreach (var p in d.Plates)
            list.Add(new Recognition(Guid.NewGuid().ToString(), "plate", f.CameraId, f.FrameId, f.Ts, p.Plate, null,
                p.Confidence, p.Box, null, d.PlanId));
        foreach (var fc in d.Faces)
            list.Add(new Recognition(Guid.NewGuid().ToString(), "face", f.CameraId, f.FrameId, f.Ts,
                string.IsNullOrEmpty(fc.PersonName) ? "unknown" : fc.PersonName, fc.PersonId, fc.Confidence, fc.Box,
                null, d.PlanId));
        return list;
    }
}
