using System.Text.RegularExpressions;
using Registry.Data;

namespace Registry;

public sealed class ValidationException(string message) : Exception(message);

public sealed partial class CameraService(
    ICameraStore store, ICameraPublisher publisher, TimeProvider clock, IMediaMtxClient mediaMtx,
    ILogger<CameraService>? log = null, string rtspBase = "rtsp://mediamtx:8554", Registry.Assignment.ExtractModeEngine? engine = null)
{
    public const string Publisher = "publisher";
    public const string AnalysisSuffix = "-analysis";
    private static readonly string[] Sensitivities = ["low", "medium", "high"];

    // Camera writes are rare; one in-process gate keeps "DB commit, provision, Kafka publish" ordered per replica.
    private readonly SemaphoreSlim _gate = new(1, 1);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,39}$")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string id) => IdRegex().IsMatch(id);

    public static bool IsPublisher(string streamUrl) => streamUrl == Publisher;

    public static void ValidateStreamUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ValidationException("stream_url is required (rtsp://, rtsps://, rtmp://, http(s)://...m3u8, srt://, or \"publisher\")");
        if (url == Publisher) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("rtsp" or "rtsps" or "rtmp" or "rtmps" or "http" or "https" or "srt") || string.IsNullOrEmpty(u.Host))
            throw new ValidationException("stream_url must be rtsp://, rtsps://, rtmp://, http(s)://...m3u8, srt:// (or \"publisher\")");
    }

    public static void ValidateAnalysisStreamUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("rtsp" or "rtsps") || string.IsNullOrEmpty(u.Host))
            throw new ValidationException("analysis_stream_url must be an rtsp:// or rtsps:// URL (send \"\" to clear it)");
    }

    /// <summary>
    /// analysis_stream_url: absent/null keeps the previous value (PUT) or none (POST); "" clears; otherwise validated rtsp(s) URL.
    /// motion: members left out keep the previous value (PUT) or the default (POST). Errors name the offending fields.
    /// </summary>
    private static (string? Analysis, MotionConfig Motion) ResolveAnalysisAndMotion(CameraInput i, CameraConfig? old)
    {
        string? analysis = old?.AnalysisStreamUrl;
        if (i.AnalysisStreamUrl is { } a)
        {
            analysis = string.IsNullOrWhiteSpace(a) ? null : a.Trim();
            if (analysis is not null) ValidateAnalysisStreamUrl(analysis);
        }
        var m = old?.Motion ?? MotionConfig.Default;
        if (i.Motion is { } mi)
        {
            var errors = new List<string>(2);
            var sens = mi.Sensitivity is null ? m.Sensitivity : mi.Sensitivity.Trim().ToLowerInvariant();
            if (Array.IndexOf(Sensitivities, sens) < 0) errors.Add("motion.sensitivity must be one of low|medium|high");
            var hb = mi.HeartbeatS ?? m.HeartbeatS;
            if (!(hb >= 1 && hb <= 60)) errors.Add("motion.heartbeat_s must be in [1, 60]");
            if (errors.Count > 0) throw new ValidationException(string.Join("; ", errors));
            m = new MotionConfig(mi.Enabled ?? m.Enabled, sens, hb);
        }
        return (analysis, m);
    }

    /// <summary>Builds the stored camera. source_url is always derived (clients never set it). <paramref name="old"/> = PUT baseline.</summary>
    public CameraConfig Build(string id, CameraInput i, long now, CameraConfig? old = null)
    {
        if (!IsValidId(id))
            throw new ValidationException("camera_id must be 3-40 characters of lowercase letters, digits and '-', starting with a letter or digit (it is also the MediaMTX path name)");
        if (id.EndsWith(AnalysisSuffix, StringComparison.Ordinal))
            throw new ValidationException($"camera_id must not end with '{AnalysisSuffix}' (reserved for the analysis MediaMTX path)");
        var stream = string.IsNullOrWhiteSpace(i.StreamUrl) ? old?.StreamUrl : i.StreamUrl.Trim();
        ValidateStreamUrl(stream);
        var fps = i.AnalysisFps ?? 3;
        if (fps is <= 0 or > 60) throw new ValidationException("analysis_fps must be in (0, 60]");
        var (analysis, motion) = ResolveAnalysisAndMotion(i, old);
        return new CameraConfig(id, string.IsNullOrWhiteSpace(i.Name) ? id : i.Name!, SourceUrlFor(id), fps,
            i.Enabled ?? true, string.IsNullOrWhiteSpace(i.PlanId) ? null : i.PlanId, i.Tags ?? [], now, stream!, null)
        { AnalysisStreamUrl = analysis, Motion = motion };
    }

    private static string AnalysisPath(string id) => id + AnalysisSuffix;

    private string SourceUrlFor(string id) => $"{rtspBase.TrimEnd('/')}/{id}";

    public async Task<IReadOnlyList<CameraConfig>> ListAsync(CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        var d = Duplicates.Compute(all);
        RegistryMetrics.Duplicates.Set(d.Count(kv => kv.Value.Length > 0));
        return all.Select(c => c with { DuplicateOf = d[c.CameraId] }).ToList();
    }

    private CameraConfig WithPlacement(CameraConfig c, IReadOnlyDictionary<string, string> asg, IReadOnlyDictionary<string, Registry.Assignment.Extract> modes) =>
        c with
        {
            Extract = modes.GetValueOrDefault(c.CameraId),
            AssignedWorker = asg.GetValueOrDefault(c.CameraId),
            PinFallback = c.PinnedWorker is not null && asg.GetValueOrDefault(c.CameraId) != c.PinnedWorker,
        };

    /// <summary>List for the HTTP API: duplicate_of plus computed assigned_worker / pin_fallback.</summary>
    public async Task<IReadOnlyList<CameraConfig>> ListApiAsync(CancellationToken ct)
    {
        var all = await ListAsync(ct);
        var asg = await store.AssignmentsAsync(ct);
        var modes = ModesOf(all, prune: true);
        return all.Select(c => WithPlacement(c, asg, modes)).ToList();
    }

    private IReadOnlyDictionary<string, Registry.Assignment.Extract> ModesOf(IReadOnlyList<CameraConfig> all, bool prune = false) =>
        engine is null ? new Dictionary<string, Registry.Assignment.Extract>() : engine.Resolve(all, prune).Modes;

    /// <summary>Pin (or unpin with null) a camera; false = unknown camera. Republishes camera.config.</summary>
    public async Task<CameraConfig?> PinAsync(string id, string? workerId, CancellationToken ct)
    {
        if (workerId is not null && (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 128)) throw new ValidationException("invalid worker_id");
        await _gate.WaitAsync(ct);
        try
        {
            if (!await store.SetPinAsync(id, workerId, ct)) return null;
            return await RepublishAsync(id, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Bumps the camera's generation; extractors restart that camera when it changes.</summary>
    public async Task<CameraConfig?> RestartAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!await store.BumpGenerationAsync(id, ct)) return null;
            return await RepublishAsync(id, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<CameraConfig?> RepublishAsync(string id, CancellationToken ct)
    {
        var c = await store.GetAsync(id, ct);
        if (c is null) return null;
        c = c with { DuplicateOf = Duplicates.Compute(await store.ListAsync(ct))[id] };
        await publisher.PublishAsync(id, c, ct);
        return WithPlacement(c, await store.AssignmentsAsync(ct), ModesOf(await store.ListAsync(ct)));
    }

    public async Task<CameraConfig?> GetAsync(string id, CancellationToken ct)
    {
        var c = await store.GetAsync(id, ct);
        if (c is null) return null;
        var all = await store.ListAsync(ct);
        return WithPlacement(c with { DuplicateOf = Duplicates.Compute(all)[id] }, await store.AssignmentsAsync(ct), ModesOf(all));
    }

    private async Task<Dictionary<string, string[]>> DupMapAsync(CancellationToken ct) => Duplicates.Compute(await store.ListAsync(ct));

    /// <summary>Publishes the written camera and every other camera whose duplicate_of changed. Returns the enriched camera.</summary>
    private async Task<CameraConfig?> PublishAfterWriteAsync(CameraConfig? cam, string id, Dictionary<string, string[]> before, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        var after = Duplicates.Compute(all);
        RegistryMetrics.Duplicates.Set(after.Count(kv => kv.Value.Length > 0));
        if (cam is not null)
        {
            cam = cam with { DuplicateOf = after[id] };
            await publisher.PublishAsync(id, cam, ct);
        }
        else await publisher.PublishAsync(id, null, ct); // tombstone
        foreach (var c in all)
            if (c.CameraId != id && !before.GetValueOrDefault(c.CameraId, []).SequenceEqual(after[c.CameraId]))
                await publisher.PublishAsync(c.CameraId, c with { DuplicateOf = after[c.CameraId] }, ct);
        return cam;
    }

    /// <returns>null when the camera already exists.</returns>
    public async Task<CameraConfig?> CreateAsync(CameraInput input, CancellationToken ct)
    {
        var cam = Build(input.CameraId ?? throw new ValidationException("camera_id is required"), input, Now());
        await _gate.WaitAsync(ct);
        try
        {
            var before = await DupMapAsync(ct);
            if (!await store.InsertAsync(cam, ct)) return null;
            var ops = new List<(string, Func<Task>)>(2);
            if (!IsPublisher(cam.StreamUrl)) ops.Add((cam.CameraId, () => mediaMtx.AddAsync(cam.CameraId, cam.StreamUrl, ct)));
            if (cam.AnalysisStreamUrl is { } au) ops.Add((AnalysisPath(cam.CameraId), () => mediaMtx.AddAsync(AnalysisPath(cam.CameraId), au, ct)));
            var err = await ProvisionAsync(ops);
            if (err is not null) { await store.SetProvisionErrorAsync(cam.CameraId, err, ct); cam = cam with { ProvisionError = err }; }
            return await PublishAfterWriteAsync(cam, cam.CameraId, before, ct);
        }
        finally { _gate.Release(); }
    }

    /// <returns>null when the camera does not exist.</returns>
    public async Task<CameraConfig?> UpdateAsync(string id, CameraInput input, CancellationToken ct)
    {
        if (input.CameraId is not null && input.CameraId != id) throw new ValidationException("camera_id in body differs from URL");
        await _gate.WaitAsync(ct);
        try
        {
            var before = await DupMapAsync(ct);
            var old = await store.GetAsync(id, ct);
            if (old is null) return null;
            var cam = Build(id, input, Now(), old) with { PinnedWorker = old.PinnedWorker, Generation = old.Generation };
            if (!await store.UpdateAsync(cam, ct)) return null;

            // A path is (re)provisioned when its URL changed or the last attempt failed; Add upserts, so a retry is safe.
            var retry = old.ProvisionError is not null;
            var ops = new List<(string, Func<Task>)>(2);
            if (IsPublisher(cam.StreamUrl)) { if (!IsPublisher(old.StreamUrl)) ops.Add((id, () => mediaMtx.DeleteAsync(id, ct))); }
            else if (cam.StreamUrl != old.StreamUrl || retry)
                ops.Add((id, () => (IsPublisher(old.StreamUrl) || retry) ? mediaMtx.AddAsync(id, cam.StreamUrl, ct) : mediaMtx.PatchAsync(id, cam.StreamUrl, ct)));
            var ap = AnalysisPath(id);
            if (cam.AnalysisStreamUrl is { } au)
            {
                if (au != old.AnalysisStreamUrl || retry)
                    ops.Add((ap, () => (old.AnalysisStreamUrl is null || retry) ? mediaMtx.AddAsync(ap, au, ct) : mediaMtx.PatchAsync(ap, au, ct)));
            }
            else if (old.AnalysisStreamUrl is not null || retry) ops.Add((ap, () => mediaMtx.DeleteAsync(ap, ct)));
            if (ops.Count > 0)
            {
                var err = await ProvisionAsync(ops);
                if (err is not null) { await store.SetProvisionErrorAsync(id, err, ct); cam = cam with { ProvisionError = err }; }
            }
            return await PublishAfterWriteAsync(cam, id, before, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var before = await DupMapAsync(ct);
            var old = await store.GetAsync(id, ct);
            if (old is null || !await store.DeleteAsync(id, ct)) return false;
            var ops = new List<(string, Func<Task>)>(2);
            if (!IsPublisher(old.StreamUrl)) ops.Add((id, () => mediaMtx.DeleteAsync(id, ct)));
            if (old.AnalysisStreamUrl is not null) ops.Add((AnalysisPath(id), () => mediaMtx.DeleteAsync(AnalysisPath(id), ct)));
            if (ops.Count > 0) await ProvisionAsync(ops); // best effort; the camera is gone either way
            await PublishAfterWriteAsync(null, id, before, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    [GeneratedRegex(@"(?<=://)[^/\s@]+@")]
    private static partial Regex UserInfoRegex();

    /// <summary>Strips "user:pass@" from anything that may echo a source URL; error text is stored, logged and returned by the API.</summary>
    internal static string Redact(string s) => UserInfoRegex().Replace(s, "***@");

    private static string? Join(List<string>? errors)
    {
        if (errors is null or { Count: 0 }) return null;
        var all = string.Join("; ", errors);
        return all.Length > 300 ? all[..300] : all;
    }

    /// <summary>Runs MediaMTX calls (all of them, even after a failure); returns the joined error text, or null on success. Never throws (except cancellation).</summary>
    private async Task<string?> ProvisionAsync(IReadOnlyList<(string Path, Func<Task> Call)> calls)
    {
        List<string>? errors = null;
        foreach (var (path, call) in calls)
        {
            try { await call(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = "mediamtx provisioning failed: " + Redact(ex.Message);
                log?.LogWarning("{Path}: {Error}", path, msg);
                (errors ??= []).Add(msg);
            }
        }
        return Join(errors);
    }

    private Task<string?> ProvisionAsync(string path, Func<Task> call) => ProvisionAsync([(path, call)]);

    /// <summary>
    /// Brings one path to its desired state. A path that exists with the right source but not the on-demand settings
    /// (provisioned before 9.9) is upgraded in place only while nobody reads it: a PATCH restarts the path and would drop
    /// the readers. Such busy paths keep running as they are and pick the settings up on their next change / MediaMTX restart.
    /// </summary>
    private Task<string?> ReconcilePathAsync(IReadOnlyDictionary<string, MediaMtxPath> paths, string name, string url, CancellationToken ct)
    {
        if (!paths.TryGetValue(name, out var cur)) return ProvisionAsync(name, () => mediaMtx.AddAsync(name, url, ct));
        if (cur.Source != url) return ProvisionAsync(name, () => mediaMtx.PatchAsync(name, url, ct));
        if (cur.IsOnDemandPull) return Task.FromResult<string?>(null);
        return ProvisionAsync(name, async () =>
        {
            if (await mediaMtx.ReaderCountAsync(name, ct) == 0) await mediaMtx.PatchAsync(name, url, ct);
        });
    }

    /// <summary>
    /// Makes MediaMTX match the cameras (startup + every 30 s): adds missing paths (main and analysis), patches differing sources,
    /// removes a leftover analysis path of a camera without analysis_stream_url, records/clears provision_error and republishes
    /// a camera only when its error changed. Paths of unknown cameras are left alone.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, MediaMtxPath>? paths = null;
        string? listError = null;
        try { paths = await mediaMtx.ListPathsAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            listError = "mediamtx unreachable: " + Redact(ex.Message);
            log?.LogWarning("reconcile: {Error}", listError);
        }

        var dups = await DupMapAsync(ct);
        foreach (var c in await store.ListAsync(ct))
        {
            var orphan = paths is not null && c.AnalysisStreamUrl is null && paths.ContainsKey(AnalysisPath(c.CameraId));
            if (IsPublisher(c.StreamUrl) && c.AnalysisStreamUrl is null && !orphan) continue;
            await _gate.WaitAsync(ct);
            try
            {
                var cam = await store.GetAsync(c.CameraId, ct); // re-read under the gate
                if (cam is null) continue;
                string? err = listError;
                if (paths is not null)
                {
                    var errors = new List<string>(2);
                    if (!IsPublisher(cam.StreamUrl) && await ReconcilePathAsync(paths, cam.CameraId, cam.StreamUrl, ct) is { } e1) errors.Add(e1);
                    if (cam.AnalysisStreamUrl is { } au)
                    { if (await ReconcilePathAsync(paths, AnalysisPath(cam.CameraId), au, ct) is { } e2) errors.Add(e2); }
                    else if (orphan && await ProvisionAsync(AnalysisPath(cam.CameraId), () => mediaMtx.DeleteAsync(AnalysisPath(cam.CameraId), ct)) is { } e3) errors.Add(e3);
                    err = Join(errors);
                }
                if (err != cam.ProvisionError)
                {
                    await store.SetProvisionErrorAsync(cam.CameraId, err, ct);
                    await publisher.PublishAsync(cam.CameraId, cam with { ProvisionError = err, DuplicateOf = dups.GetValueOrDefault(cam.CameraId, []) }, ct);
                }
            }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Startup: seed (if configured and empty), reconcile MediaMTX, republish every camera so consumers recover.</summary>
    public async Task SeedAndRepublishAsync(int seedCameras, CancellationToken ct)
    {
        if (seedCameras > 0 && await store.CountAsync(ct) == 0)
        {
            for (var n = 1; n <= seedCameras; n++)
            {
                var id = $"cam-{n:000}";
                await store.InsertAsync(new CameraConfig(id, $"Camera {n:000}", SourceUrlFor(id), 3, true, null, [], Now(), Publisher, null), ct);
            }
        }
        await ReconcileAsync(ct);
        await publisher.PublishManyAsync(await ListAsync(ct), ct);
    }

    private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();
}
