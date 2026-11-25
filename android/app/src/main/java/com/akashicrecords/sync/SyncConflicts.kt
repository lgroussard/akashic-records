package com.akashicrecords.sync

import androidx.sqlite.db.SupportSQLiteDatabase

/** A version that lost a field-level conflict, kept so nothing is lost silently. */
data class SyncConflict(
    val id: Long,
    val entityType: String,
    val label: String,
    val field: String,
    val keptValue: String?,
    val lostValue: String?,
    val lostDevice: String,
    val createdAt: String,
)

/** The `sync_conflict` table: what the engine sets aside, and what the settings show. Local to this device. */
object SyncConflicts {
    const val CREATE =
        "CREATE TABLE IF NOT EXISTS sync_conflict(id INTEGER PRIMARY KEY AUTOINCREMENT, entity_type TEXT NOT NULL, " +
            "sync_id TEXT NOT NULL, local_id INTEGER, label TEXT NOT NULL, field TEXT NOT NULL, kept_value TEXT, " +
            "lost_value TEXT, lost_device TEXT NOT NULL, created_at TEXT NOT NULL)"

    /** Newest first. */
    fun list(db: SupportSQLiteDatabase): List<SyncConflict> {
        db.execSQL(CREATE)
        return db.query("SELECT id, entity_type, label, field, kept_value, lost_value, lost_device, created_at FROM sync_conflict ORDER BY id DESC")
            .use { c ->
                buildList {
                    while (c.moveToNext()) {
                        add(
                            SyncConflict(
                                c.getLong(0), c.getString(1), c.getString(2), c.getString(3),
                                if (c.isNull(4)) null else c.getString(4), if (c.isNull(5)) null else c.getString(5),
                                c.getString(6), c.getString(7),
                            ),
                        )
                    }
                }
            }
    }

    /** Forgets one set-aside version (the user has read it, or taken it back by hand). */
    fun dismiss(db: SupportSQLiteDatabase, id: Long) = db.execSQL("DELETE FROM sync_conflict WHERE id = ?", arrayOf<Any?>(id))
}
