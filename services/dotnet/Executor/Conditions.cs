namespace Executor;

/// <summary>Evaluates plan "when" conditions (contract 6) against the results of earlier steps.</summary>
public static class Conditions
{
    public static bool Evaluate(When? when, IReadOnlyDictionary<string, StepResult> results)
    {
        if (when is null) return true;
        switch (when.Type)
        {
            case "always":
                return true;
            case "found":
            {
                var min = when.MinConfidence ?? 0.0;
                foreach (var r in results.Values)
                    if (r.Status == StepStatus.Ok && CountMatching(r, when.Class, min, stopAt: 1) > 0) return true;
                return false;
            }
            case "count":
            {
                // union over all earlier detect_objects steps
                var total = 0;
                foreach (var r in results.Values)
                    if (r.Status == StepStatus.Ok) total += CountMatching(r, when.Class, 0.0, int.MaxValue);
                return total > (when.Gt ?? 0);
            }
            case "failed":
                return when.Step is not null && results.TryGetValue(when.Step, out var s) && s.Status == StepStatus.Failed;
            default:
                return false; // unknown condition type: never run (the planner validator should have rejected it)
        }
    }

    /// <summary>
    /// Counts objects whose class is in <paramref name="cls"/> (comma-separated any-of list, trimmed, case-insensitive;
    /// COCO names with spaces) with confidence >= min.
    /// </summary>
    internal static int CountMatching(StepResult r, string? cls, double minConfidence, int stopAt)
    {
        if (string.IsNullOrWhiteSpace(cls) || r.Objects is not { } objs) return 0;
        var wanted = cls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var n = 0;
        foreach (var o in objs)
        {
            if (o.Confidence < minConfidence) continue;
            foreach (var w in wanted)
                if (string.Equals(o.Class, w, StringComparison.OrdinalIgnoreCase)) { if (++n >= stopAt) return n; break; }
        }
        return n;
    }
}
