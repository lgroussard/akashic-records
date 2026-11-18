using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Hub;

/// <summary>
/// The hub's own SQLite file: `sync_log` (append-only, the source of truth) and `entities`
/// (current state per entity), as in MOBILITE-SYNC.md section 3.
/// Order comes from `seq` only; timestamps are stored for display and never compared.
/// </summary>
public sealed partial class HubStore
{
    private readonly string _connectionString;
    // One writer at a time: revisions are allocated by reading then writing `entities`.
    private readonly object _writeLock = new();

    public HubStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        DbPath = Path.GetFullPath(dbPath);
        EnsureCreated();
    }

    public string DbPath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void EnsureCreated()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS sync_log(
                seq          INTEGER PRIMARY KEY AUTOINCREMENT,
                entity_type  TEXT    NOT NULL,
                entity_id    TEXT    NOT NULL,
                rev          INTEGER NOT NULL,
                device_id    TEXT    NOT NULL,
                payload      TEXT,
                deleted      INTEGER NOT NULL,
                committed_at TEXT    NOT NULL,
                mutation_id  TEXT    NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS entities(
                entity_type  TEXT    NOT NULL,
                entity_id    TEXT    NOT NULL,
                rev          INTEGER NOT NULL,
                payload      TEXT,
                deleted      INTEGER NOT NULL,
                updated_at   TEXT    NOT NULL,
                last_device  TEXT    NOT NULL,
                PRIMARY KEY(entity_type, entity_id)
            );
            CREATE TABLE IF NOT EXISTS devices(
                device_id    TEXT    PRIMARY KEY,
                name         TEXT    NOT NULL,
                token_hash   TEXT    NOT NULL UNIQUE,   -- SHA-256 of the token; the token itself is never stored
                created_at   TEXT    NOT NULL,
                last_seen_at TEXT,
                revoked      INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS pair_codes(
                code_hash    TEXT    PRIMARY KEY,
                expires_at   INTEGER NOT NULL,          -- unix seconds, hub clock
                failures     INTEGER NOT NULL DEFAULT 0,
                used         INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public long HeadSeq()
    {
        using var c = Open();
        return HeadSeq(c, null);
    }

    private static long HeadSeq(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM sync_log";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Applies a batch. Each mutation is judged on its own (accepted / duplicate / conflict);
    /// every accepted one writes `sync_log` then `entities`, and the whole batch commits in a
    /// single transaction: a crash mid-batch leaves nothing behind.
    /// </summary>
    public PushResponse Push(string deviceId, IReadOnlyList<Mutation> mutations)
    {
        var results = new List<MutationResult>(mutations.Count);
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction(); // BEGIN IMMEDIATE
            foreach (var m in mutations)
            {
                // Idempotence: this exact mutation was already committed -> same answer, no write.
                var already = FindByMutationId(c, tx, m.MutationId);
                if (already is not null)
                {
                    results.Add(new MutationResult(m.MutationId, MutationStatus.Duplicate, already.Value.Rev, already.Value.Seq, null));
                    continue;
                }

                var current = ReadEntity(c, tx, m.EntityType, m.EntityId);
                var currentRev = current?.Rev ?? 0;
                if (m.BaseRev != currentRev)
                {
                    results.Add(new MutationResult(m.MutationId, MutationStatus.Conflict, currentRev, null, current));
                    continue;
                }

                var newRev = currentRev + 1;
                var now = DateTime.UtcNow.ToString("O"); // display only, never an ordering criterion
                var payload = m.Deleted ? null : m.Payload?.GetRawText();

                long seq;
                using (var ins = c.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO sync_log(entity_type, entity_id, rev, device_id, payload, deleted, committed_at, mutation_id)
                        VALUES ($t, $id, $rev, $dev, $p, $del, $at, $mid);
                        SELECT last_insert_rowid();
                        """;
                    ins.Parameters.AddWithValue("$t", m.EntityType);
                    ins.Parameters.AddWithValue("$id", m.EntityId);
                    ins.Parameters.AddWithValue("$rev", newRev);
                    ins.Parameters.AddWithValue("$dev", deviceId);
                    ins.Parameters.AddWithValue("$p", (object?)payload ?? DBNull.Value);
                    ins.Parameters.AddWithValue("$del", m.Deleted ? 1 : 0);
                    ins.Parameters.AddWithValue("$at", now);
                    ins.Parameters.AddWithValue("$mid", m.MutationId);
                    seq = (long)ins.ExecuteScalar()!;
                }

                using (var up = c.CreateCommand())
                {
                    up.Transaction = tx;
                    up.CommandText = """
                        INSERT INTO entities(entity_type, entity_id, rev, payload, deleted, updated_at, last_device)
                        VALUES ($t, $id, $rev, $p, $del, $at, $dev)
                        ON CONFLICT(entity_type, entity_id) DO UPDATE SET
                            rev = excluded.rev, payload = excluded.payload, deleted = excluded.deleted,
                            updated_at = excluded.updated_at, last_device = excluded.last_device;
                        """;
                    up.Parameters.AddWithValue("$t", m.EntityType);
                    up.Parameters.AddWithValue("$id", m.EntityId);
                    up.Parameters.AddWithValue("$rev", newRev);
                    up.Parameters.AddWithValue("$p", (object?)payload ?? DBNull.Value);
                    up.Parameters.AddWithValue("$del", m.Deleted ? 1 : 0);
                    up.Parameters.AddWithValue("$at", now);
                    up.Parameters.AddWithValue("$dev", deviceId);
                    up.ExecuteNonQuery();
                }

                results.Add(new MutationResult(m.MutationId, MutationStatus.Accepted, newRev, seq, null));
            }

            var head = HeadSeq(c, tx);
            tx.Commit();
            return new PushResponse(results, head);
        }
    }

    /// <summary>Journal rows with seq &gt; since, in seq order, tombstones included.</summary>
    public PullResponse Pull(long since, int limit)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: true); // one consistent read snapshot
        var rows = new List<LogRow>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT seq, entity_type, entity_id, rev, device_id, payload, deleted, committed_at
                FROM sync_log WHERE seq > $since ORDER BY seq LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$since", since);
            cmd.Parameters.AddWithValue("$n", limit + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new LogRow(
                    r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4),
                    r.IsDBNull(5) ? null : ParseJson(r.GetString(5)),
                    r.GetInt64(6) != 0, r.GetString(7)));
            }
        }
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var next = rows.Count > 0 ? rows[^1].Seq : since;
        return new PullResponse(rows, next, hasMore, HeadSeq(c, tx));
    }

    /// <summary>
    /// One page of the full current state (tombstones included), ordered by (entity_type, entity_id).
    /// `HeadSeq` is the journal position of this page: a client takes it from the FIRST page, loads
    /// every page, then pulls the journal from there - anything committed meanwhile is replayed.
    /// </summary>
    public SnapshotResponse Snapshot(string? entityType, string? afterType, string? afterId, int limit)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: true);
        var list = new List<SnapshotEntity>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT entity_type, entity_id, rev, payload, deleted, updated_at, last_device
                FROM entities
                WHERE ($type IS NULL OR entity_type = $type)
                  AND ($at IS NULL OR (entity_type, entity_id) > ($at, $aid))
                ORDER BY entity_type, entity_id LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$type", (object?)entityType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", (object?)afterType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$aid", (object?)afterId ?? "");
            cmd.Parameters.AddWithValue("$n", limit + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new SnapshotEntity(
                    r.GetString(0), r.GetString(1), r.GetInt64(2),
                    r.IsDBNull(3) ? null : ParseJson(r.GetString(3)),
                    r.GetInt64(4) != 0, r.GetString(5), r.GetString(6)));
            }
        }
        var hasMore = list.Count > limit;
        if (hasMore) list.RemoveAt(list.Count - 1);
        var last = hasMore ? list[^1] : null;
        return new SnapshotResponse(HeadSeq(c, tx), list, hasMore, last?.EntityType, last?.EntityId);
    }

    private static (long Seq, long Rev)? FindByMutationId(SqliteConnection c, SqliteTransaction tx, string mutationId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT seq, rev FROM sync_log WHERE mutation_id = $mid";
        cmd.Parameters.AddWithValue("$mid", mutationId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : null;
    }

    private static EntityState? ReadEntity(SqliteConnection c, SqliteTransaction tx, string type, string id)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT rev, payload, deleted, last_device, updated_at
            FROM entities WHERE entity_type = $t AND entity_id = $id
            """;
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new EntityState(
            r.GetInt64(0),
            r.IsDBNull(1) ? null : ParseJson(r.GetString(1)),
            r.GetInt64(2) != 0, r.GetString(3), r.GetString(4));
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
