using System.Reflection;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Results;

public static class Migrator
{
    public static async Task RunAsync(NpgsqlDataSource ds, Assembly asm, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, "SELECT pg_advisory_xact_lock(727010)", ct);
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

public sealed class PgResultsStore(NpgsqlDataSource ds) : IResultsStore
{
    public async Task<IReadOnlyDictionary<TP, long>> LoadOffsetsAsync(CancellationToken ct)
    {
        var d = new Dictionary<TP, long>();
        await using var cmd = ds.CreateCommand("SELECT topic, partition, next_offset FROM consumer_offsets");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) d[new TP(r.GetString(0), r.GetInt32(1))] = r.GetInt64(2);
        return d;
    }

    public async Task CommitBatchAsync(BatchWrite w, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1. fence: lock the offset rows; if somebody already advanced past our batch start, abort
        foreach (var (tp, range) in w.Offsets.OrderBy(k => k.Key.Topic, StringComparer.Ordinal).ThenBy(k => k.Key.Partition))
        {
            await using var q = new NpgsqlCommand("SELECT next_offset FROM consumer_offsets WHERE topic=@t AND partition=@p FOR UPDATE", conn, tx);
            q.Parameters.AddWithValue("t", tp.Topic);
            q.Parameters.AddWithValue("p", tp.Partition);
            if (await q.ExecuteScalarAsync(ct) is long stored && stored > range.First)
                throw new OffsetFencedException($"{tp.Topic}[{tp.Partition}] stored {stored} > batch start {range.First}");
        }

        // 2. recognitions (idempotent)
        if (w.Recognitions.Count > 0)
        {
            var rs = w.Recognitions;
            await using var c = new NpgsqlCommand(@"
INSERT INTO recognitions(recognition_id,type,camera_id,frame_id,ts,value,person_id,confidence,box,snapshot_key,plan_id)
SELECT id,type,cam,frame,ts,val,person,conf,box::jsonb,skey,plan
FROM unnest(@id,@type,@cam,@frame,@ts,@val,@person,@conf,@box,@skey,@plan)
     AS t(id,type,cam,frame,ts,val,person,conf,box,skey,plan)
ON CONFLICT (recognition_id) DO NOTHING", conn, tx);
            c.Parameters.AddWithValue("id", rs.Select(x => x.R.RecognitionId).ToArray());
            c.Parameters.AddWithValue("type", rs.Select(x => x.R.Type).ToArray());
            c.Parameters.AddWithValue("cam", rs.Select(x => x.R.CameraId).ToArray());
            c.Parameters.AddWithValue("frame", rs.Select(x => x.R.FrameId ?? "").ToArray());
            c.Parameters.AddWithValue("ts", rs.Select(x => x.R.Ts).ToArray());
            c.Parameters.AddWithValue("val", rs.Select(x => x.R.Value ?? "").ToArray());
            c.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rs.Select(x => x.R.PersonId).ToArray() });
            c.Parameters.AddWithValue("conf", rs.Select(x => x.R.Confidence).ToArray());
            c.Parameters.Add(new NpgsqlParameter("box", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rs.Select(x => x.R.Box is null ? null : JsonSerializer.Serialize(x.R.Box)).ToArray() });
            c.Parameters.Add(new NpgsqlParameter("skey", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rs.Select(x => x.SnapshotKey).ToArray() });
            c.Parameters.Add(new NpgsqlParameter("plan", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rs.Select(x => x.R.PlanId).ToArray() });
            await c.ExecuteNonQueryAsync(ct);
        }

        // 3. aggregates: additive upsert (one pre-aggregated row per key => no "row affected twice")
        if (w.Aggregates.Count > 0)
        {
            var keys = w.Aggregates.Keys.ToArray();
            await using var c = new NpgsqlCommand(@"
INSERT INTO detection_counts(camera_id,class,minute_ts,count)
SELECT * FROM unnest(@cam,@cls,@min,@cnt)
ON CONFLICT (camera_id,class,minute_ts) DO UPDATE SET count = detection_counts.count + EXCLUDED.count", conn, tx);
            c.Parameters.AddWithValue("cam", keys.Select(k => k.CameraId).ToArray());
            c.Parameters.AddWithValue("cls", keys.Select(k => k.Class).ToArray());
            c.Parameters.AddWithValue("min", keys.Select(k => k.MinuteTs).ToArray());
            c.Parameters.AddWithValue("cnt", keys.Select(k => w.Aggregates[k]).ToArray());
            await c.ExecuteNonQueryAsync(ct);
        }

        // 4. offsets, same transaction
        foreach (var (tp, range) in w.Offsets)
        {
            await using var c = new NpgsqlCommand(
                "INSERT INTO consumer_offsets(topic,partition,next_offset) VALUES(@t,@p,@n) ON CONFLICT (topic,partition) DO UPDATE SET next_offset = GREATEST(consumer_offsets.next_offset, EXCLUDED.next_offset)", conn, tx);
            c.Parameters.AddWithValue("t", tp.Topic);
            c.Parameters.AddWithValue("p", tp.Partition);
            c.Parameters.AddWithValue("n", range.Next);
            await c.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<RecognitionDto>> QueryRecognitionsAsync(string? cameraId, string? type, long? since, int limit, CancellationToken ct, string? personId = null)
    {
        await using var cmd = ds.CreateCommand(@"
SELECT recognition_id,type,camera_id,frame_id,ts,value,person_id,confidence,box::text,plan_id,snapshot_key
FROM recognitions
WHERE (@cam::text IS NULL OR camera_id=@cam) AND (@type::text IS NULL OR type=@type) AND (@since::bigint IS NULL OR ts>=@since) AND (@person::text IS NULL OR person_id=@person)
ORDER BY ts DESC LIMIT @lim");
        cmd.Parameters.Add(new NpgsqlParameter("cam", NpgsqlDbType.Text) { Value = (object?)cameraId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = (object?)type ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.Bigint) { Value = (object?)since ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Text) { Value = (object?)personId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lim", limit);
        var list = new List<RecognitionDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = r.IsDBNull(10) ? null : r.GetString(10);
            list.Add(new RecognitionDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetDouble(7),
                r.IsDBNull(8) ? null : JsonSerializer.Deserialize<double[]>(r.GetString(8)),
                r.IsDBNull(9) ? null : r.GetString(9), key, key is null ? null : "/api/snapshots/" + key));
        }
        return list;
    }

    public async Task<int> DeleteRecognitionsOlderThanAsync(long cutoffTsMs, int batch, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("DELETE FROM recognitions WHERE recognition_id IN (SELECT recognition_id FROM recognitions WHERE ts < @cutoff ORDER BY ts LIMIT @n)");
        cmd.Parameters.AddWithValue("cutoff", cutoffTsMs);
        cmd.Parameters.AddWithValue("n", batch);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DetectionStat>> QueryStatsAsync(string? cameraId, long fromMinuteTs, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
SELECT camera_id,class,minute_ts,count FROM detection_counts
WHERE minute_ts >= @from AND (@cam::text IS NULL OR camera_id=@cam)
ORDER BY minute_ts, camera_id, class");
        cmd.Parameters.AddWithValue("from", fromMinuteTs);
        cmd.Parameters.Add(new NpgsqlParameter("cam", NpgsqlDbType.Text) { Value = (object?)cameraId ?? DBNull.Value });
        var list = new List<DetectionStat>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new DetectionStat(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        return list;
    }
}
