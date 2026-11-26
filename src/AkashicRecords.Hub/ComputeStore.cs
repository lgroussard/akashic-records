using Microsoft.Data.Sqlite;

namespace AkashicRecords.Hub;

/// <summary>
/// One upscale request. Status: queued, running, done, failed. The input and the result are blobs (by hash) in
/// the hub's blob store: they stay on the hub, a device fetches the result when it wants to look at it.
/// </summary>
public sealed record ComputeJob(
    string Id, string Model, string InputHash, string InputName, long InputSize, string Status,
    string AddedAt, string AddedBy, string? Worker, string? WorkerName, string? StartedAt, string? FinishedAt,
    double? Seconds, string? ResultHash, long? ResultSize, string? ResultName, string? Error);

/// <summary>The shared job list (its own SQLite file next to hub.db: working state, not synchronised data).</summary>
public sealed class ComputeStore
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    private readonly string _connectionString;
    private readonly object _lock = new();

    public ComputeStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS cp_job(
                id          TEXT PRIMARY KEY,
                model       TEXT NOT NULL,
                input_hash  TEXT NOT NULL,
                input_name  TEXT NOT NULL,
                input_size  INTEGER NOT NULL,
                status      TEXT NOT NULL,
                added_at    TEXT NOT NULL,
                added_by    TEXT NOT NULL,
                worker      TEXT,
                worker_name TEXT,
                lease_until INTEGER,
                started_at  TEXT,
                finished_at TEXT,
                seconds     REAL,
                result_hash TEXT,
                result_size INTEGER,
                result_name TEXT,
                error       TEXT
            );
            """);
        // A job a worker was holding when the hub stopped is done again by whoever comes next.
        Exec(c, "UPDATE cp_job SET status = 'queued', worker = NULL, worker_name = NULL, lease_until = NULL, started_at = NULL WHERE status = 'running'");
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static int Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    private static List<ComputeJob> Query(SqliteConnection c, string where, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, model, input_hash, input_name, input_size, status, added_at, added_by, worker, worker_name,
                   started_at, finished_at, seconds, result_hash, result_size, result_name, error
            FROM cp_job {where}
            """;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<ComputeJob>();
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
            list.Add(new ComputeJob(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
                r.GetString(6), r.GetString(7), S(8), S(9), S(10), S(11), r.IsDBNull(12) ? null : r.GetDouble(12),
                S(13), r.IsDBNull(14) ? null : r.GetInt64(14), S(15), S(16)));
        return list;
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public List<ComputeJob> Jobs(int limit = 200)
    {
        using var c = Open();
        return Query(c, "ORDER BY added_at DESC, rowid DESC LIMIT $n", ("$n", limit));
    }

    public ComputeJob? Job(string id)
    {
        using var c = Open();
        return Query(c, "WHERE id = $id", ("$id", id)).FirstOrDefault();
    }

    public ComputeJob AddJob(string model, string inputHash, string inputName, long inputSize, string device)
    {
        lock (_lock)
        {
            using var c = Open();
            var id = Guid.NewGuid().ToString();
            Exec(c, """
                INSERT INTO cp_job(id, model, input_hash, input_name, input_size, status, added_at, added_by)
                VALUES ($id, $m, $h, $n, $s, 'queued', $at, $by)
                """, ("$id", id), ("$m", model), ("$h", inputHash), ("$n", inputName), ("$s", inputSize), ("$at", Now()), ("$by", device));
            return Query(c, "WHERE id = $id", ("$id", id)).Single();
        }
    }

    /// <summary>Gives the oldest waiting job this worker may take (marked running, held for a while); null if none.</summary>
    public ComputeJob? Claim(string worker, string workerName, Func<ComputeJob, bool> mayTake)
    {
        lock (_lock)
        {
            using var c = Open();
            // A worker that vanished mid-job gives it back once its hold has run out.
            Exec(c, "UPDATE cp_job SET status = 'queued', worker = NULL, worker_name = NULL, lease_until = NULL, started_at = NULL " +
                    "WHERE status = 'running' AND lease_until < $now", ("$now", NowMs()));
            var job = Query(c, "WHERE status = 'queued' ORDER BY added_at, rowid").FirstOrDefault(mayTake);
            if (job is null) return null;
            Exec(c, "UPDATE cp_job SET status = 'running', worker = $w, worker_name = $wn, lease_until = $l, started_at = $at WHERE id = $id",
                ("$w", worker), ("$wn", workerName), ("$l", NowMs() + (long)Lease.TotalMilliseconds), ("$at", Now()), ("$id", job.Id));
            return Query(c, "WHERE id = $id", ("$id", job.Id)).Single();
        }
    }

    /// <summary>Records the result of a job; only the worker holding it can.</summary>
    public bool Complete(string id, string worker, string resultHash, long resultSize, string resultName, double seconds)
    {
        lock (_lock)
        {
            using var c = Open();
            return Exec(c, """
                UPDATE cp_job SET status = 'done', result_hash = $h, result_size = $s, result_name = $n, seconds = $sec,
                                  finished_at = $at, lease_until = NULL, error = NULL
                WHERE id = $id AND status = 'running' AND worker = $w
                """, ("$h", resultHash), ("$s", resultSize), ("$n", resultName), ("$sec", seconds), ("$at", Now()), ("$id", id), ("$w", worker)) > 0;
        }
    }

    public bool Fail(string id, string worker, string error)
    {
        lock (_lock)
        {
            using var c = Open();
            return Exec(c, "UPDATE cp_job SET status = 'failed', error = $e, finished_at = $at, lease_until = NULL " +
                           "WHERE id = $id AND status = 'running' AND worker = $w",
                ("$e", error.Length > 500 ? error[..500] : error), ("$at", Now()), ("$id", id), ("$w", worker)) > 0;
        }
    }

    public bool Retry(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            return Exec(c, "UPDATE cp_job SET status = 'queued', error = NULL, worker = NULL, worker_name = NULL, started_at = NULL, finished_at = NULL " +
                           "WHERE id = $id AND status = 'failed'", ("$id", id)) > 0;
        }
    }

    /// <summary>Removes a job from the list (never one being worked on). Its files stay in the blob store.</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            return Exec(c, "DELETE FROM cp_job WHERE id = $id AND status <> 'running'", ("$id", id)) > 0;
        }
    }

    /// <summary>The jobs a worker holds right now (to say it is busy).</summary>
    public HashSet<string> BusyWorkers()
    {
        using var c = Open();
        return Query(c, "WHERE status = 'running' AND lease_until >= $now", ("$now", NowMs()))
            .Select(j => j.Worker!).ToHashSet();
    }
}
