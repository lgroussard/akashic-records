using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Sync;

/// <summary>One synchronised table. `LocalOnlyColumns` never leave the device (file paths, for
/// instance, mean nothing on another machine) and are kept as they are when a row is updated.</summary>
public sealed record SyncTable(string EntityType, string Table, params string[] LocalOnlyColumns);

public sealed class SyncReport
{
    public int Pulled, Inserted, Updated, Deleted, Adopted, Pushed, PushedDeletes, Conflicts;
    public long Cursor;
    public bool NothingToDo => Inserted + Updated + Deleted + Adopted + Pushed + PushedDeletes == 0;
    public override string ToString() =>
        $"recu {Pulled} (ajouts {Inserted}, modifs {Updated}, suppressions {Deleted}, reconnus {Adopted}) ; " +
        $"envoye {Pushed} (dont suppressions {PushedDeletes}) ; conflits {Conflicts} ; curseur {Cursor}";
}

/// <summary>Raised instead of doing something that looks like a mistake (see MaxDeleteShare).</summary>
public sealed class SyncSafetyException(string message) : Exception(message);

/// <summary>
/// Keeps the tables listed in `tables` of one local SQLite database in step with the hub.
///
/// The existing tables are not altered and the app's repositories are not involved: the engine
/// compares each row with what it last synchronised (a hash kept in `sync_map`) and derives what
/// to send. A row is the durable truth, the "outbox" is recomputed at every run, so a crash or a
/// cut connection loses nothing and resending is harmless (the mutation id is derived from the
/// content).
///
/// Conflict policy: last writer wins, per entity. A row changed here AND on the hub since the last
/// sync keeps this device's version and is pushed over the hub's.
/// </summary>
public sealed class SyncEngine
{
    private const int PullPage = 500;
    private const int PushBatch = 200;

    private readonly string _connectionString;
    private readonly HubClient _hub;
    private readonly IReadOnlyList<SyncTable> _tables;

    /// <summary>Safety brake: a run that would delete more than this share of a table's synchronised
    /// rows on the hub (and more than `MinDeletesForBrake`) stops instead. A database swapped under
    /// the engine looks exactly like "the user deleted everything".</summary>
    public double MaxDeleteShare { get; set; } = 0.30;
    public int MinDeletesForBrake { get; set; } = 5;

    public SyncEngine(string dbPath, HubClient hub, IReadOnlyList<SyncTable> tables)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _hub = hub;
        _tables = tables;
    }

    public async Task<SyncReport> RunAsync(CancellationToken ct = default)
    {
        var report = new SyncReport();
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        EnsureSchema(db);
        var tables = _tables.Select(t => TableInfo.Load(db, t)).Where(t => t is not null).Select(t => t!).ToList();

        await PullAsync(db, tables, report, ct);
        for (var round = 0; round < 4; round++)
        {
            var pending = tables.SelectMany(t => Scan(db, t)).ToList();
            if (pending.Count == 0) break;
            var conflicts = await PushAsync(db, pending, report, ct);
            await PullAsync(db, tables, report, ct);
            if (conflicts == 0) break; // otherwise: rebased on the hub's revision, send again
        }
        report.Cursor = GetCursor(db);
        return report;
    }

    // ------------------------------------------------------------------ schema & state

    private static void EnsureSchema(SqliteConnection db)
    {
        Exec(db, null, """
            CREATE TABLE IF NOT EXISTS sync_map(
                entity_type TEXT    NOT NULL,
                sync_id     TEXT    NOT NULL,   -- the entity's id on the hub, same on every device
                local_id    INTEGER,            -- this database's row id; NULL once deleted
                rev         INTEGER NOT NULL,   -- hub revision this device last saw
                synced_hash TEXT,               -- hash of the row as last synchronised; NULL = never sent
                deleted     INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(entity_type, sync_id)
            );
            CREATE INDEX IF NOT EXISTS sync_map_local ON sync_map(entity_type, local_id);
            CREATE TABLE IF NOT EXISTS sync_state(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    private static long GetCursor(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = Cmd(db, tx, "SELECT value FROM sync_state WHERE key = 'cursor'");
        return cmd.ExecuteScalar() is string s ? long.Parse(s, CultureInfo.InvariantCulture) : 0;
    }

    private static void SetCursor(SqliteConnection db, SqliteTransaction tx, long cursor) =>
        Exec(db, tx, "INSERT INTO sync_state(key, value) VALUES ('cursor', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$v", cursor.ToString(CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ pull

    private async Task PullAsync(SqliteConnection db, List<TableInfo> tables, SyncReport report, CancellationToken ct)
    {
        while (true)
        {
            var cursor = GetCursor(db);
            var page = await _hub.PullAsync(cursor, PullPage, ct);
            if (page.HeadSeq < cursor)
                throw new SyncSafetyException(
                    $"Le hub est en retard sur cet appareil (hub : {page.HeadSeq}, ici : {cursor}). " +
                    "Ce n'est pas le meme hub, ou il a ete reinitialise. Rien n'a ete modifie.");

            using (var tx = db.BeginTransaction())
            {
                foreach (var row in page.Rows)
                {
                    var table = tables.FirstOrDefault(t => t.Def.EntityType == row.EntityType);
                    if (table is null) continue; // a type this device does not synchronise
                    report.Pulled++;
                    Apply(db, tx, table, row, report);
                }
                SetCursor(db, tx, page.NextCursor);
                tx.Commit();
            }
            if (!page.HasMore) break;
        }
    }

    private static void Apply(SqliteConnection db, SqliteTransaction tx, TableInfo t, HubLogRow row, SyncReport report)
    {
        var map = MapEntry.BySyncId(db, tx, t.Def.EntityType, row.EntityId);
        var payload = row.Deleted || row.Payload is null ? null : t.Project(row.Payload.Value);

        if (map is null)
        {
            if (payload is null) { MapEntry.Upsert(db, tx, t.Def.EntityType, row.EntityId, null, row.Rev, null, true); return; }

            // First contact with this entity. If an identical row is already here and not yet linked
            // (both devices started from the same data), recognise it instead of creating a twin.
            var twin = t.FindUnmappedTwin(db, tx, payload);
            if (twin is not null)
            {
                MapEntry.Upsert(db, tx, t.Def.EntityType, row.EntityId, twin, row.Rev, t.HashOfRow(db, tx, twin.Value), false);
                report.Adopted++;
                return;
            }
            var id = t.Insert(db, tx, payload);
            MapEntry.Upsert(db, tx, t.Def.EntityType, row.EntityId, id, row.Rev, t.HashOfRow(db, tx, id), false);
            report.Inserted++;
            return;
        }

        if (row.Rev <= map.Rev) return; // already known (our own push coming back, or a replay)

        var localHash = map.LocalId is null ? null : t.HashOfRow(db, tx, map.LocalId.Value);
        var locallyChanged = !map.Deleted && (localHash is null || localHash != map.SyncedHash);
        if (locallyChanged && SameContent(db, tx, t, map, localHash, payload))
        {
            // Both sides hold the same thing (typically our own push whose answer got lost):
            // nothing to resolve, just take note of the revision.
            MapEntry.Upsert(db, tx, t.Def.EntityType, row.EntityId, payload is null ? null : map.LocalId, row.Rev, localHash, payload is null);
            return;
        }
        if (locallyChanged)
        {
            // Both sides changed. Last writer wins per entity: keep ours, remember the hub's
            // revision so the next push goes on top of it.
            // (local_id is kept even if the row is gone: that is how the scan sees a pending delete.)
            MapEntry.Upsert(db, tx, t.Def.EntityType, row.EntityId, map.LocalId, row.Rev, map.SyncedHash, false);
            report.Conflicts++;
            return;
        }

        if (payload is null)
        {
            if (map.LocalId is not null && localHash is not null) { t.Delete(db, tx, map.LocalId.Value); report.Deleted++; }
            MapEntry.Upsert(db, tx, t.Def.EntityType, row.EntityId, null, row.Rev, null, true);
        }
        else if (map.Deleted || map.LocalId is null)
        {
            var id = t.Insert(db, tx, payload); // deleted here earlier, alive again on the hub
            MapEntry.Upsert(db, tx, t.Def.EntityType, row.EntityId, id, row.Rev, t.HashOfRow(db, tx, id), false);
            report.Inserted++;
        }
        else
        {
            t.Update(db, tx, map.LocalId.Value, payload);
            MapEntry.Upsert(db, tx, t.Def.EntityType, row.EntityId, map.LocalId, row.Rev, t.HashOfRow(db, tx, map.LocalId.Value), false);
            report.Updated++;
        }
    }

    private static bool SameContent(SqliteConnection db, SqliteTransaction tx, TableInfo t, MapEntry map, string? localHash, SortedDictionary<string, object?>? payload)
    {
        if (payload is null) return localHash is null;          // deleted on both sides
        if (localHash is null || map.LocalId is null) return false;
        return t.RowMatches(db, tx, map.LocalId.Value, payload);
    }

    // ------------------------------------------------------------------ scan & push

    private sealed record Pending(TableInfo Table, string SyncId, long? LocalId, long BaseRev, string? Hash, string? PayloadJson)
    {
        public bool IsDelete => PayloadJson is null;
        // Derived from the content: sending the same change twice is the same mutation for the hub.
        public string MutationId => Sha($"{Table.Def.EntityType}|{SyncId}|{BaseRev}|{Hash ?? "deleted"}");
    }

    private List<Pending> Scan(SqliteConnection db, TableInfo t)
    {
        var pending = new List<Pending>();
        using var tx = db.BeginTransaction();
        var maps = MapEntry.All(db, tx, t.Def.EntityType);
        var byLocal = maps.Where(m => m.LocalId is not null && !m.Deleted).ToDictionary(m => m.LocalId!.Value);
        var seen = new HashSet<long>();

        foreach (var (id, values) in t.ReadAll(db, tx))
        {
            seen.Add(id);
            var json = TableInfo.Canonical(values);
            var hash = Sha(json);
            if (!byLocal.TryGetValue(id, out var map))
            {
                // New here. The id is stored before anything is sent, so a retry reuses it.
                var syncId = Guid.NewGuid().ToString();
                MapEntry.Upsert(db, tx, t.Def.EntityType, syncId, id, 0, null, false);
                pending.Add(new Pending(t, syncId, id, 0, hash, json));
            }
            else if (map.SyncedHash != hash)
            {
                pending.Add(new Pending(t, map.SyncId, id, map.Rev, hash, json));
            }
        }

        var deletes = byLocal.Values.Where(m => !seen.Contains(m.LocalId!.Value)).ToList();
        if (deletes.Count > MinDeletesForBrake && deletes.Count > byLocal.Count * MaxDeleteShare)
            throw new SyncSafetyException(
                $"{deletes.Count} {t.Def.EntityType} sur {byLocal.Count} ont disparu de cette base depuis la derniere synchro. " +
                "Cela ressemble a une base remplacee plutot qu'a des suppressions voulues : rien n'a ete envoye au hub.");
        pending.AddRange(deletes.Select(m => new Pending(t, m.SyncId, null, m.Rev, null, null)));

        tx.Commit();
        return pending;
    }

    private async Task<int> PushAsync(SqliteConnection db, List<Pending> pending, SyncReport report, CancellationToken ct)
    {
        var conflicts = 0;
        foreach (var batch in pending.Chunk(PushBatch))
        {
            var mutations = batch.Select(p => new HubMutation(
                p.MutationId, p.Table.Def.EntityType, p.SyncId, p.BaseRev,
                p.PayloadJson is null ? null : Parse(p.PayloadJson), p.IsDelete)).ToList();
            var response = await _hub.PushAsync(mutations, ct);

            using var tx = db.BeginTransaction();
            foreach (var (p, res) in batch.Zip(response.Results))
            {
                var type = p.Table.Def.EntityType;
                if (res.Status is "accepted" or "duplicate")
                {
                    MapEntry.Upsert(db, tx, type, p.SyncId, p.IsDelete ? null : p.LocalId, res.Rev, p.Hash, p.IsDelete);
                    report.Pushed++;
                    if (p.IsDelete) report.PushedDeletes++;
                }
                else
                {
                    // The hub moved since we last looked. Keep our version, take the hub's revision
                    // as the new base: the next round sends it again on top (last writer wins).
                    var map = MapEntry.BySyncId(db, tx, type, p.SyncId)!;
                    MapEntry.Upsert(db, tx, type, p.SyncId, map.LocalId, res.Rev, map.SyncedHash, false);
                    report.Conflicts++;
                    conflicts++;
                }
            }
            tx.Commit();
        }
        return conflicts;
    }

    // ------------------------------------------------------------------ sync_map rows

    private sealed record MapEntry(string SyncId, long? LocalId, long Rev, string? SyncedHash, bool Deleted)
    {
        public static MapEntry? BySyncId(SqliteConnection db, SqliteTransaction tx, string type, string syncId)
        {
            using var cmd = Cmd(db, tx, "SELECT sync_id, local_id, rev, synced_hash, deleted FROM sync_map WHERE entity_type = $t AND sync_id = $s",
                ("$t", type), ("$s", syncId));
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }

        public static List<MapEntry> All(SqliteConnection db, SqliteTransaction tx, string type)
        {
            using var cmd = Cmd(db, tx, "SELECT sync_id, local_id, rev, synced_hash, deleted FROM sync_map WHERE entity_type = $t", ("$t", type));
            using var r = cmd.ExecuteReader();
            var list = new List<MapEntry>();
            while (r.Read()) list.Add(Read(r));
            return list;
        }

        public static void Upsert(SqliteConnection db, SqliteTransaction tx, string type, string syncId, long? localId, long rev, string? hash, bool deleted) =>
            Exec(db, tx, """
                INSERT INTO sync_map(entity_type, sync_id, local_id, rev, synced_hash, deleted) VALUES ($t, $s, $l, $r, $h, $d)
                ON CONFLICT(entity_type, sync_id) DO UPDATE SET
                    local_id = excluded.local_id, rev = excluded.rev, synced_hash = excluded.synced_hash, deleted = excluded.deleted
                """, ("$t", type), ("$s", syncId), ("$l", localId), ("$r", rev), ("$h", hash), ("$d", deleted ? 1 : 0));

        private static MapEntry Read(SqliteDataReader r) => new(
            r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) != 0);
    }

    // ------------------------------------------------------------------ one local table

    private sealed class TableInfo
    {
        private sealed record Column(string Name, string Type, bool NotNull, bool HasDefault);

        public required SyncTable Def { get; init; }
        private string _pk = "Id";
        private List<Column> _columns = new(); // synchronised columns, sorted by name

        /// <summary>Reads the table's real columns: the engine follows the schema it finds.</summary>
        public static TableInfo? Load(SqliteConnection db, SyncTable def)
        {
            var info = new TableInfo { Def = def };
            using var cmd = Cmd(db, null, $"PRAGMA table_info(\"{def.Table}\")");
            using var r = cmd.ExecuteReader();
            var found = false;
            while (r.Read())
            {
                found = true;
                var name = r.GetString(1);
                if (r.GetInt64(5) != 0) { info._pk = name; continue; }
                if (def.LocalOnlyColumns.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                info._columns.Add(new Column(name, r.GetString(2).ToUpperInvariant(), r.GetInt64(3) != 0, !r.IsDBNull(4)));
            }
            info._columns = info._columns.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            return found ? info : null; // table absent from this database: nothing to synchronise
        }

        private static bool IsReal(Column c) => c.Type.Contains("REAL") || c.Type.Contains("FLOA") || c.Type.Contains("DOUB");

        private string ColumnList =>string.Join(", ", _columns.Select(c => $"\"{c.Name}\""));

        public IEnumerable<(long Id, SortedDictionary<string, object?> Values)> ReadAll(SqliteConnection db, SqliteTransaction tx)
        {
            using var cmd = Cmd(db, tx, $"SELECT \"{_pk}\", {ColumnList} FROM \"{Def.Table}\"");
            using var r = cmd.ExecuteReader();
            while (r.Read()) yield return (r.GetInt64(0), ReadValues(r));
        }

        public string? HashOfRow(SqliteConnection db, SqliteTransaction tx, long id)
        {
            using var cmd = Cmd(db, tx, $"SELECT \"{_pk}\", {ColumnList} FROM \"{Def.Table}\" WHERE \"{_pk}\" = $id", ("$id", id));
            using var r = cmd.ExecuteReader();
            return r.Read() ? Sha(Canonical(ReadValues(r))) : null;
        }

        private SortedDictionary<string, object?> ReadValues(SqliteDataReader r)
        {
            var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < _columns.Count; i++)
            {
                var v = r.GetValue(i + 1);
                values[_columns[i].Name] = v switch
                {
                    DBNull => null,
                    long or double or string => v,
                    byte[] => throw new NotSupportedException($"{Def.Table}.{_columns[i].Name} : colonne binaire non synchronisable."),
                    _ => Convert.ToString(v, CultureInfo.InvariantCulture),
                };
            }
            return values;
        }

        /// <summary>The part of a hub payload this database has columns for. A field this version
        /// does not know is ignored; a column the payload does not carry is left alone.</summary>
        public SortedDictionary<string, object?> Project(JsonElement payload)
        {
            var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            if (payload.ValueKind != JsonValueKind.Object) return values;
            foreach (var c in _columns)
            {
                if (!payload.TryGetProperty(c.Name, out var e)) continue;
                values[c.Name] = e.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => e.GetString(),
                    // Same type as SQLite gives back for this column, or 19 and 19.0 would look different.
                    // (The casts matter: without them C# widens the whole expression to double.)
                    JsonValueKind.Number => IsReal(c) ? e.GetDouble() : e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
                    JsonValueKind.True => 1L,
                    JsonValueKind.False => 0L,
                    _ => e.GetRawText(),
                };
            }
            return values;
        }

        /// <summary>True when the local row holds exactly what the payload says, for every field the payload carries.</summary>
        public bool RowMatches(SqliteConnection db, SqliteTransaction tx, long id, SortedDictionary<string, object?> payload)
        {
            if (payload.Count == 0) return false;
            using var cmd = Cmd(db, tx, $"SELECT \"{_pk}\", {ColumnList} FROM \"{Def.Table}\" WHERE \"{_pk}\" = $id", ("$id", id));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return false;
            var local = ReadValues(r);
            return payload.All(kv => Equals(local[kv.Key], kv.Value));
        }

        public long? FindUnmappedTwin(SqliteConnection db, SqliteTransaction tx, SortedDictionary<string, object?> payload)
        {
            if (payload.Count == 0) return null;
            using var cmd = Cmd(db, tx,
                $"SELECT \"{_pk}\", {ColumnList} FROM \"{Def.Table}\" " +
                $"WHERE \"{_pk}\" NOT IN (SELECT local_id FROM sync_map WHERE entity_type = $t AND local_id IS NOT NULL)",
                ("$t", Def.EntityType));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var local = ReadValues(r);
                if (payload.All(kv => Equals(local[kv.Key], kv.Value))) return r.GetInt64(0);
            }
            return null;
        }

        public long Insert(SqliteConnection db, SqliteTransaction tx, SortedDictionary<string, object?> payload)
        {
            var values = new SortedDictionary<string, object?>(payload, StringComparer.Ordinal);
            // A required column the sender does not have: an empty value rather than a failed insert.
            foreach (var c in _columns.Where(c => c.NotNull && !c.HasDefault && !values.ContainsKey(c.Name)))
                values[c.Name] = c.Type.Contains("INT") || c.Type.Contains("REAL") ? 0L : "";
            foreach (var c in _columns.Where(c => c.NotNull && values.TryGetValue(c.Name, out var v) && v is null))
                values[c.Name] = c.Type.Contains("INT") || c.Type.Contains("REAL") ? 0L : "";

            var names = values.Keys.ToList();
            var sql = names.Count == 0
                ? $"INSERT INTO \"{Def.Table}\" DEFAULT VALUES; SELECT last_insert_rowid();"
                : $"INSERT INTO \"{Def.Table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select((_, i) => $"$p{i}"))}); SELECT last_insert_rowid();";
            using var cmd = Cmd(db, tx, sql, names.Select((n, i) => ($"$p{i}", values[n])).ToArray());
            return (long)cmd.ExecuteScalar()!;
        }

        public void Update(SqliteConnection db, SqliteTransaction tx, long id, SortedDictionary<string, object?> payload)
        {
            if (payload.Count == 0) return;
            var names = payload.Keys.ToList();
            var args = names.Select((n, i) => ($"$p{i}", payload[n])).Append(("$id", (object?)id)).ToArray();
            Exec(db, tx, $"UPDATE \"{Def.Table}\" SET {string.Join(", ", names.Select((n, i) => $"\"{n}\" = $p{i}"))} WHERE \"{_pk}\" = $id", args);
        }

        public void Delete(SqliteConnection db, SqliteTransaction tx, long id) =>
            Exec(db, tx, $"DELETE FROM \"{Def.Table}\" WHERE \"{_pk}\" = $id", ("$id", id));

        /// <summary>One canonical JSON text per row content: keys sorted, numbers invariant.</summary>
        public static string Canonical(SortedDictionary<string, object?> values)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                foreach (var (k, v) in values)
                {
                    switch (v)
                    {
                        case null: w.WriteNull(k); break;
                        case long l: w.WriteNumber(k, l); break;
                        case double d: w.WriteNumber(k, d); break;
                        default: w.WriteString(k, Convert.ToString(v, CultureInfo.InvariantCulture)); break;
                    }
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    // ------------------------------------------------------------------ helpers

    private static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static void Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd(db, tx, sql, args);
        cmd.ExecuteNonQuery();
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
