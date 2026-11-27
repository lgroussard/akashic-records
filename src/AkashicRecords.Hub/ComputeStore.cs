using Microsoft.Data.Sqlite;

namespace AkashicRecords.Hub;

/// <summary>
/// One request of shared computing. Kind: null or "upscale" (Nomos ×4, result beside the original), "edit" (FLUX Kontext
/// following a sentence, optionally only inside a mask) or "restore" (Kontext cleans the picture up, then Nomos ×4); an
/// edit or a restore of a picture of the app replaces it as a revertible edit. Params: the edit's JSON (prompt,
/// mask_hash, mask_text, erase). Status: queued, running, done, failed. The input and the result are blobs (by hash) in
/// the hub's blob store: they stay on the hub, a device fetches the result when it wants to look at it.
/// </summary>
public sealed record ComputeJob(
    string Id, string Model, string InputHash, string InputName, long InputSize, string Status,
    string AddedAt, string AddedBy, string? Worker, string? WorkerName, string? StartedAt, string? FinishedAt,
    double? Seconds, string? ResultHash, long? ResultSize, string? ResultName, string? Error,
    string? AttachType = null, string? AttachId = null, string? AttachedPath = null, string? AttachError = null,
    string? Kind = null, string? Params = null, string? Results = null);

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
        // Added later: the image of the app a job came from (it gets its result next to it), and what became of that.
        foreach (var col in new[] { "attach_type", "attach_id", "attached_path", "attach_error", "kind", "params", "results" })
        {
            using var has = c.CreateCommand();
            has.CommandText = "SELECT COUNT(*) FROM pragma_table_info('cp_job') WHERE name = $n";
            has.Parameters.AddWithValue("$n", col);
            if ((long)has.ExecuteScalar()! == 0) Exec(c, $"ALTER TABLE cp_job ADD COLUMN {col} TEXT");
        }
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
                   started_at, finished_at, seconds, result_hash, result_size, result_name, error,
                   attach_type, attach_id, attached_path, attach_error, kind, params, results
            FROM cp_job {where}
            """;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<ComputeJob>();
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
            list.Add(new ComputeJob(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
                r.GetString(6), r.GetString(7), S(8), S(9), S(10), S(11), r.IsDBNull(12) ? null : r.GetDouble(12),
                S(13), r.IsDBNull(14) ? null : r.GetInt64(14), S(15), S(16), S(17), S(18), S(19), S(20), S(21), S(22), S(23)));
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

    public ComputeJob AddJob(string model, string inputHash, string inputName, long inputSize, string device,
        string? attachType = null, string? attachId = null, string? kind = null, string? parameters = null)
    {
        lock (_lock)
        {
            using var c = Open();
            var id = Guid.NewGuid().ToString();
            Exec(c, """
                INSERT INTO cp_job(id, model, input_hash, input_name, input_size, status, added_at, added_by, attach_type, attach_id, kind, params)
                VALUES ($id, $m, $h, $n, $s, 'queued', $at, $by, $att, $aid, $k, $p)
                """, ("$id", id), ("$m", model), ("$h", inputHash), ("$n", inputName), ("$s", inputSize), ("$at", Now()), ("$by", device),
                ("$att", attachType), ("$aid", attachId), ("$k", kind), ("$p", parameters));
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

    /// <summary>The versions of a job asked with several tries (JSON array of blob hashes), waiting for a choice.</summary>
    public void SetResults(string id, string resultsJson)
    {
        lock (_lock)
        {
            using var c = Open();
            Exec(c, "UPDATE cp_job SET results = $r WHERE id = $id", ("$r", resultsJson), ("$id", id));
        }
    }

    /// <summary>The version kept among several: it becomes the job's result.</summary>
    public void Chose(string id, string resultHash, long resultSize)
    {
        lock (_lock)
        {
            using var c = Open();
            Exec(c, "UPDATE cp_job SET result_hash = $h, result_size = $s WHERE id = $id", ("$h", resultHash), ("$s", resultSize), ("$id", id));
        }
    }

    /// <summary>What became of putting the result next to its original: the new file's path, or why it failed.</summary>
    public void Attached(string id, string? path, string? error)
    {
        lock (_lock)
        {
            using var c = Open();
            Exec(c, "UPDATE cp_job SET attached_path = $p, attach_error = $e WHERE id = $id",
                ("$p", path), ("$e", error is { Length: > 500 } ? error[..500] : error), ("$id", id));
        }
    }

    /// <summary>The jobs a worker holds right now (to say it is busy).</summary>
    public HashSet<string> BusyWorkers()
    {
        using var c = Open();
        return Query(c, "WHERE status = 'running' AND lease_until >= $now", ("$now", NowMs()))
            .Select(j => j.Worker!).ToHashSet();
    }

    /// <summary>Every blob a listed job still needs (its input, mask, results and their versions): the clean-up
    /// (BlobCleaner.cs) never removes those. Any 64-hex value of a row counts, whatever column holds it.</summary>
    public HashSet<string> ReferencedHashes()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM cp_job";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            for (var i = 0; i < r.FieldCount; i++)
                if (!r.IsDBNull(i) && r.GetValue(i) is string s)
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, "[0-9a-fA-F]{64}"))
                        set.Add(m.Value.ToLowerInvariant());
        return set;
    }
}
