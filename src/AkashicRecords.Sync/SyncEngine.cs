using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Sync;

/// <summary>One synchronised table. `LocalOnlyColumns` never leave the device (file paths, for
/// instance, mean nothing on another machine) and are kept as they are when a row is updated.</summary>
public sealed record SyncTable(string EntityType, string Table, params string[] LocalOnlyColumns)
{
    /// <summary>
    /// A column that says "this is the same thing" even when two devices each created their own row
    /// (a journal entry's day). When an entity arrives for the first time and a local row not yet
    /// linked has the same value there (its first `MatchPrefix` characters), the two are joined and
    /// merged field by field instead of becoming two rows for the same day.
    /// </summary>
    public string? MatchColumn { get; init; }
    public int MatchPrefix { get; init; } = int.MaxValue;

    /// <summary>Same idea with several columns that must all be equal (a link row is "the same thing"
    /// when it joins the same two items). Used when `MatchColumn` is not set.</summary>
    public string[] MatchColumns { get; init; } = [];

    /// <summary>
    /// Columns holding the row id of another synchronised item: column -> that item's entity type.
    /// Row ids differ from one device to the next, so on the wire such a column carries the target's
    /// sync id and each device translates it back to its own row id.
    /// </summary>
    public IReadOnlyDictionary<string, string> Refs { get; init; } = new Dictionary<string, string>();

    /// <summary>References whose target type depends on another column of the same row
    /// (Observation.OwnerId means an Artwork, a Poem... according to OwnerKind).</summary>
    public IReadOnlyList<KindRef> KindRefs { get; init; } = [];

    /// <summary>Text columns holding a decimal number (budget amounts). They are always written with a
    /// point; an older row written "-9,12" by a French machine is rewritten before comparing, or the
    /// same amount would look different on two devices.</summary>
    public string[] DecimalTextColumns { get; init; } = [];

    internal IReadOnlyList<string> AllMatchColumns => MatchColumn is not null ? [MatchColumn] : MatchColumns;

    /// <summary>The entity type `column` points at for this row, or null when it is not a reference
    /// (or its kind is unknown: the value then travels as it is).</summary>
    internal string? TargetOf(string column, IReadOnlyDictionary<string, object?> row)
    {
        if (Refs.TryGetValue(column, out var fixedTarget)) return fixedTarget;
        foreach (var k in KindRefs)
        {
            if (k.IdColumn != column) continue;
            var kind = row.TryGetValue(k.KindColumn, out var v) ? v as string : null;
            if (kind is null) return null;
            if (k.Kinds is not null) return k.Kinds.TryGetValue(kind, out var mapped) ? mapped : null;
            return kind; // the kind is the entity type itself
        }
        return null;
    }
}

/// <summary>`IdColumn` holds a row id whose table is named by `KindColumn`. `Kinds` maps the kind's
/// values to entity types; null = the value is the entity type.</summary>
public sealed record KindRef(string IdColumn, string KindColumn, IReadOnlyDictionary<string, string>? Kinds = null);

public sealed class SyncReport
{
    public int Pulled, Inserted, Updated, Deleted, Adopted, Pushed, PushedDeletes;
    /// <summary>Entities changed on both sides since the last sync (merged field by field).</summary>
    public int Conflicts;
    /// <summary>Fields where both sides wrote something different: this device's text was kept,
    /// the other one was saved in `sync_conflict` for the user to read.</summary>
    public int SetAside;
    /// <summary>Rows waiting for an item they point at that has not arrived yet (applied as soon as it does).</summary>
    public int Waiting;
    public long Cursor;
    public bool NothingToDo => Inserted + Updated + Deleted + Adopted + Pushed + PushedDeletes == 0;
    public override string ToString() =>
        $"recu {Pulled} (ajouts {Inserted}, modifs {Updated}, suppressions {Deleted}, reconnus {Adopted}) ; " +
        $"envoye {Pushed} (dont suppressions {PushedDeletes}) ; conflits {Conflicts} (versions mises de cote {SetAside}) ; curseur {Cursor}";
}

/// <summary>A version that lost a field-level conflict, kept so nothing is lost silently.</summary>
public sealed record SyncConflict(long Id, string EntityType, long? LocalId, string Label, string Field, string? KeptValue, string? LostValue, string LostDevice, string CreatedAt);

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
/// Conflict policy, when a row changed here AND on the hub since the last sync:
///  - merged field by field against the last synchronised version (kept in `sync_map.synced_values`):
///    a field only one side touched takes that side's value;
///  - a field both sides changed keeps this device's value, and the other one is written to
///    `sync_conflict` so the user can read it and take it back: nothing is lost silently;
///  - an edit beats a delete, whichever side did which.
/// The Kotlin engine (android/.../sync/SyncEngine.kt) follows the same rules: change both together.
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
    public double MaxDeleteShare { get; set; } = 0.90;
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
        foreach (var t in tables) t.NormalizeDecimalText(db);
        foreach (var t in tables) BackfillAncestors(db, t);

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
            CREATE TABLE IF NOT EXISTS sync_deferred(
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                entity_type TEXT NOT NULL,
                sync_id     TEXT NOT NULL,
                rev         INTEGER NOT NULL,
                payload     TEXT NOT NULL,      -- as received: it points at an item not here yet
                device      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_conflict(
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                entity_type TEXT NOT NULL,
                sync_id     TEXT NOT NULL,
                local_id    INTEGER,
                label       TEXT NOT NULL,      -- the row's title at the time, to recognise it
                field       TEXT NOT NULL,
                kept_value  TEXT,               -- what this device kept
                lost_value  TEXT,               -- what the other device had written
                lost_device TEXT NOT NULL,
                created_at  TEXT NOT NULL
            );
            """);
        // The row as last synchronised (canonical JSON): the common ancestor of a field-by-field merge.
        using var cmd = Cmd(db, null, "SELECT COUNT(*) FROM pragma_table_info('sync_map') WHERE name = 'synced_values'");
        if ((long)cmd.ExecuteScalar()! == 0) Exec(db, null, "ALTER TABLE sync_map ADD COLUMN synced_values TEXT");
    }

    /// <summary>
    /// The synchronised content of a database as it would travel: one line per row, references
    /// written as sync ids, sorted. Two databases that hold the same data give the same list whatever
    /// their row ids - once each has synchronised (before that, nothing has a sync id and references
    /// read as empty). For checks and rehearsals; changes no data (creates the bookkeeping tables).
    /// </summary>
    public static List<string> WireDump(string dbPath, IReadOnlyList<SyncTable> tables)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        db.Open();
        EnsureSchema(db);
        var infos = tables.Select(d => TableInfo.Load(db, d)).Where(t => t is not null).Select(t => t!).ToList();
        var lines = new List<string>();
        using var tx = db.BeginTransaction();
        foreach (var t in infos)
            foreach (var (_, values) in t.ReadAll(db, tx).ToList())
                lines.Add($"{t.Def.EntityType}|{ToWire(db, tx, t, values)}");
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    /// <summary>The versions set aside by field-level conflicts, newest first.</summary>
    public static List<SyncConflict> Conflicts(string dbPath)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        db.Open();
        var list = new List<SyncConflict>();
        using var exists = Cmd(db, null, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_conflict'");
        if ((long)exists.ExecuteScalar()! == 0) return list;
        using var cmd = Cmd(db, null, "SELECT id, entity_type, local_id, label, field, kept_value, lost_value, lost_device, created_at FROM sync_conflict ORDER BY id DESC");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SyncConflict(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetString(8)));
        return list;
    }

    /// <summary>Forgets one set-aside version (the user has read it, or taken it back by hand).</summary>
    public static void DismissConflict(string dbPath, long id)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        db.Open();
        Exec(db, null, "DELETE FROM sync_conflict WHERE id = $id", ("$id", id));
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
                RetryDeferred(db, tx, tables, report);
                SetCursor(db, tx, page.NextCursor);
                tx.Commit();
            }
            if (!page.HasMore) break;
        }
    }

    private static void Apply(SqliteConnection db, SqliteTransaction tx, TableInfo t, HubLogRow row, SyncReport report)
    {
        if (row.Deleted || row.Payload is null) { ApplyRemote(db, tx, t, row.EntityId, row.Rev, null, row.DeviceId, report); return; }
        var payload = FromWire(db, tx, t, t.Project(row.Payload.Value));
        if (payload is null)
        {
            // It points at an item that has not arrived yet: kept as received, applied once it has.
            Exec(db, tx, "INSERT INTO sync_deferred(entity_type, sync_id, rev, payload, device) VALUES ($t, $s, $r, $p, $d)",
                ("$t", row.EntityType), ("$s", row.EntityId), ("$r", row.Rev), ("$p", row.Payload.Value.GetRawText()), ("$d", row.DeviceId));
            return;
        }
        ApplyRemote(db, tx, t, row.EntityId, row.Rev, payload, row.DeviceId, report);
    }

    /// <summary>Rows set aside because they pointed at something unknown: tried again now that more has arrived.</summary>
    private static void RetryDeferred(SqliteConnection db, SqliteTransaction tx, List<TableInfo> tables, SyncReport report)
    {
        for (var progress = true; progress;)
        {
            progress = false;
            var waiting = new List<(long Id, string Type, string SyncId, long Rev, string Payload, string Device)>();
            using (var cmd = Cmd(db, tx, "SELECT id, entity_type, sync_id, rev, payload, device FROM sync_deferred ORDER BY id"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) waiting.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetString(5)));
            foreach (var w in waiting)
            {
                var t = tables.FirstOrDefault(x => x.Def.EntityType == w.Type);
                var known = MapEntry.BySyncId(db, tx, w.Type, w.SyncId);
                var obsolete = t is null || (known is not null && known.Rev >= w.Rev);
                var payload = obsolete ? null : FromWire(db, tx, t!, t!.Project(Parse(w.Payload)));
                if (!obsolete && payload is null) continue; // still waiting
                if (!obsolete) ApplyRemote(db, tx, t!, w.SyncId, w.Rev, payload, w.Device, report);
                Exec(db, tx, "DELETE FROM sync_deferred WHERE id = $id", ("$id", w.Id));
                progress = true;
            }
        }
        using var count = Cmd(db, tx, "SELECT COUNT(*) FROM sync_deferred");
        report.Waiting = (int)(long)count.ExecuteScalar()!;
    }

    // ---- References: row ids are local, so on the wire a reference carries the target's sync id.

    /// <summary>A row as it goes to the hub: each reference replaced by its target's sync id (null when
    /// the target does not exist here).</summary>
    private static string ToWire(SqliteConnection db, SqliteTransaction tx, TableInfo t, SortedDictionary<string, object?> values)
    {
        var wire = new SortedDictionary<string, object?>(values, StringComparer.Ordinal);
        foreach (var (column, value) in values)
        {
            if (value is not long id || id == 0) continue;
            var target = t.Def.TargetOf(column, values);
            if (target is null) continue;
            using var cmd = Cmd(db, tx, "SELECT sync_id FROM sync_map WHERE entity_type = $t AND local_id = $l AND deleted = 0", ("$t", target), ("$l", id));
            // A link to something that does not exist here says the same as no link: null, or 0 where
            // the column cannot be empty (what the receiving side would store for null anyway).
            object? syncId = cmd.ExecuteScalar() as string;
            wire[column] = syncId ?? (t.IsRequired(column) ? 0L : null);
        }
        return TableInfo.Canonical(wire);
    }

    /// <summary>A row as received, with each reference turned back into this database's row id.
    /// Null when one of them points at an item that is not here (yet).</summary>
    private static SortedDictionary<string, object?>? FromWire(SqliteConnection db, SqliteTransaction tx, TableInfo t, SortedDictionary<string, object?> wire)
    {
        var values = new SortedDictionary<string, object?>(wire, StringComparer.Ordinal);
        foreach (var (column, value) in wire)
        {
            var target = t.Def.TargetOf(column, wire);
            if (target is null || value is not string syncId) continue;
            using var cmd = Cmd(db, tx, "SELECT local_id FROM sync_map WHERE entity_type = $t AND sync_id = $s", ("$t", target), ("$s", syncId));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;                            // never heard of it: wait for it
            values[column] = r.IsDBNull(0) ? null : r.GetInt64(0); // known but deleted: the link is empty
        }
        return values;
    }

    /// <summary>Brings one entity's state on the hub (`payload` null = deleted there) into this database.</summary>
    private static void ApplyRemote(SqliteConnection db, SqliteTransaction tx, TableInfo t, string syncId, long rev,
        SortedDictionary<string, object?>? payload, string device, SyncReport report)
    {
        var type = t.Def.EntityType;
        var map = MapEntry.BySyncId(db, tx, type, syncId);

        if (map is null)
        {
            if (payload is null) { MapEntry.Upsert(db, tx, type, syncId, null, rev, null, true); return; }

            // First contact with this entity. If an identical row is already here and not yet linked
            // (both devices started from the same data), recognise it instead of creating a twin.
            var twin = t.FindUnmappedTwin(db, tx, payload);
            if (twin is not null)
            {
                MapEntry.Upsert(db, tx, type, syncId, twin, rev, t.JsonOfRow(db, tx, twin.Value), false);
                report.Adopted++;
                return;
            }

            var sameThing = t.FindUnmappedByKey(db, tx, payload);
            if (sameThing is null)
            {
                var id = t.Insert(db, tx, payload);
                MapEntry.Upsert(db, tx, type, syncId, id, rev, t.JsonOfRow(db, tx, id), false);
                report.Inserted++;
                return;
            }
            // The same thing written on both devices (two entries for one day): joined into one row
            // and merged below, with no common ancestor.
            map = new MapEntry(syncId, sameThing, 0, null, null, false);
            report.Adopted++;
        }
        else if (rev <= map.Rev)
        {
            return; // already known (our own push coming back, or a replay)
        }

        var local = map.LocalId is null ? null : t.ReadRow(db, tx, map.LocalId.Value);
        var localJson = local is null ? null : TableInfo.Canonical(local);
        var locallyChanged = !map.Deleted && (local is null || Sha(localJson!) != map.SyncedHash);

        if (!locallyChanged)
        {
            // Only the hub moved: take its version.
            if (payload is null)
            {
                if (local is not null) { t.Delete(db, tx, map.LocalId!.Value); report.Deleted++; }
                MapEntry.Upsert(db, tx, type, syncId, null, rev, null, true);
            }
            else if (local is null)
            {
                var id = t.Insert(db, tx, payload); // deleted here earlier, alive again on the hub
                MapEntry.Upsert(db, tx, type, syncId, id, rev, t.JsonOfRow(db, tx, id), false);
                report.Inserted++;
            }
            else
            {
                t.Update(db, tx, map.LocalId!.Value, payload);
                MapEntry.Upsert(db, tx, type, syncId, map.LocalId, rev, t.JsonOfRow(db, tx, map.LocalId.Value), false);
                report.Updated++;
            }
            return;
        }

        // ---- Both sides changed since the last sync.

        if (payload is null)
        {
            if (local is null) { MapEntry.Upsert(db, tx, type, syncId, null, rev, null, true); return; } // deleted on both sides
            // Deleted there, edited here: the edit wins. "Never sent" at the hub's revision, so the
            // scan pushes our row back on top of the delete.
            MapEntry.Upsert(db, tx, type, syncId, map.LocalId, rev, null, false);
            report.Conflicts++;
            return;
        }
        if (local is null)
        {
            // Deleted here, edited there: the edit wins, the row comes back.
            var id = t.Insert(db, tx, payload);
            MapEntry.Upsert(db, tx, type, syncId, id, rev, t.JsonOfRow(db, tx, id), false);
            report.Inserted++;
            report.Conflicts++;
            return;
        }
        if (payload.All(kv => Equals(local[kv.Key], kv.Value)))
        {
            // Both sides hold the same thing (typically our own push whose answer got lost).
            MapEntry.Upsert(db, tx, type, syncId, map.LocalId, rev, localJson, false);
            return;
        }

        // Field by field, against the version both sides started from.
        var ancestor = map.SyncedValues is null ? null : t.Project(Parse(map.SyncedValues));
        var take = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, theirs) in payload)
        {
            var ours = local[field];
            if (Equals(ours, theirs)) continue;
            object? common = null;
            var known = ancestor is not null && ancestor.TryGetValue(field, out common);
            if (known && Equals(ours, common)) take[field] = theirs;       // only they changed it
            else if (known && Equals(theirs, common)) { }                  // only we changed it
            else if (!known && t.Def.AllMatchColumns.Contains(field)) take[field] = theirs; // joined on it: same day, another hour — not a conflict
            else if (!known && IsEmpty(ours)) take[field] = theirs;        // no ancestor: something beats nothing
            else if (!known && IsEmpty(theirs)) { }
            else
            {
                // Both wrote something different: one stays, the other is set aside for the user.
                // With a common ancestor, ours stays (we are the one syncing last). Without one (two
                // devices linking rows they each already had), nobody knows which is newer: the
                // longer text stays - the fuller version of a journal day - whichever side holds it.
                var theirsWins = !known && theirs is string theirText && ours is string ourText && theirText.Length > ourText.Length;
                if (theirsWins) take[field] = theirs;
                var (kept, lost) = theirsWins ? (theirs, ours) : (ours, theirs);
                Exec(db, tx, """
                    INSERT INTO sync_conflict(entity_type, sync_id, local_id, label, field, kept_value, lost_value, lost_device, created_at)
                    VALUES ($t, $s, $l, $label, $f, $kept, $lost, $dev, $at)
                    """, ("$t", type), ("$s", syncId), ("$l", map.LocalId), ("$label", t.LabelOf(local)), ("$f", field),
                    ("$kept", Text(kept)), ("$lost", Text(lost)), ("$dev", theirsWins ? "cet appareil" : device), ("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                report.SetAside++;
            }
        }
        if (take.Count > 0) { t.Update(db, tx, map.LocalId!.Value, take); report.Updated++; }

        // What the hub holds, seen through this table's columns: the new common ancestor. Our row
        // differs from it wherever we kept our own value, so the scan pushes the merged result.
        var theirRow = new SortedDictionary<string, object?>(local, StringComparer.Ordinal);
        foreach (var (field, theirs) in payload) theirRow[field] = theirs;
        MapEntry.Upsert(db, tx, type, syncId, map.LocalId, rev, TableInfo.Canonical(theirRow), false);
        report.Conflicts++;
    }

    private static bool IsEmpty(object? v) => v is null or "" or 0L or 0.0;

    private static string? Text(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

    /// <summary>Rows linked before `synced_values` existed only have a hash. While such a row is
    /// untouched, its content IS the last synchronised version: write it down as the ancestor.</summary>
    private static void BackfillAncestors(SqliteConnection db, TableInfo t)
    {
        using var tx = db.BeginTransaction();
        foreach (var map in MapEntry.All(db, tx, t.Def.EntityType).Where(m => m is { SyncedValues: null, SyncedHash: not null, LocalId: not null, Deleted: false }))
        {
            var json = t.JsonOfRow(db, tx, map.LocalId!.Value);
            if (json is not null && Sha(json) == map.SyncedHash)
                MapEntry.Upsert(db, tx, t.Def.EntityType, map.SyncId, map.LocalId, map.Rev, json, false);
        }
        tx.Commit();
    }

    // ------------------------------------------------------------------ scan & push

    private sealed record Pending(TableInfo Table, string SyncId, long? LocalId, long BaseRev, string? Hash, string? PayloadJson, string? WireJson = null)
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
        // Now that every new row of this table has its sync id, write the rows as they travel.
        pending = pending.Select(p => p with { WireJson = ToWire(db, tx, t, t.Project(Parse(p.PayloadJson!))) }).ToList();
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
                p.WireJson is null ? null : Parse(p.WireJson), p.IsDelete)).ToList();
            var response = await _hub.PushAsync(mutations, ct);

            using var tx = db.BeginTransaction();
            foreach (var (p, res) in batch.Zip(response.Results))
            {
                var type = p.Table.Def.EntityType;
                if (res.Status is "accepted" or "duplicate")
                {
                    MapEntry.Upsert(db, tx, type, p.SyncId, p.IsDelete ? null : p.LocalId, res.Rev, p.PayloadJson, p.IsDelete);
                    report.Pushed++;
                    if (p.IsDelete) report.PushedDeletes++;
                }
                else
                {
                    // The hub moved since we last looked: merge with what it holds now (same rules
                    // as a pull); the next round sends the result on top of its revision.
                    var map = MapEntry.BySyncId(db, tx, type, p.SyncId)!;
                    if (res.Current is null || res.Current.Rev <= map.Rev)
                        MapEntry.Upsert(db, tx, type, p.SyncId, map.LocalId, res.Rev, null, false); // unknown to this hub: send as it is
                    else
                    {
                        var gone = res.Current.Deleted || res.Current.Payload is null;
                        var theirs = gone ? null : FromWire(db, tx, p.Table, p.Table.Project(res.Current.Payload!.Value));
                        if (!gone && theirs is null)
                            MapEntry.Upsert(db, tx, type, p.SyncId, map.LocalId, res.Current.Rev, null, false); // points at something unknown here: ours goes on top
                        else
                            ApplyRemote(db, tx, p.Table, p.SyncId, res.Current.Rev, theirs, res.Current.LastDevice, report);
                    }
                    conflicts++;
                }
            }
            tx.Commit();
        }
        return conflicts;
    }

    // ------------------------------------------------------------------ sync_map rows

    private sealed record MapEntry(string SyncId, long? LocalId, long Rev, string? SyncedHash, string? SyncedValues, bool Deleted)
    {
        private const string Columns = "sync_id, local_id, rev, synced_hash, synced_values, deleted";

        public static MapEntry? BySyncId(SqliteConnection db, SqliteTransaction tx, string type, string syncId)
        {
            using var cmd = Cmd(db, tx, $"SELECT {Columns} FROM sync_map WHERE entity_type = $t AND sync_id = $s", ("$t", type), ("$s", syncId));
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }

        public static List<MapEntry> All(SqliteConnection db, SqliteTransaction tx, string type)
        {
            using var cmd = Cmd(db, tx, $"SELECT {Columns} FROM sync_map WHERE entity_type = $t", ("$t", type));
            using var r = cmd.ExecuteReader();
            var list = new List<MapEntry>();
            while (r.Read()) list.Add(Read(r));
            return list;
        }

        /// <summary>`values`: the row as synchronised (canonical JSON), or null for "never sent" /
        /// deleted. The hash the scan compares with is derived from it.</summary>
        public static void Upsert(SqliteConnection db, SqliteTransaction tx, string type, string syncId, long? localId, long rev, string? values, bool deleted) =>
            Exec(db, tx, """
                INSERT INTO sync_map(entity_type, sync_id, local_id, rev, synced_hash, synced_values, deleted) VALUES ($t, $s, $l, $r, $h, $v, $d)
                ON CONFLICT(entity_type, sync_id) DO UPDATE SET
                    local_id = excluded.local_id, rev = excluded.rev, synced_hash = excluded.synced_hash,
                    synced_values = excluded.synced_values, deleted = excluded.deleted
                """, ("$t", type), ("$s", syncId), ("$l", localId), ("$r", rev), ("$h", values is null ? null : Sha(values)), ("$v", values), ("$d", deleted ? 1 : 0));

        private static MapEntry Read(SqliteDataReader r) => new(
            r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5) != 0);
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

        public bool IsRequired(string column) => _columns.FirstOrDefault(c => c.Name == column) is { NotNull: true };

        /// <summary>See SyncTable.DecimalTextColumns.</summary>
        public void NormalizeDecimalText(SqliteConnection db)
        {
            foreach (var column in Def.DecimalTextColumns.Where(name => _columns.Any(c => c.Name == name)))
                Exec(db, null, $"UPDATE \"{Def.Table}\" SET \"{column}\" = REPLACE(\"{column}\", ',', '.') WHERE \"{column}\" LIKE '%,%'");
        }

        private string ColumnList =>string.Join(", ", _columns.Select(c => $"\"{c.Name}\""));

        public IEnumerable<(long Id, SortedDictionary<string, object?> Values)> ReadAll(SqliteConnection db, SqliteTransaction tx)
        {
            using var cmd = Cmd(db, tx, $"SELECT \"{_pk}\", {ColumnList} FROM \"{Def.Table}\"");
            using var r = cmd.ExecuteReader();
            while (r.Read()) yield return (r.GetInt64(0), ReadValues(r));
        }

        public SortedDictionary<string, object?>? ReadRow(SqliteConnection db, SqliteTransaction tx, long id)
        {
            using var cmd = Cmd(db, tx, $"SELECT \"{_pk}\", {ColumnList} FROM \"{Def.Table}\" WHERE \"{_pk}\" = $id", ("$id", id));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadValues(r) : null;
        }

        /// <summary>The row's content as canonical JSON, or null when the row is gone.</summary>
        public string? JsonOfRow(SqliteConnection db, SqliteTransaction tx, long id) =>
            ReadRow(db, tx, id) is { } row ? Canonical(row) : null;

        /// <summary>What to call the row when showing a conflict to the user.</summary>
        public string LabelOf(SortedDictionary<string, object?> row) =>
            new[] { "Title", "Name", "EntryDate", "Date" }
                .Select(c => row.TryGetValue(c, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : null)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? Def.EntityType;

        /// <summary>A local row not yet linked that is "the same thing" by the table's match column
        /// (see SyncTable.MatchColumn). Null when the table has none or nothing matches.</summary>
        public long? FindUnmappedByKey(SqliteConnection db, SqliteTransaction tx, SortedDictionary<string, object?> payload)
        {
            var keys = Def.AllMatchColumns;
            if (keys.Count == 0 || keys.Any(k => _columns.All(c => c.Name != k))) return null;
            // Every key column must be carried and say something: an empty key identifies nothing.
            if (keys.Any(k => !payload.TryGetValue(k, out var v) || v is null or "")) return null;

            object? Key(object? v) => v is string s && s.Length > Def.MatchPrefix ? s[..Def.MatchPrefix] : v;
            long? best = null;
            var bestScore = -1;
            var (filter, args) = KeyFilter(payload)!.Value;
            using var cmd = Cmd(db, tx,
                $"SELECT \"{_pk}\", {ColumnList} FROM \"{Def.Table}\" " +
                $"WHERE \"{_pk}\" NOT IN (SELECT local_id FROM sync_map WHERE entity_type = $t AND local_id IS NOT NULL){filter} ORDER BY \"{_pk}\"",
                [("$t", Def.EntityType), .. args]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var local = ReadValues(r);
                if (!keys.All(k => Equals(Key(local[k]), Key(payload[k])))) continue;
                // Several candidates (rows created in one batch share a timestamp): the closest one.
                var score = payload.Count(kv => Equals(local[kv.Key], kv.Value));
                if (score > bestScore) { best = r.GetInt64(0); bestScore = score; }
            }
            return best;
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

        /// <summary>
        /// The match columns as an SQL condition (" AND col = $k0 ..."), when the payload carries a value
        /// for each. Looking a row up this way instead of reading every unlinked row is what keeps the
        /// first link of a few thousand tracks from taking minutes; an identical row always has the same
        /// key, so nothing is missed. Null when the table has no key or the payload's key is empty.
        /// </summary>
        private (string Sql, (string Name, object? Value)[] Args)? KeyFilter(SortedDictionary<string, object?> payload)
        {
            var keys = Def.AllMatchColumns;
            if (keys.Count == 0 || keys.Any(k => _columns.All(c => c.Name != k))) return null;
            if (keys.Any(k => !payload.TryGetValue(k, out var v) || v is null or "")) return null;
            var sql = new StringBuilder();
            var args = new List<(string, object?)>();
            for (var i = 0; i < keys.Count; i++)
            {
                var value = payload[keys[i]];
                if (Def.MatchPrefix != int.MaxValue && value is string s)
                {
                    sql.Append($" AND substr(\"{keys[i]}\", 1, {Def.MatchPrefix}) = $k{i}");
                    args.Add(($"$k{i}", s.Length > Def.MatchPrefix ? s[..Def.MatchPrefix] : s));
                }
                else
                {
                    sql.Append($" AND \"{keys[i]}\" = $k{i}");
                    args.Add(($"$k{i}", value));
                }
            }
            return (sql.ToString(), args.ToArray());
        }

        public long? FindUnmappedTwin(SqliteConnection db, SqliteTransaction tx, SortedDictionary<string, object?> payload)
        {
            if (payload.Count == 0) return null;
            var filter = KeyFilter(payload);
            using var cmd = Cmd(db, tx,
                $"SELECT \"{_pk}\", {ColumnList} FROM \"{Def.Table}\" " +
                $"WHERE \"{_pk}\" NOT IN (SELECT local_id FROM sync_map WHERE entity_type = $t AND local_id IS NOT NULL){filter?.Sql}",
                [("$t", Def.EntityType), .. filter?.Args ?? []]);
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
