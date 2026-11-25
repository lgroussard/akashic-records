package com.akashicrecords.sync

import androidx.sqlite.db.SupportSQLiteDatabase
import java.io.File
import java.security.MessageDigest

class FileSyncReport {
    var uploaded = 0
    var downloaded = 0
    var missing = 0
}

/**
 * The files the rows point at (covers, photos, tickets, books). Same method as the desktop's
 * src/AkashicRecords.Sync/FileSync.cs (see there for the reasoning), kept in step with it by hand:
 *
 *  - `sync_file` lists, for each referenced path, the SHA-256 and size of its content; it is an ordinary
 *    synchronised table (entity type "MediaFile");
 *  - before the rows are synchronised, every referenced file present here and not listed yet is hashed, its
 *    content sent to the hub, and listed;
 *  - after, every listed file missing here is fetched from the hub.
 *
 * Paths are relative to the app's files folder, in the same form as on the PC ("media\covers\x.jpg",
 * "books\x.epub", "references/x.jpg"). A file is never deleted nor overwritten by this.
 */
class FileSync(private val db: SupportSQLiteDatabase, private val baseDir: File, private val hub: HubClient, private val columns: List<Pair<String, String>>) {

    private fun file(normalized: String) = File(baseDir, normalized)

    /** Hashes, sends and lists the referenced files that are here. Run BEFORE the row engine. */
    fun publish(report: FileSyncReport) {
        for (path in referencedPaths()) {
            val f = file(path)
            if (!f.isFile) continue

            var hash: String? = null
            var uploaded = false
            db.query("SELECT hash, uploaded FROM sync_file_local WHERE path = ? AND size = ? AND modified = ?", arrayOf<Any?>(path, f.length(), f.lastModified()))
                .use { c ->
                    if (c.moveToFirst()) {
                        hash = c.getString(0)
                        uploaded = c.getLong(1) != 0L
                    }
                }
            val known = hash ?: sha256(f).also {
                db.execSQL(
                    "INSERT OR REPLACE INTO sync_file_local(path, size, modified, hash, uploaded) VALUES (?, ?, ?, ?, 0)",
                    arrayOf<Any?>(path, f.length(), f.lastModified(), it),
                )
                uploaded = false
            }
            if (!uploaded) {
                // The content first, the listing after: a device that sees the listing can fetch at once.
                if (!hub.hasFile(known)) {
                    hub.uploadFile(f, known)
                    report.uploaded++
                }
                db.execSQL("UPDATE sync_file_local SET uploaded = 1 WHERE path = ?", arrayOf<Any?>(path))
            }
            // Listed once. A path whose listing says another content keeps the listing (file names are random ids).
            db.execSQL("INSERT OR IGNORE INTO sync_file(Path, Hash, Size) VALUES (?, ?, ?)", arrayOf<Any?>(path, known, f.length()))
        }
    }

    /** Fetches the listed files that are missing here. Run AFTER the row engine. */
    fun fetch(report: FileSyncReport) {
        val listed = db.query("SELECT Path, Hash FROM sync_file").use { c -> buildList { while (c.moveToNext()) add(c.getString(0) to c.getString(1)) } }
        for ((raw, hash) in listed) {
            val path = normalize(raw) ?: continue
            if (hash.length != 64 || !hash.all { it.isDigit() || it.lowercaseChar() in 'a'..'f' }) continue // not something to write to disk
            val target = file(path)
            if (target.exists()) continue
            if (hub.downloadFile(hash.lowercase(), target)) {
                db.execSQL(
                    "INSERT OR REPLACE INTO sync_file_local(path, size, modified, hash, uploaded) VALUES (?, ?, ?, ?, 1)",
                    arrayOf<Any?>(path, target.length(), target.lastModified(), hash.lowercase()),
                )
                report.downloaded++
            } else {
                report.missing++ // listed by a device that has not sent the content yet
            }
        }
    }

    private fun referencedPaths(): List<String> {
        val paths = sortedSetOf<String>()
        for ((table, column) in columns) {
            val exists = db.query("SELECT COUNT(*) FROM pragma_table_info(?) WHERE name = ?", arrayOf<Any?>(table, column)).use { c -> c.moveToFirst() && c.getLong(0) > 0 }
            if (!exists) continue
            db.query("SELECT DISTINCT `$column` FROM `$table` WHERE `$column` IS NOT NULL AND `$column` <> ''").use { c ->
                while (c.moveToNext()) normalize(c.getString(0))?.let(paths::add)
            }
        }
        return paths.toList()
    }

    private fun sha256(f: File): String {
        val sha = MessageDigest.getInstance("SHA-256")
        f.inputStream().use { input ->
            val buffer = ByteArray(81920)
            while (true) {
                val n = input.read(buffer)
                if (n < 0) break
                sha.update(buffer, 0, n)
            }
        }
        return sha.digest().joinToString("") { "%02x".format(it) }
    }

    companion object {
        /** The index of files, synchronised with the rest (added to the engine's tables). */
        val indexTable = SyncTable("MediaFile", "sync_file", matchColumns = listOf("Path"))

        /** Only these top folders of the app folder hold synchronised files. */
        private val roots = listOf("media/", "books/", "references/")

        /** Creates `sync_file` and the local cache of hashes. Call before the row engine loads its tables. */
        fun ensureSchema(db: SupportSQLiteDatabase) {
            db.execSQL("CREATE TABLE IF NOT EXISTS sync_file(Id INTEGER PRIMARY KEY AUTOINCREMENT, Path TEXT NOT NULL UNIQUE, Hash TEXT NOT NULL, Size INTEGER NOT NULL)")
            db.execSQL(
                "CREATE TABLE IF NOT EXISTS sync_file_local(path TEXT PRIMARY KEY, size INTEGER NOT NULL, modified INTEGER NOT NULL, " +
                    "hash TEXT NOT NULL, uploaded INTEGER NOT NULL DEFAULT 0)",
            )
        }

        /** "media\covers\x.jpg" -> "media/covers/x.jpg"; null for anything that is not a plain path under one of
         * the synchronised folders. */
        fun normalize(path: String?): String? {
            if (path.isNullOrBlank()) return null
            val clean = path.replace('\\', '/').trimStart('/')
            if (clean.split('/').any { it.isEmpty() || it == "." || it == ".." }) return null
            return if (roots.any { clean.startsWith(it, ignoreCase = true) }) clean else null
        }
    }
}
