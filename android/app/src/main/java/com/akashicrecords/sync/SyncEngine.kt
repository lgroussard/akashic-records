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
data class SyncTable(val entityType: String, val table: String, val localOnly: Set<String> = emptySet())

class SyncReport {
    var pulled = 0
    var inserted = 0
    var updated = 0
    var deleted = 0
    var adopted = 0
    var pushed = 0
    var pushedDeletes = 0
    var conflicts = 0
    var cursor = 0L
    val received get() = inserted + updated + deleted
    val nothingToDo get() = inserted + updated + deleted + adopted + pushed + pushedDeletes == 0
    override fun toString() =
        "recu $pulled (ajouts $inserted, modifs $updated, suppressions $deleted, reconnus $adopted) ; " +
            "envoye $pushed (dont suppressions $pushedDeletes) ; conflits $conflicts ; curseur $cursor"
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
 * Conflict policy: last writer wins, per entity. A row changed here AND on the hub since the last sync keeps
 * this device's version and is pushed over the hub's.
 */
class SyncEngine(private val db: SupportSQLiteDatabase, private val hub: HubClient, private val tables: List<SyncTable>) {

    /** Safety brake: a run that would delete more than this share of a table's synchronised rows on the hub
     * (and more than [minDeletesForBrake]) stops instead. A database swapped under the engine looks exactly
     * like "the user deleted everything". */
    var maxDeleteShare = 0.30
    var minDeletesForBrake = 5

    fun run(): SyncReport {
        val report = SyncReport()
        ensureSchema()
        val infos = tables.mapNotNull { TableInfo.load(db, it) }

        pull(infos, report)
        for (round in 0 until 4) {
            val pending = infos.flatMap { scan(it) }
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
                setCursor(page.getLong("next_cursor"))
            }
            if (!page.getBoolean("has_more")) break
        }
    }

    private fun apply(t: TableInfo, row: JSONObject, report: SyncReport) {
        val type = t.def.entityType
        val syncId = row.getString("entity_id")
        val rev = row.getLong("rev")
        val payload: SortedMap<String, Any?>? =
            if (row.optBoolean("deleted") || row.isNull("payload")) null else t.project(row.getJSONObject("payload"))
        val map = mapBySyncId(type, syncId)

        if (map == null) {
            if (payload == null) {
                upsertMap(type, syncId, null, rev, null, true)
                return
            }
            // First contact with this entity. If an identical row is already here and not yet linked (both
            // devices started from the same data), recognise it instead of creating a twin.
            val twin = t.findUnmappedTwin(db, payload)
            if (twin != null) {
                upsertMap(type, syncId, twin, rev, t.hashOfRow(db, twin), false)
                report.adopted++
                return
            }
            val id = t.insert(db, payload)
            upsertMap(type, syncId, id, rev, t.hashOfRow(db, id), false)
            report.inserted++
            return
        }

        if (rev <= map.rev) return // already known (our own push coming back, or a replay)

        val localHash = map.localId?.let { t.hashOfRow(db, it) }
        val locallyChanged = !map.deleted && (localHash == null || localHash != map.syncedHash)
        if (locallyChanged && sameContent(t, map, localHash, payload)) {
            // Both sides hold the same thing (typically our own push whose answer got lost).
            upsertMap(type, syncId, if (payload == null) null else map.localId, rev, localHash, payload == null)
            return
        }
        if (locallyChanged) {
            // Both sides changed. Last writer wins per entity: keep ours, remember the hub's revision so the
            // next push goes on top of it. (local_id is kept even if the row is gone: that is how the scan
            // sees a pending delete.)
            upsertMap(type, syncId, map.localId, rev, map.syncedHash, false)
            report.conflicts++
            return
        }

        if (payload == null) {
            if (map.localId != null && localHash != null) {
                t.delete(db, map.localId)
                report.deleted++
            }
            upsertMap(type, syncId, null, rev, null, true)
        } else if (map.deleted || map.localId == null) {
            val id = t.insert(db, payload) // deleted here earlier, alive again on the hub
            upsertMap(type, syncId, id, rev, t.hashOfRow(db, id), false)
            report.inserted++
        } else {
            t.update(db, map.localId, payload)
            upsertMap(type, syncId, map.localId, rev, t.hashOfRow(db, map.localId), false)
            report.updated++
        }
    }

    private fun sameContent(t: TableInfo, map: MapEntry, localHash: String?, payload: SortedMap<String, Any?>?): Boolean {
        if (payload == null) return localHash == null // deleted on both sides
        if (localHash == null || map.localId == null) return false
        val local = t.readRow(db, map.localId) ?: return false
        return SyncValues.matches(local, payload)
    }

    // ------------------------------------------------------------------ scan & push

    private class Pending(val table: TableInfo, val syncId: String, val localId: Long?, val baseRev: Long, val hash: String?, val values: SortedMap<String, Any?>?) {
        val isDelete get() = values == null
        val mutationId get() = SyncValues.mutationId(table.def.entityType, syncId, baseRev, hash)
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
        if (deletes.size > minDeletesForBrake && deletes.size > byLocal.size * maxDeleteShare) {
            throw SyncSafetyException(
                "${deletes.size} $type sur ${byLocal.size} ont disparu de ce téléphone depuis la dernière synchro. " +
                    "Cela ressemble à une base remplacée plutôt qu'à des suppressions voulues : rien n'a été envoyé au hub.",
            )
        }
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
                        .put("payload", p.values?.let(SyncValues::toPayload) ?: JSONObject.NULL),
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
                            upsertMap(type, p.syncId, if (p.isDelete) null else p.localId, rev, p.hash, p.isDelete)
                            report.pushed++
                            if (p.isDelete) report.pushedDeletes++
                        }
                        else -> {
                            // The hub moved since we last looked. Keep our version, take the hub's revision as
                            // the new base: the next round sends it again on top (last writer wins).
                            val map = mapBySyncId(type, p.syncId)!!
                            upsertMap(type, p.syncId, map.localId, rev, map.syncedHash, false)
                            report.conflicts++
                            conflicts++
                        }
                    }
                }
            }
        }
        return conflicts
    }

    // ------------------------------------------------------------------ sync_map rows

    private class MapEntry(val syncId: String, val localId: Long?, val rev: Long, val syncedHash: String?, val deleted: Boolean)

    private fun readMap(c: Cursor) =
        MapEntry(c.getString(0), if (c.isNull(1)) null else c.getLong(1), c.getLong(2), if (c.isNull(3)) null else c.getString(3), c.getLong(4) != 0L)

    private fun mapBySyncId(type: String, syncId: String): MapEntry? =
        db.query("SELECT sync_id, local_id, rev, synced_hash, deleted FROM sync_map WHERE entity_type = ? AND sync_id = ?", arrayOf<Any?>(type, syncId))
            .use { c -> if (c.moveToFirst()) readMap(c) else null }

    private fun allMaps(type: String): List<MapEntry> =
        db.query("SELECT sync_id, local_id, rev, synced_hash, deleted FROM sync_map WHERE entity_type = ?", arrayOf<Any?>(type))
            .use { c -> buildList { while (c.moveToNext()) add(readMap(c)) } }

    private fun upsertMap(type: String, syncId: String, localId: Long?, rev: Long, hash: String?, deleted: Boolean) =
        db.execSQL(
            "INSERT INTO sync_map(entity_type, sync_id, local_id, rev, synced_hash, deleted) VALUES (?, ?, ?, ?, ?, ?) " +
                "ON CONFLICT(entity_type, sync_id) DO UPDATE SET local_id = excluded.local_id, rev = excluded.rev, " +
                "synced_hash = excluded.synced_hash, deleted = excluded.deleted",
            arrayOf<Any?>(type, syncId, localId, rev, hash, if (deleted) 1 else 0),
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

        fun hashOfRow(db: SupportSQLiteDatabase, id: Long): String? = readRow(db, id)?.let { SyncValues.sha(SyncValues.canonical(it)) }

        fun findUnmappedTwin(db: SupportSQLiteDatabase, payload: SortedMap<String, Any?>): Long? {
            if (payload.isEmpty()) return null
            db.query(
                "SELECT `$pk`, $columnList FROM `${def.table}` WHERE `$pk` NOT IN " +
                    "(SELECT local_id FROM sync_map WHERE entity_type = ? AND local_id IS NOT NULL)",
                arrayOf<Any?>(def.entityType),
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
        const val PUSH_BATCH = 200
    }
}
