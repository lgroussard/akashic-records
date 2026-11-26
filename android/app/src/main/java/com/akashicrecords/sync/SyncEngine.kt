package com.akashicrecords.sync

import android.content.ContentValues
import android.database.Cursor
import android.database.sqlite.SQLiteDatabase
import androidx.sqlite.db.SupportSQLiteDatabase
import org.json.JSONArray
import org.json.JSONObject
import java.util.SortedMap
import java.util.TreeMap
import java.util.UUID

/** One synchronised table. [localOnly] columns never leave the device and are kept as they are on an update. */
/** [matchColumn]: a column that says "this is the same thing" even when two devices each created their own row
 * (a journal entry's day, compared on its first [matchPrefix] characters): such rows are joined and merged. */
data class SyncTable(
    val entityType: String,
    val table: String,
    val localOnly: Set<String> = emptySet(),
    val matchColumn: String? = null,
    val matchPrefix: Int = Int.MAX_VALUE,
    /** Same idea with several columns that must all be equal. Used when [matchColumn] is not set. */
    val matchColumns: List<String> = emptyList(),
    /** Columns holding the row id of another synchronised item: column -> that item's entity type. Row ids differ
     * from one device to the next, so on the wire such a column carries the target's sync id. */
    val refs: Map<String, String> = emptyMap(),
    /** References whose target type depends on another column of the same row. */
    val kindRefs: List<KindRef> = emptyList(),
    /** Text columns holding a decimal number (budget amounts): always written with a point. An older row written
     * "-9,12" is rewritten before comparing, or the same amount would look different on two devices. */
    val decimalTextColumns: List<String> = emptyList(),
) {
    val allMatchColumns: List<String> get() = matchColumn?.let(::listOf) ?: matchColumns

    /** The entity type [column] points at for this row, or null when it is not a reference (or its kind is
     * unknown: the value then travels as it is). */
    fun targetOf(column: String, row: Map<String, Any?>): String? {
        refs[column]?.let { return it }
        for (k in kindRefs) {
            if (k.idColumn != column) continue
            val kind = row[k.kindColumn] as? String ?: return null
            return if (k.kinds != null) k.kinds[kind] else kind
        }
        return null
    }
}

/** [idColumn] holds a row id whose table is named by [kindColumn]. [kinds] maps the kind's values to entity types;
 * null = the value is the entity type. */
data class KindRef(val idColumn: String, val kindColumn: String, val kinds: Map<String, String>? = null)

class SyncReport {
    var pulled = 0
    var inserted = 0
    var updated = 0
    var deleted = 0
    var adopted = 0
    var pushed = 0
    var pushedDeletes = 0
    var conflicts = 0   // entities changed on both sides since the last sync (merged field by field)
    var setAside = 0    // fields both sides wrote differently: ours kept, theirs saved in sync_conflict
    var waiting = 0     // rows waiting for an item they point at that has not arrived yet
    var cursor = 0L
    val received get() = inserted + updated + deleted
    val nothingToDo get() = inserted + updated + deleted + adopted + pushed + pushedDeletes == 0
    override fun toString() =
        "recu $pulled (ajouts $inserted, modifs $updated, suppressions $deleted, reconnus $adopted) ; " +
            "envoye $pushed (dont suppressions $pushedDeletes) ; conflits $conflicts (versions mises de cote $setAside) ; curseur $cursor"
}

/** Raised instead of doing something that looks like a mistake (see [SyncEngine.maxDeleteShare]). */
class SyncSafetyException(message: String) : Exception(message)

/**
 * Keeps the listed tables of the phone database in step with the hub. Same method as the desktop engine
 * (src/AkashicRecords.Sync/SyncEngine.cs), kept in step with it by hand:
 *
 * the Room tables are not altered and the DAOs are not involved. Each row is compared with what was last
 * synchronised (a hash kept in `sync_map`) and what to send is derived from that. A row is the durable truth,
 * the "outbox" is recomputed at every run, so a kill or a lost connection loses nothing and resending is harmless.
 *
 * Conflict policy, when a row changed here AND on the hub since the last sync:
 *  - merged field by field against the last synchronised version (`sync_map.synced_values`): a field only one
 *    side touched takes that side's value;
 *  - a field both sides changed keeps this device's value, and the other one is written to `sync_conflict` so
 *    the user can read it and take it back: nothing is lost silently;
 *  - an edit beats a delete, whichever side did which.
 */
class SyncEngine(private val db: SupportSQLiteDatabase, private val hub: HubClient, private val tables: List<SyncTable>) {

    /** Safety brake: a run that would delete more than this share of a table's synchronised rows on the hub
     * (and more than [minDeletesForBrake]) stops instead. A database swapped under the engine looks exactly
     * like "the user deleted everything". */
    var maxDeleteShare = 0.90
    var minDeletesForBrake = 5

    fun run(): SyncReport {
        val report = SyncReport()
        ensureSchema()
        val infos = tables.mapNotNull { TableInfo.load(db, it) }
        infos.forEach { it.normalizeDecimalText(db) }
        infos.forEach(::backfillAncestors)

        pull(infos, report)
        for (round in 0 until 4) {
            scanLinked = 0
            scanDeleted = 0
            val pending = infos.flatMap { scan(it) }
            checkDeleteBrake()
            if (pending.isEmpty()) break
            val conflicts = push(pending, report)
            pull(infos, report)
            if (conflicts == 0) break // otherwise: rebased on the hub's revision, send again
        }
        report.cursor = cursor()
        return report
    }

    // ------------------------------------------------------------------ schema & state

    private fun ensureSchema() {
        db.execSQL(
            "CREATE TABLE IF NOT EXISTS sync_map(entity_type TEXT NOT NULL, sync_id TEXT NOT NULL, local_id INTEGER, " +
                "rev INTEGER NOT NULL, synced_hash TEXT, deleted INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(entity_type, sync_id))",
        )
        db.execSQL("CREATE INDEX IF NOT EXISTS sync_map_local ON sync_map(entity_type, local_id)")
        db.execSQL("CREATE TABLE IF NOT EXISTS sync_state(key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        db.execSQL(SyncConflicts.CREATE)
        // Rows as received that point at an item not here yet.
        db.execSQL(
            "CREATE TABLE IF NOT EXISTS sync_deferred(id INTEGER PRIMARY KEY AUTOINCREMENT, entity_type TEXT NOT NULL, " +
                "sync_id TEXT NOT NULL, rev INTEGER NOT NULL, payload TEXT NOT NULL, device TEXT NOT NULL)",
        )
        // The row as last synchronised (canonical JSON): the common ancestor of a field-by-field merge.
        val hasValues = db.query("SELECT COUNT(*) FROM pragma_table_info('sync_map') WHERE name = 'synced_values'").use { c -> c.moveToFirst() && c.getLong(0) > 0 }
        if (!hasValues) db.execSQL("ALTER TABLE sync_map ADD COLUMN synced_values TEXT")
        // An alias: a second hub entity for a row already linked here (see the desktop engine).
        val hasAlias = db.query("SELECT COUNT(*) FROM pragma_table_info('sync_map') WHERE name = 'alias'").use { c -> c.moveToFirst() && c.getLong(0) > 0 }
        if (!hasAlias) db.execSQL("ALTER TABLE sync_map ADD COLUMN alias INTEGER NOT NULL DEFAULT 0")
    }

    private fun cursor(): Long =
        db.query("SELECT value FROM sync_state WHERE key = 'cursor'").use { c -> if (c.moveToFirst()) c.getString(0).toLong() else 0L }

    private fun setCursor(value: Long) =
        db.execSQL(
            "INSERT INTO sync_state(key, value) VALUES ('cursor', ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            arrayOf<Any?>(value.toString()),
        )

    private inline fun <T> transaction(block: () -> T): T {
        db.beginTransaction()
        try {
            val r = block()
            db.setTransactionSuccessful()
            return r
        } finally {
            db.endTransaction()
        }
    }

    // ------------------------------------------------------------------ pull

    private fun pull(infos: List<TableInfo>, report: SyncReport) {
        while (true) {
            val cursor = cursor()
            val page = hub.pull(cursor, PULL_PAGE)
            if (page.getLong("head_seq") < cursor) {
                throw SyncSafetyException(
                    "Le hub est en retard sur ce téléphone (hub : ${page.getLong("head_seq")}, ici : $cursor). " +
                        "Ce n'est pas le même hub, ou il a été réinitialisé. Rien n'a été modifié.",
                )
            }
            val rows = page.getJSONArray("rows")
            transaction {
                for (i in 0 until rows.length()) {
                    val row = rows.getJSONObject(i)
                    val table = infos.firstOrNull { it.def.entityType == row.getString("entity_type") } ?: continue
                    report.pulled++
                    apply(table, row, report)
                }
                retryDeferred(infos, report)
                setCursor(page.getLong("next_cursor"))
            }
            if (!page.getBoolean("has_more")) break
        }
    }

    private fun apply(t: TableInfo, row: JSONObject, report: SyncReport) {
        val syncId = row.getString("entity_id")
        val rev = row.getLong("rev")
        val device = row.optString("device_id", "?")
        if (row.optBoolean("deleted") || row.isNull("payload")) {
            applyRemote(t, syncId, rev, null, device, report)
            return
        }
        val payload = fromWire(t, t.project(row.getJSONObject("payload")))
        if (payload == null) {
            // It points at an item that has not arrived yet: kept as received, applied once it has.
            db.execSQL(
                "INSERT INTO sync_deferred(entity_type, sync_id, rev, payload, device) VALUES (?, ?, ?, ?, ?)",
                arrayOf<Any?>(t.def.entityType, syncId, rev, row.getJSONObject("payload").toString(), device),
            )
            return
        }
        applyRemote(t, syncId, rev, payload, device, report)
    }

    private class Deferred(val id: Long, val type: String, val syncId: String, val rev: Long, val payload: String, val device: String)

    /** Rows set aside because they pointed at something unknown: tried again now that more has arrived. */
    private fun retryDeferred(infos: List<TableInfo>, report: SyncReport) {
        var progress = true
        while (progress) {
            progress = false
            val waiting = db.query("SELECT id, entity_type, sync_id, rev, payload, device FROM sync_deferred ORDER BY id").use { c ->
                buildList { while (c.moveToNext()) add(Deferred(c.getLong(0), c.getString(1), c.getString(2), c.getLong(3), c.getString(4), c.getString(5))) }
            }
            for (w in waiting) {
                val t = infos.firstOrNull { it.def.entityType == w.type }
                val known = mapBySyncId(w.type, w.syncId)
                val obsolete = t == null || (known != null && known.rev >= w.rev)
                val payload = if (obsolete) null else fromWire(t!!, t.project(JSONObject(w.payload)))
                if (!obsolete && payload == null) continue // still waiting
                if (!obsolete) applyRemote(t!!, w.syncId, w.rev, payload, w.device, report)
                db.execSQL("DELETE FROM sync_deferred WHERE id = ?", arrayOf<Any?>(w.id))
                progress = true
            }
        }
        report.waiting = db.query("SELECT COUNT(*) FROM sync_deferred").use { c -> if (c.moveToFirst()) c.getInt(0) else 0 }
    }

    // ---- References: row ids are local, so on the wire a reference carries the target's sync id.

    /** A row as it goes to the hub: each reference replaced by its target's sync id (null when the target does
     * not exist here). */
    private fun toWire(t: TableInfo, values: SortedMap<String, Any?>): JSONObject {
        val wire = TreeMap(values)
        for ((column, value) in values) {
            if (value !is Long || value == 0L) continue
            val target = t.def.targetOf(column, values) ?: continue
            wire[column] = db.query("SELECT sync_id FROM sync_map WHERE entity_type = ? AND local_id = ? AND deleted = 0 AND alias = 0", arrayOf<Any?>(target, value))
                .use { c -> if (c.moveToFirst()) c.getString(0) else null }
                // A link to something that does not exist here says the same as no link: null, or 0 where the
                // column cannot be empty (what the receiving side would store for null anyway).
                ?: if (t.isRequired(column)) 0L else null
        }
        return SyncValues.toPayload(wire)
    }

    /** A row as received, with each reference turned back into this database's row id. Null when one of them
     * points at an item that is not here (yet). */
    private fun fromWire(t: TableInfo, wire: SortedMap<String, Any?>): SortedMap<String, Any?>? {
        val values = TreeMap(wire)
        for ((column, value) in wire) {
            val target = t.def.targetOf(column, wire) ?: continue
            if (value !is String) continue
            val found = db.query("SELECT local_id FROM sync_map WHERE entity_type = ? AND sync_id = ?", arrayOf<Any?>(target, value)).use { c ->
                if (!c.moveToFirst()) return null // never heard of it: wait for it
                if (c.isNull(0)) null else c.getLong(0) // known but deleted: the link is empty
            }
            values[column] = found
        }
        return values
    }

    /** Brings one entity's state on the hub ([payload] null = deleted there) into this database. */
    private fun applyRemote(t: TableInfo, syncId: String, rev: Long, payload: SortedMap<String, Any?>?, device: String, report: SyncReport) {
        val type = t.def.entityType
        var map = mapBySyncId(type, syncId)

        if (map == null) {
            if (payload == null) {
                upsertMap(type, syncId, null, rev, null, true)
                return
            }
            // First contact with this entity. If an identical row is already here and not yet linked (both
            // devices started from the same data), recognise it instead of creating a twin.
            val twin = t.findUnmappedTwin(db, payload)
            if (twin != null) {
                upsertMap(type, syncId, twin, rev, t.jsonOfRow(db, twin), false)
                report.adopted++
                return
            }
            val sameThing = t.findUnmappedByKey(db, payload)
            val linked = if (sameThing == null) t.findByKey(db, payload, mapped = true) else null
            if (linked != null) {
                // Already linked here under another entity: this one becomes an alias of that row, merged below
                // with no common ancestor (instead of a twin row the table may not even accept).
                upsertMap(type, syncId, linked, 0, null, false)
                db.execSQL("UPDATE sync_map SET alias = 1 WHERE entity_type = ? AND sync_id = ?", arrayOf<Any?>(type, syncId))
                map = MapEntry(syncId, linked, 0, null, null, false)
                report.adopted++
            } else if (sameThing == null) {
                val id = t.insert(db, payload)
                upsertMap(type, syncId, id, rev, t.jsonOfRow(db, id), false)
                report.inserted++
                return
            } else {
                // The same thing written on both devices (two entries for one day): joined into one row and
                // merged below, with no common ancestor.
                map = MapEntry(syncId, sameThing, 0, null, null, false)
                report.adopted++
            }
        } else if (rev <= map.rev) {
            return // already known (our own push coming back, or a replay)
        }

        val localId = map.localId
        val local = localId?.let { t.readRow(db, it) }
        val localJson = local?.let(SyncValues::canonical)
        val locallyChanged = !map.deleted && (local == null || SyncValues.sha(localJson!!) != map.syncedHash)

        if (!locallyChanged) {
            // Only the hub moved: take its version.
            if (payload == null) {
                if (local != null) {
                    t.delete(db, localId!!)
                    report.deleted++
                }
                upsertMap(type, syncId, null, rev, null, true)
            } else if (local == null) {
                val id = t.insert(db, payload) // deleted here earlier, alive again on the hub
                upsertMap(type, syncId, id, rev, t.jsonOfRow(db, id), false)
                report.inserted++
            } else {
                t.update(db, localId!!, payload)
                upsertMap(type, syncId, localId, rev, t.jsonOfRow(db, localId), false)
                report.updated++
            }
            return
        }

        // ---- Both sides changed since the last sync.

        if (payload == null) {
            if (local == null) {
                upsertMap(type, syncId, null, rev, null, true) // deleted on both sides
                return
            }
            // Deleted there, edited here: the edit wins. "Never sent" at the hub's revision, so the scan
            // pushes our row back on top of the delete.
            upsertMap(type, syncId, localId, rev, null, false)
            report.conflicts++
            return
        }
        if (local == null) {
            // Deleted here, edited there: the edit wins, the row comes back.
            val id = t.insert(db, payload)
            upsertMap(type, syncId, id, rev, t.jsonOfRow(db, id), false)
            report.inserted++
            report.conflicts++
            return
        }
        if (SyncValues.matches(local, payload)) {
            // Both sides hold the same thing (typically our own push whose answer got lost).
            upsertMap(type, syncId, localId, rev, localJson, false)
            return
        }

        // Field by field, against the version both sides started from.
        val ancestor = map.syncedValues?.let { t.project(JSONObject(it)) }
        val merge = SyncValues.merge(local, payload, ancestor, t.def.allMatchColumns)
        for (c in merge.setAside) {
            // Both wrote something different: ours stays, theirs is set aside for the user.
            db.execSQL(
                "INSERT INTO sync_conflict(entity_type, sync_id, local_id, label, field, kept_value, lost_value, lost_device, created_at) " +
                    "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                arrayOf<Any?>(
                    type, syncId, localId, t.labelOf(local), c.field, c.kept?.toString(), c.lost?.toString(), if (c.lostIsOurs) "cet appareil" else device,
                    java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss", java.util.Locale.US).format(java.util.Date()),
                ),
            )
            report.setAside++
        }
        if (merge.take.isNotEmpty()) {
            t.update(db, localId!!, merge.take)
            report.updated++
        }

        // What the hub holds, seen through this table's columns: the new common ancestor. Our row differs
        // from it wherever we kept our own value, so the scan pushes the merged result.
        val theirRow = TreeMap(local).apply { putAll(payload) }
        upsertMap(type, syncId, localId, rev, SyncValues.canonical(theirRow), false)
        report.conflicts++
    }

    /** Rows linked before `synced_values` existed only have a hash. While such a row is untouched, its content
     * IS the last synchronised version: write it down as the ancestor. */
    private fun backfillAncestors(t: TableInfo) = transaction {
        for (map in allMaps(t.def.entityType)) {
            if (map.syncedValues != null || map.syncedHash == null || map.localId == null || map.deleted) continue
            val json = t.jsonOfRow(db, map.localId) ?: continue
            if (SyncValues.sha(json) == map.syncedHash) upsertMap(t.def.entityType, map.syncId, map.localId, map.rev, json, false)
        }
    }

    // ------------------------------------------------------------------ scan & push

    private class Pending(val table: TableInfo, val syncId: String, val localId: Long?, val baseRev: Long, val hash: String?, val values: SortedMap<String, Any?>?) {
        var wire: JSONObject? = null // the row as it travels (references as sync ids)
        val isDelete get() = values == null
        val mutationId get() = SyncValues.mutationId(table.def.entityType, syncId, baseRev, hash)
    }

    private var scanLinked = 0
    private var scanDeleted = 0

    /**
     * The brake, on the whole database: a database swapped under the engine (an empty or old file) loses nearly
     * every row of every table at once. Emptying one table (the shopping list, a cleared collection) is an
     * ordinary use and goes through; it used to stop every sync of the phone, its other changes included.
     */
    private fun checkDeleteBrake() {
        if (scanDeleted > minDeletesForBrake && scanDeleted > scanLinked * maxDeleteShare) {
            throw SyncSafetyException(
                "$scanDeleted lignes sur $scanLinked ont disparu de ce téléphone depuis la dernière synchro. " +
                    "Cela ressemble à une base remplacée plutôt qu'à des suppressions voulues : rien n'a été envoyé au hub.",
            )
        }
    }

    private fun scan(t: TableInfo): List<Pending> = transaction {
        val type = t.def.entityType
        val pending = mutableListOf<Pending>()
        val byLocal = allMaps(type).filter { it.localId != null && !it.deleted }.associateBy { it.localId!! }
        val seen = HashSet<Long>()

        for ((id, values) in t.readAll(db)) {
            seen += id
            val hash = SyncValues.sha(SyncValues.canonical(values))
            val map = byLocal[id]
            if (map == null) {
                // New here. The id is stored before anything is sent, so a retry reuses it.
                val syncId = UUID.randomUUID().toString()
                upsertMap(type, syncId, id, 0, null, false)
                pending += Pending(t, syncId, id, 0, hash, values)
            } else if (map.syncedHash != hash) {
                pending += Pending(t, map.syncId, id, map.rev, hash, values)
            }
        }

        val deletes = byLocal.values.filter { it.localId!! !in seen }
        // Counted for the brake, which looks at the whole database (see checkDeleteBrake).
        scanLinked += byLocal.size
        scanDeleted += deletes.size
        // Now that every new row of this table has its sync id, write the rows as they travel.
        pending.forEach { p -> p.wire = toWire(t, p.values!!) }
        deletes.forEach { pending += Pending(t, it.syncId, null, it.rev, null, null) }
        pending
    }

    private fun push(pending: List<Pending>, report: SyncReport): Int {
        var conflicts = 0
        for (batch in pending.chunked(PUSH_BATCH)) {
            val mutations = JSONArray()
            for (p in batch) {
                mutations.put(
                    JSONObject()
                        .put("mutation_id", p.mutationId)
                        .put("entity_type", p.table.def.entityType)
                        .put("entity_id", p.syncId)
                        .put("base_rev", p.baseRev)
                        .put("deleted", p.isDelete)
                        .put("payload", p.wire ?: JSONObject.NULL),
                )
            }
            val results = hub.push(mutations).getJSONArray("results")
            transaction {
                batch.forEachIndexed { i, p ->
                    val res = results.getJSONObject(i)
                    val type = p.table.def.entityType
                    val rev = res.getLong("rev")
                    when (res.getString("status")) {
                        "accepted", "duplicate" -> {
                            upsertMap(type, p.syncId, if (p.isDelete) null else p.localId, rev, p.values?.let(SyncValues::canonical), p.isDelete)
                            report.pushed++
                            if (p.isDelete) report.pushedDeletes++
                        }
                        else -> {
                            // The hub moved since we last looked: merge with what it holds now (same rules as a
                            // pull); the next round sends the result on top of its revision.
                            val map = mapBySyncId(type, p.syncId)!!
                            val current = res.optJSONObject("current")
                            if (current == null || current.getLong("rev") <= map.rev) {
                                upsertMap(type, p.syncId, map.localId, rev, null, false) // unknown to this hub: send as it is
                            } else {
                                val gone = current.optBoolean("deleted") || current.isNull("payload")
                                val theirs = if (gone) null else fromWire(p.table, p.table.project(current.getJSONObject("payload")))
                                if (!gone && theirs == null) {
                                    // points at something unknown here: ours goes on top
                                    upsertMap(type, p.syncId, map.localId, current.getLong("rev"), null, false)
                                } else {
                                    applyRemote(p.table, p.syncId, current.getLong("rev"), theirs, current.optString("last_device", "?"), report)
                                }
                            }
                            conflicts++
                        }
                    }
                }
            }
        }
        return conflicts
    }

    // ------------------------------------------------------------------ sync_map rows

    private class MapEntry(val syncId: String, val localId: Long?, val rev: Long, val syncedHash: String?, val syncedValues: String?, val deleted: Boolean)

    private fun readMap(c: Cursor) = MapEntry(
        c.getString(0), if (c.isNull(1)) null else c.getLong(1), c.getLong(2), if (c.isNull(3)) null else c.getString(3),
        if (c.isNull(4)) null else c.getString(4), c.getLong(5) != 0L,
    )

    private fun mapBySyncId(type: String, syncId: String): MapEntry? =
        db.query("SELECT $MAP_COLUMNS FROM sync_map WHERE entity_type = ? AND sync_id = ?", arrayOf<Any?>(type, syncId))
            .use { c -> if (c.moveToFirst()) readMap(c) else null }

    private fun allMaps(type: String): List<MapEntry> =
        db.query("SELECT $MAP_COLUMNS FROM sync_map WHERE entity_type = ? AND alias = 0", arrayOf<Any?>(type))
            .use { c -> buildList { while (c.moveToNext()) add(readMap(c)) } }

    /** [values]: the row as synchronised (canonical JSON), or null for "never sent" / deleted. The hash the
     * scan compares with is derived from it. */
    private fun upsertMap(type: String, syncId: String, localId: Long?, rev: Long, values: String?, deleted: Boolean) =
        db.execSQL(
            "INSERT INTO sync_map(entity_type, sync_id, local_id, rev, synced_hash, synced_values, deleted) VALUES (?, ?, ?, ?, ?, ?, ?) " +
                "ON CONFLICT(entity_type, sync_id) DO UPDATE SET local_id = excluded.local_id, rev = excluded.rev, " +
                "synced_hash = excluded.synced_hash, synced_values = excluded.synced_values, deleted = excluded.deleted",
            arrayOf<Any?>(type, syncId, localId, rev, values?.let(SyncValues::sha), values, if (deleted) 1 else 0),
        )

    // ------------------------------------------------------------------ one local table

    private class TableInfo(val def: SyncTable, private val pk: String, private val columns: List<Column>) {
        class Column(val name: String, val type: String, val notNull: Boolean, val hasDefault: Boolean) {
            val isReal get() = "REAL" in type || "FLOA" in type || "DOUB" in type
            val isNumber get() = isReal || "INT" in type
        }

        private val columnList = columns.joinToString(", ") { "`${it.name}`" }
        private val realByName = columns.associate { it.name to it.isReal }

        fun project(payload: JSONObject) = SyncValues.project(payload, realByName)

        private fun values(c: Cursor): SortedMap<String, Any?> {
            val out = TreeMap<String, Any?>()
            columns.forEachIndexed { i, col ->
                out[col.name] = when (c.getType(i + 1)) {
                    Cursor.FIELD_TYPE_NULL -> null
                    Cursor.FIELD_TYPE_INTEGER -> c.getLong(i + 1)
                    Cursor.FIELD_TYPE_FLOAT -> c.getDouble(i + 1)
                    Cursor.FIELD_TYPE_BLOB -> throw UnsupportedOperationException("${def.table}.${col.name} : colonne binaire non synchronisable.")
                    else -> c.getString(i + 1)
                }
            }
            return out
        }

        fun readAll(db: SupportSQLiteDatabase): List<Pair<Long, SortedMap<String, Any?>>> =
            db.query("SELECT `$pk`, $columnList FROM `${def.table}`").use { c -> buildList { while (c.moveToNext()) add(c.getLong(0) to values(c)) } }

        fun readRow(db: SupportSQLiteDatabase, id: Long): SortedMap<String, Any?>? =
            db.query("SELECT `$pk`, $columnList FROM `${def.table}` WHERE `$pk` = ?", arrayOf<Any?>(id))
                .use { c -> if (c.moveToFirst()) values(c) else null }

        fun isRequired(column: String) = columns.any { it.name == column && it.notNull }

        /** See [SyncTable.decimalTextColumns]. */
        fun normalizeDecimalText(db: SupportSQLiteDatabase) {
            for (column in def.decimalTextColumns) {
                if (columns.none { it.name == column }) continue
                db.execSQL("UPDATE `${def.table}` SET `$column` = REPLACE(`$column`, ',', '.') WHERE `$column` LIKE '%,%'")
            }
        }

        /** The row's content as canonical JSON, or null when the row is gone. */
        fun jsonOfRow(db: SupportSQLiteDatabase, id: Long): String? = readRow(db, id)?.let(SyncValues::canonical)

        /** What to call the row when showing a conflict to the user. */
        fun labelOf(row: Map<String, Any?>): String =
            listOf("Title", "Name", "EntryDate", "Date").firstNotNullOfOrNull { c -> row[c]?.toString()?.takeIf { it.isNotBlank() } } ?: def.entityType

        /** A local row not yet linked that is "the same thing" by the table's match column (see
         * [SyncTable.matchColumn]). Null when the table has none or nothing matches. */
        fun findUnmappedByKey(db: SupportSQLiteDatabase, payload: Map<String, Any?>): Long? = findByKey(db, payload, mapped = false)

        /** Same lookup among the rows already linked ([mapped]: to a live, non-alias entity) or not linked. */
        fun findByKey(db: SupportSQLiteDatabase, payload: Map<String, Any?>, mapped: Boolean): Long? {
            val keys = def.allMatchColumns
            if (keys.isEmpty() || keys.any { k -> columns.none { it.name == k } }) return null
            // Every key column must be carried and say something: an empty key identifies nothing.
            if (keys.any { k -> !payload.containsKey(k) || payload[k] == null || payload[k] == "" }) return null

            fun key(v: Any?): Any? = if (v is String && v.length > def.matchPrefix) v.take(def.matchPrefix) else v
            var best: Long? = null
            var bestScore = -1
            val (filter, args) = keyFilter(payload)!!
            db.query(
                "SELECT `$pk`, $columnList FROM `${def.table}` WHERE `$pk` " +
                    (if (mapped) "IN (SELECT local_id FROM sync_map WHERE entity_type = ? AND local_id IS NOT NULL AND deleted = 0 AND alias = 0)"
                    else "NOT IN (SELECT local_id FROM sync_map WHERE entity_type = ? AND local_id IS NOT NULL)") +
                    "$filter ORDER BY `$pk`",
                arrayOf<Any?>(def.entityType, *args.toTypedArray()),
            ).use { c ->
                while (c.moveToNext()) {
                    val local = values(c)
                    if (!keys.all { k -> key(local[k]) == key(payload[k]) }) continue
                    // Several candidates (rows created in one batch share a timestamp): the closest one.
                    val score = payload.count { (k, v) -> local[k] == v }
                    if (score > bestScore) {
                        best = c.getLong(0)
                        bestScore = score
                    }
                }
            }
            return best
        }

        /**
         * The match columns as an SQL condition (" AND col = ? ..."), when the payload carries a value for each.
         * Looking a row up this way instead of reading every unlinked row is what keeps the first link of a few
         * thousand tracks from taking minutes; an identical row always has the same key, so nothing is missed.
         * Null when the table has no key or the payload's key is empty.
         */
        private fun keyFilter(payload: Map<String, Any?>): Pair<String, List<Any?>>? {
            val keys = def.allMatchColumns
            if (keys.isEmpty() || keys.any { k -> columns.none { it.name == k } }) return null
            if (keys.any { k -> !payload.containsKey(k) || payload[k] == null || payload[k] == "" }) return null
            val sql = StringBuilder()
            val args = ArrayList<Any?>()
            for (k in keys) {
                val value = payload[k]
                if (def.matchPrefix != Int.MAX_VALUE && value is String) {
                    sql.append(" AND substr(`$k`, 1, ${def.matchPrefix}) = ?")
                    args += value.take(def.matchPrefix)
                } else {
                    sql.append(" AND `$k` = ?")
                    args += value
                }
            }
            return sql.toString() to args
        }

        fun findUnmappedTwin(db: SupportSQLiteDatabase, payload: SortedMap<String, Any?>): Long? {
            if (payload.isEmpty()) return null
            val filter = keyFilter(payload)
            db.query(
                "SELECT `$pk`, $columnList FROM `${def.table}` WHERE `$pk` NOT IN " +
                    "(SELECT local_id FROM sync_map WHERE entity_type = ? AND local_id IS NOT NULL)${filter?.first.orEmpty()}",
                arrayOf<Any?>(def.entityType, *(filter?.second ?: emptyList()).toTypedArray()),
            ).use { c ->
                while (c.moveToNext()) if (SyncValues.matches(values(c), payload)) return c.getLong(0)
            }
            return null
        }

        fun insert(db: SupportSQLiteDatabase, payload: SortedMap<String, Any?>): Long {
            val values = TreeMap(payload)
            // A required column the sender does not have (or sent empty): a neutral value rather than a failed insert.
            for (col in columns) {
                if (!col.notNull) continue
                val missing = !values.containsKey(col.name) && !col.hasDefault
                val nullSent = values.containsKey(col.name) && values[col.name] == null
                if (missing || nullSent) values[col.name] = if (col.isNumber) 0L else ""
            }
            return db.insert(def.table, SQLiteDatabase.CONFLICT_ABORT, content(values))
        }

        fun update(db: SupportSQLiteDatabase, id: Long, payload: SortedMap<String, Any?>) {
            if (payload.isEmpty()) return
            db.update(def.table, SQLiteDatabase.CONFLICT_ABORT, content(payload), "`$pk` = ?", arrayOf<Any?>(id))
        }

        fun delete(db: SupportSQLiteDatabase, id: Long) {
            db.delete(def.table, "`$pk` = ?", arrayOf<Any?>(id))
        }

        private fun content(values: Map<String, Any?>) = ContentValues().apply {
            for ((k, v) in values) {
                when (v) {
                    null -> putNull(k)
                    is Long -> put(k, v)
                    is Double -> put(k, v)
                    else -> put(k, v.toString())
                }
            }
        }

        companion object {
            /** Reads the table's real columns: the engine follows the schema it finds. Null = table absent. */
            fun load(db: SupportSQLiteDatabase, def: SyncTable): TableInfo? {
                var pk = "Id"
                val columns = mutableListOf<Column>()
                var found = false
                db.query("PRAGMA table_info(`${def.table}`)").use { c ->
                    while (c.moveToNext()) {
                        found = true
                        val name = c.getString(1)
                        if (c.getInt(5) != 0) { pk = name; continue }
                        if (def.localOnly.any { it.equals(name, ignoreCase = true) }) continue
                        columns += Column(name, c.getString(2).uppercase(), c.getInt(3) != 0, !c.isNull(4))
                    }
                }
                return if (found) TableInfo(def, pk, columns.sortedBy { it.name }) else null
            }
        }
    }

    private companion object {
        const val PULL_PAGE = 500
        const val MAP_COLUMNS = "sync_id, local_id, rev, synced_hash, synced_values, deleted"
        const val PUSH_BATCH = 200
    }
}
