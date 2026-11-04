using System.Reflection;
using Npgsql;
using Registry.Assignment;

namespace Registry.Data;

public interface ICameraStore
{
    Task<IReadOnlyList<CameraConfig>> ListAsync(CancellationToken ct);
    Task<CameraConfig?> GetAsync(string id, CancellationToken ct);
    Task<bool> InsertAsync(CameraConfig c, CancellationToken ct);
    Task<bool> UpdateAsync(CameraConfig c, CancellationToken ct);
    Task<bool> DeleteAsync(string id, CancellationToken ct);
    Task<bool> SetProvisionErrorAsync(string id, string? error, CancellationToken ct);
    Task<bool> SetPinAsync(string id, string? workerId, CancellationToken ct);
    Task<bool> BumpGenerationAsync(string id, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> AssignmentsAsync(CancellationToken ct);
    Task<long> CountAsync(CancellationToken ct);
}

/// <summary>Applies embedded *.sql scripts in name order, once each, under an advisory lock.</summary>
public static class Migrator
{
    public static async Task RunAsync(NpgsqlDataSource ds, Assembly asm, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, "SELECT pg_advisory_xact_lock(727000)", ct);
        await Exec(conn, tx, "CREATE TABLE IF NOT EXISTS schema_migrations(name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", ct);
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).OrderBy(n => n, StringComparer.Ordinal))
        {
            await using (var chk = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE name=@n", conn, tx))
            {
                chk.Parameters.AddWithValue("n", res);
                if (await chk.ExecuteScalarAsync(ct) is not null) continue;
            }
            using var rd = new StreamReader(asm.GetManifestResourceStream(res)!);
            await Exec(conn, tx, await rd.ReadToEndAsync(ct), ct);
            await using var ins = new NpgsqlCommand("INSERT INTO schema_migrations(name) VALUES(@n)", conn, tx);
            ins.Parameters.AddWithValue("n", res);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, t);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class PgCameraStore(NpgsqlDataSource ds) : ICameraStore
{
    internal const string Cols = "camera_id, name, source_url, analysis_fps, enabled, plan_id, tags, updated_at, stream_url, provision_error, pinned_worker, generation, analysis_stream_url, motion_enabled, motion_sensitivity, motion_heartbeat_s";

    internal static CameraConfig Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetBoolean(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<string[]>(6), r.GetInt64(7),
        r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9))
    {
        PinnedWorker = r.IsDBNull(10) ? null : r.GetString(10),
        Generation = r.GetInt32(11),
        AnalysisStreamUrl = r.IsDBNull(12) ? null : r.GetString(12),
        Motion = new MotionConfig(r.GetBoolean(13), r.GetString(14), r.GetDouble(15)),
    };

    public async Task<IReadOnlyList<CameraConfig>> ListAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT {Cols} FROM cameras ORDER BY camera_id");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CameraConfig>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<CameraConfig?> GetAsync(string id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT {Cols} FROM cameras WHERE camera_id=@id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static void Bind(NpgsqlCommand cmd, CameraConfig c)
    {
        cmd.Parameters.AddWithValue("id", c.CameraId);
        cmd.Parameters.AddWithValue("name", c.Name);
        cmd.Parameters.AddWithValue("url", c.SourceUrl);
        cmd.Parameters.AddWithValue("fps", c.AnalysisFps);
        cmd.Parameters.AddWithValue("en", c.Enabled);
        cmd.Parameters.Add(new NpgsqlParameter("plan", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)c.PlanId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("tags", c.Tags);
        cmd.Parameters.AddWithValue("ts", c.UpdatedAt);
        cmd.Parameters.AddWithValue("surl", c.StreamUrl);
        cmd.Parameters.Add(new NpgsqlParameter("perr", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)c.ProvisionError ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("aurl", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)c.AnalysisStreamUrl ?? DBNull.Value });
        cmd.Parameters.AddWithValue("men", c.Motion.Enabled);
        cmd.Parameters.AddWithValue("msens", c.Motion.Sensitivity);
        cmd.Parameters.AddWithValue("mhb", c.Motion.HeartbeatS);
    }

    // Camera writes take the same advisory lock as assignment so a heartbeat can never persist an
    // assignment to a camera that is being deleted/disabled concurrently.
    private async Task<bool> InLockAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lk = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({PgAssignmentStore.LockKey})", conn, tx))
            await lk.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        bind(cmd);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return n > 0;
    }

    public Task<bool> InsertAsync(CameraConfig c, CancellationToken ct) => InLockAsync(
        "INSERT INTO cameras(camera_id,name,source_url,analysis_fps,enabled,plan_id,tags,updated_at,stream_url,provision_error,analysis_stream_url,motion_enabled,motion_sensitivity,motion_heartbeat_s) VALUES(@id,@name,@url,@fps,@en,@plan,@tags,@ts,@surl,@perr,@aurl,@men,@msens,@mhb) ON CONFLICT DO NOTHING",
        cmd => Bind(cmd, c), ct);

    public Task<bool> UpdateAsync(CameraConfig c, CancellationToken ct) => InLockAsync(
        "UPDATE cameras SET name=@name,source_url=@url,analysis_fps=@fps,enabled=@en,plan_id=@plan,tags=@tags,updated_at=@ts,stream_url=@surl,provision_error=@perr,analysis_stream_url=@aurl,motion_enabled=@men,motion_sensitivity=@msens,motion_heartbeat_s=@mhb WHERE camera_id=@id",
        cmd => Bind(cmd, c), ct);

    public Task<bool> DeleteAsync(string id, CancellationToken ct) => InLockAsync(
        "DELETE FROM cameras WHERE camera_id=@id", cmd => cmd.Parameters.AddWithValue("id", id), ct);

    public Task<bool> SetProvisionErrorAsync(string id, string? error, CancellationToken ct) => InLockAsync(
        "UPDATE cameras SET provision_error=@perr WHERE camera_id=@id",
        cmd => { cmd.Parameters.AddWithValue("id", id); cmd.Parameters.Add(new NpgsqlParameter("perr", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value }); }, ct);

    public Task<bool> SetPinAsync(string id, string? workerId, CancellationToken ct) => InLockAsync(
        "UPDATE cameras SET pinned_worker=@w WHERE camera_id=@id",
        cmd => { cmd.Parameters.AddWithValue("id", id); cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)workerId ?? DBNull.Value }); }, ct);

    public Task<bool> BumpGenerationAsync(string id, CancellationToken ct) => InLockAsync(
        "UPDATE cameras SET generation=generation+1 WHERE camera_id=@id", cmd => cmd.Parameters.AddWithValue("id", id), ct);

    public async Task<IReadOnlyDictionary<string, string>> AssignmentsAsync(CancellationToken ct)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = ds.CreateCommand("SELECT camera_id, worker_id FROM assignments");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT count(*) FROM cameras");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

public sealed class PgAssignmentStore(NpgsqlDataSource ds) : IAssignmentStore
{
    public const long LockKey = 727001;

    public async Task<T> RunExclusiveAsync<T>(Func<IAssignmentTx, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lk = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({LockKey})", conn, tx))
            await lk.ExecuteNonQueryAsync(ct);
        var result = await work(new Tx(conn, tx));
        await tx.CommitAsync(ct);
        return result;
    }

    private sealed class Tx(NpgsqlConnection conn, NpgsqlTransaction tx) : IAssignmentTx
    {
        public async Task<AssignmentSnapshot> LoadAsync(CancellationToken ct)
        {
            var workers = new List<WorkerRow>();
            await using (var c = new NpgsqlCommand("SELECT worker_id, capacity, last_seen_ms, capacity_override, draining FROM workers", conn, tx))
            await using (var r = await c.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) workers.Add(new WorkerRow(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetBoolean(4)));

            var assign = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var c = new NpgsqlCommand("SELECT camera_id, worker_id FROM assignments", conn, tx))
            await using (var r = await c.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) assign[r.GetString(0)] = r.GetString(1);

            var cams = new List<CameraConfig>();
            await using (var c = new NpgsqlCommand($"SELECT {PgCameraStore.Cols} FROM cameras WHERE enabled", conn, tx))
            await using (var r = await c.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) cams.Add(PgCameraStore.Read(r));
            return new AssignmentSnapshot(workers, assign, cams);
        }

        public async Task SaveAsync(AssignmentDiff d, CancellationToken ct)
        {
            if (d.UpsertWorker is { } w)
            {
                await using var c = new NpgsqlCommand(
                    "INSERT INTO workers(worker_id,capacity,last_seen_ms) VALUES(@w,@c,@t) ON CONFLICT (worker_id) DO UPDATE SET capacity=@c, last_seen_ms=@t", conn, tx);
                c.Parameters.AddWithValue("w", w.WorkerId);
                c.Parameters.AddWithValue("c", w.Capacity);
                c.Parameters.AddWithValue("t", w.LastSeenMs);
                await c.ExecuteNonQueryAsync(ct);
            }
            if (d.UpdateSettings is { } st)
            {
                await using var c = new NpgsqlCommand("UPDATE workers SET capacity_override=@o, draining=@d WHERE worker_id=@w", conn, tx);
                c.Parameters.AddWithValue("w", st.WorkerId);
                c.Parameters.Add(new NpgsqlParameter("o", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)st.CapacityOverride ?? DBNull.Value });
                c.Parameters.AddWithValue("d", st.Draining);
                await c.ExecuteNonQueryAsync(ct);
            }
            if (d.DeleteWorkers.Count > 0)
            {
                await using var c = new NpgsqlCommand("DELETE FROM workers WHERE worker_id = ANY(@ids)", conn, tx);
                c.Parameters.AddWithValue("ids", d.DeleteWorkers.ToArray());
                await c.ExecuteNonQueryAsync(ct);
            }
            if (d.DeleteAssignments.Count > 0)
            {
                await using var c = new NpgsqlCommand("DELETE FROM assignments WHERE camera_id = ANY(@ids)", conn, tx);
                c.Parameters.AddWithValue("ids", d.DeleteAssignments.ToArray());
                await c.ExecuteNonQueryAsync(ct);
            }
            if (d.UpsertAssignments.Count > 0)
            {
                await using var c = new NpgsqlCommand(
                    "INSERT INTO assignments(camera_id, worker_id) SELECT * FROM unnest(@cams, @workers) ON CONFLICT (camera_id) DO UPDATE SET worker_id = EXCLUDED.worker_id", conn, tx);
                c.Parameters.AddWithValue("cams", d.UpsertAssignments.Keys.ToArray());
                c.Parameters.AddWithValue("workers", d.UpsertAssignments.Values.ToArray());
                await c.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
