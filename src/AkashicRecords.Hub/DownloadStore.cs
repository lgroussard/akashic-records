using Microsoft.Data.Sqlite;

namespace AkashicRecords.Hub;

/// <summary>A placement zone of the download board: where its tracks go (a music folder, "" = the root).</summary>
public sealed record DownloadZone(string Id, string Name, string Folder, string CreatedAt);

/// <summary>One requested track. Status: queued, running, done, failed.</summary>
public sealed record DownloadJob(
    string Id, string Query, string Label, string? ZoneId, string Status, string? Error,
    string AddedAt, string AddedBy, string? FinishedAt, string? TrackPath, string? Folder);

/// <summary>
/// The download board shared by every device: the queue of requested tracks and the zones they go into. Its own
/// SQLite file next to hub.db (it is the hub's working state, not synchronised data). A zone with nothing left
/// to download goes away by itself once its tracks are done: the folder stays in the music, the board stays short.
/// </summary>
public sealed class DownloadStore
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public DownloadStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS dl_zone(
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                folder     TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS dl_job(
                id          TEXT PRIMARY KEY,
                query       TEXT NOT NULL,
                label       TEXT NOT NULL,
                zone_id     TEXT,
                status      TEXT NOT NULL,
                error       TEXT,
                added_at    TEXT NOT NULL,
                added_by    TEXT NOT NULL,
                not_before  INTEGER NOT NULL,
                finished_at TEXT,
                track_path  TEXT,
                folder      TEXT
            );
            """);
        // A job left "running" by a stop of the hub starts again.
        Exec(c, "UPDATE dl_job SET status = 'queued' WHERE status = 'running'");
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> read, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DownloadZone ReadZone(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
    private const string ZoneColumns = "id, name, folder, created_at";

    private static DownloadJob ReadJob(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), S(r, 3), r.GetString(4), S(r, 5), r.GetString(6), r.GetString(7),
        S(r, 8), S(r, 9), S(r, 10));
    private const string JobColumns = "id, query, label, zone_id, status, error, added_at, added_by, finished_at, track_path, folder";

    /// <summary>One safe folder name (same rule as the apps' downloaders): no separator, nothing reserved; "" = the root.</summary>
    public static string SanitizeFolder(string? raw)
    {
        var name = (raw ?? "").Trim().TrimEnd('/', '\\');
        if (name.Length == 0) return "";
        name = name.Split('/', '\\')[^1];
        name = new string(name.Select(ch => "<>:\"/\\|?*".Contains(ch) || ch < 32 ? '_' : ch).ToArray()).Trim();
        return name is "." or ".." ? "" : name;
    }

    public List<DownloadZone> Zones()
    {
        using var c = Open();
        return Query(c, $"SELECT {ZoneColumns} FROM dl_zone ORDER BY created_at DESC", ReadZone);
    }

    /// <summary>Everything still to do or failed, and the last finished ones.</summary>
    public List<DownloadJob> Jobs(int doneKept = 40)
    {
        using var c = Open();
        return Query(c,
            $"SELECT {JobColumns} FROM dl_job WHERE status <> 'done' " +
            $"UNION ALL SELECT * FROM (SELECT {JobColumns} FROM dl_job WHERE status = 'done' ORDER BY finished_at DESC LIMIT $n) " +
            "ORDER BY added_at", ReadJob, ("$n", doneKept));
    }

    public DownloadJob? Job(string id)
    {
        using var c = Open();
        return Query(c, $"SELECT {JobColumns} FROM dl_job WHERE id = $id", ReadJob, ("$id", id)).FirstOrDefault();
    }

    public DownloadZone? Zone(string id)
    {
        using var c = Open();
        return Query(c, $"SELECT {ZoneColumns} FROM dl_zone WHERE id = $id", ReadZone, ("$id", id)).FirstOrDefault();
    }

    /// <summary>A zone for a folder: the existing one for that folder, or a new one.</summary>
    public DownloadZone AddZone(string? name, string? folder)
    {
        var f = SanitizeFolder(folder ?? name);
        var n = string.IsNullOrWhiteSpace(name) ? (f.Length > 0 ? f : "Racine") : name.Trim();
        if (n.Length > 100) n = n[..100];
        lock (_lock)
        {
            using var c = Open();
            var same = Query(c, $"SELECT {ZoneColumns} FROM dl_zone WHERE lower(folder) = lower($f)", ReadZone, ("$f", f)).FirstOrDefault();
            if (same is not null) return same;
            var zone = new DownloadZone(Guid.NewGuid().ToString(), n, f, Now());
            Exec(c, "INSERT INTO dl_zone(id, name, folder, created_at) VALUES ($id, $n, $f, $at)",
                ("$id", zone.Id), ("$n", zone.Name), ("$f", zone.Folder), ("$at", zone.CreatedAt));
            return zone;
        }
    }

    public bool RenameZone(string id, string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE dl_zone SET name = $n WHERE id = $id";
        cmd.Parameters.AddWithValue("$n", name.Trim().Length > 100 ? name.Trim()[..100] : name.Trim());
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Removes a zone; its waiting tracks go back to "unsorted" (the root).</summary>
    public bool DeleteZone(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            Exec(c, "UPDATE dl_job SET zone_id = NULL WHERE zone_id = $id AND status <> 'done'", ("$id", id));
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM dl_zone WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Queues a track. It waits a few seconds before starting, time enough to move it to another zone.</summary>
    public DownloadJob AddJob(string query, string label, string? zoneId, string device, int graceSeconds = 10)
    {
        lock (_lock)
        {
            using var c = Open();
            var job = new DownloadJob(Guid.NewGuid().ToString(), query, label, zoneId, "queued", null, Now(), device, null, null, null);
            Exec(c, """
                INSERT INTO dl_job(id, query, label, zone_id, status, added_at, added_by, not_before)
                VALUES ($id, $q, $l, $z, 'queued', $at, $by, $nb)
                """, ("$id", job.Id), ("$q", query), ("$l", label), ("$z", zoneId), ("$at", job.AddedAt), ("$by", device),
                ("$nb", DateTimeOffset.UtcNow.AddSeconds(graceSeconds).ToUnixTimeMilliseconds()));
            return job;
        }
    }

    /// <summary>Moves a waiting (or failed) track to another zone (null = unsorted).</summary>
    public bool MoveJob(string id, string? zoneId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE dl_job SET zone_id = $z WHERE id = $id AND status IN ('queued', 'failed')";
        cmd.Parameters.AddWithValue("$z", (object?)zoneId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Retry(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE dl_job SET status = 'queued', error = NULL, not_before = 0 WHERE id = $id AND status = 'failed'";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Removes a track from the board (never one being downloaded). Its file, if any, stays in the music.</summary>
    public bool DeleteJob(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM dl_job WHERE id = $id AND status <> 'running'";
            cmd.Parameters.AddWithValue("$id", id);
            var removed = cmd.ExecuteNonQuery() > 0;
            if (removed) DropFinishedZones(c);
            return removed;
        }
    }

    public void ClearDone()
    {
        lock (_lock)
        {
            using var c = Open();
            Exec(c, "DELETE FROM dl_job WHERE status = 'done'");
        }
    }

    /// <summary>The next track to download, marked running; null when there is none (or none due yet).</summary>
    public (DownloadJob Job, string Folder)? TakeNext()
    {
        lock (_lock)
        {
            using var c = Open();
            var job = Query(c,
                $"SELECT {JobColumns} FROM dl_job WHERE status = 'queued' AND not_before <= $now ORDER BY added_at LIMIT 1",
                ReadJob, ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).FirstOrDefault();
            if (job is null) return null;
            var folder = job.ZoneId is null ? "" : Query(c, "SELECT folder FROM dl_zone WHERE id = $id", r => r.GetString(0), ("$id", job.ZoneId)).FirstOrDefault() ?? "";
            Exec(c, "UPDATE dl_job SET status = 'running', folder = $f WHERE id = $id", ("$f", folder), ("$id", job.Id));
            return (job, folder);
        }
    }

    public void Finish(string id, string? trackPath, string? error)
    {
        lock (_lock)
        {
            using var c = Open();
            Exec(c, "UPDATE dl_job SET status = $s, error = $e, track_path = $p, finished_at = $at WHERE id = $id",
                ("$s", error is null ? "done" : "failed"), ("$e", error), ("$p", trackPath), ("$at", Now()), ("$id", id));
            DropFinishedZones(c);
            // The board keeps the last finished tracks only.
            Exec(c, "DELETE FROM dl_job WHERE status = 'done' AND id NOT IN (SELECT id FROM dl_job WHERE status = 'done' ORDER BY finished_at DESC LIMIT 200)");
        }
    }

    // A zone whose tracks are all downloaded has done its job: it leaves the board (its folder stays in the music).
    // A zone just created, still empty, stays until something is put in it.
    private static void DropFinishedZones(SqliteConnection c) =>
        Exec(c, """
            DELETE FROM dl_zone WHERE id IN (SELECT zone_id FROM dl_job WHERE zone_id IS NOT NULL AND status = 'done')
              AND id NOT IN (SELECT zone_id FROM dl_job WHERE zone_id IS NOT NULL AND status <> 'done')
            """);
}
