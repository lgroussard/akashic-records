package com.akashicrecords.sync

import android.os.SystemClock
import androidx.sqlite.db.SupportSQLiteDatabase
import java.io.File
import java.io.InputStream
import java.security.MessageDigest

class FileSyncReport {
    var uploaded = 0
    var downloaded = 0
    var downloadedMusic = 0
    var missing = 0  // listed, wanted, but the hub does not have the content yet
    var toSend = 0   // left for the next run (time budget reached)
    var toFetch = 0
}

/**
 * The files the rows point at (covers, photos, tickets, books, music). Same method as the desktop's
 * src/AkashicRecords.Sync/FileSync.cs (see there for the reasoning), kept in step with it by hand:
 *
 *  - `sync_file` lists, for each path, the SHA-256 and size of its content; it is an ordinary synchronised
 *    table (entity type "MediaFile");
 *  - before the rows are synchronised, every file present here and not listed yet is hashed, its content sent
 *    to the hub, and listed;
 *  - after, the listed files missing here and wanted here are fetched from the hub.
 *
 * Paths are relative to the app's files folder, in the same form as on the PC ("media\covers\x.jpg",
 * "books\x.epub", "references/x.jpg"). Music is apart on two counts: its files live in the phone's shared Music
 * folder (through [music]), and the phone only fetches the tracks chosen for it (`sync_keep`). A track whose
 * file was here and has been removed is remembered as "not kept here" instead of being fetched again.
 *
 * Each call works for a limited time ([budgetMs]) and picks up where it stopped at the next one.
 * A file is never deleted nor overwritten by this.
 */
class FileSync(
    private val db: SupportSQLiteDatabase,
    private val baseDir: File,
    private val cacheDir: File,
    private val hub: HubClient,
    private val columns: List<Pair<String, String>>,
    private val music: MusicFiles?,
    private val budgetMs: Long = 25_000,
) {
    private fun file(normalized: String) = File(baseDir, normalized)

    private class Candidate(val path: String, val size: Long, val modified: Long, val open: () -> InputStream)

    /** Hashes, sends and lists the files that are here. Run BEFORE the row engine. */
    fun publish(report: FileSyncReport) {
        val start = SystemClock.elapsedRealtime()
        val done = db.query("SELECT l.path FROM sync_file_local l JOIN sync_file f ON f.Path = l.path WHERE l.uploaded = 1")
            .use { c -> buildSet { while (c.moveToNext()) add(c.getString(0)) } }

        val todo = ArrayList<Candidate>()
        for (path in referencedPaths()) {
            if (path in done || isMusic(path)) continue
            val f = file(path)
            if (f.isFile) todo += Candidate(path, f.length(), f.lastModified()) { f.inputStream() }
        }
        // Then this phone's music, when the network allows it (never on mobile data).
        if (music != null && music.allowedNow) {
            for (m in music.local()) if (m.path !in done) todo += Candidate(m.path, m.size, m.modified) { music.open(m) }
        }

        var handled = 0
        for ((i, c) in todo.withIndex()) {
            // At least one file per call, whatever the budget: a run always moves forward.
            if (handled++ > 0 && SystemClock.elapsedRealtime() - start > budgetMs) {
                report.toSend = todo.size - i
                return
            }
            var hash: String? = null
            var uploaded = false
            db.query("SELECT hash, uploaded FROM sync_file_local WHERE path = ? AND size = ? AND modified = ?", arrayOf<Any?>(c.path, c.size, c.modified)).use { cur ->
                if (cur.moveToFirst()) {
                    hash = cur.getString(0)
                    uploaded = cur.getLong(1) != 0L
                }
            }
            val known = hash ?: sha256(c.open).also {
                db.execSQL("INSERT OR REPLACE INTO sync_file_local(path, size, modified, hash, uploaded) VALUES (?, ?, ?, ?, 0)", arrayOf<Any?>(c.path, c.size, c.modified, it))
                uploaded = false
            }
            if (!uploaded) {
                // The content first, the listing after: a device that sees the listing can fetch at once.
                if (!hub.hasFile(known)) {
                    hub.uploadStream(c.size, known, c.open)
                    report.uploaded++
                }
                db.execSQL("UPDATE sync_file_local SET uploaded = 1 WHERE path = ?", arrayOf<Any?>(c.path))
            }
            // Listed once, unless the catalogue already lists that path (the PC may spell its case differently).
            val listed = db.query("SELECT COUNT(*) FROM sync_file WHERE lower(Path) = ?", arrayOf<Any?>(c.path.lowercase())).use { cur -> cur.moveToFirst() && cur.getLong(0) > 0 }
            if (!listed) db.execSQL("INSERT OR IGNORE INTO sync_file(Path, Hash, Size) VALUES (?, ?, ?)", arrayOf<Any?>(c.path, known, c.size))
        }
    }

    /** Fetches the listed files that are missing here and wanted here. Run AFTER the row engine. */
    fun fetch(report: FileSyncReport) {
        val start = SystemClock.elapsedRealtime()
        class Listed(val path: String, val hash: String, val wasHere: Boolean, val keep: Long?)

        applyKeptLists(db)
        // Paths are compared in Kotlin: SQLite's lower() only folds ASCII, "É" would never match its key.
        val wasHere = db.query("SELECT path FROM sync_file_local").use { c -> buildSet { while (c.moveToNext()) add(MusicFiles.key(c.getString(0))) } }
        val keeps = db.query("SELECT path, keep FROM sync_keep").use { c -> buildMap { while (c.moveToNext()) put(c.getString(0), c.getLong(1)) } }
        val listed = db.query("SELECT Path, Hash FROM sync_file ORDER BY Path").use { c ->
            buildList {
                while (c.moveToNext()) {
                    val key = MusicFiles.key(c.getString(0))
                    add(Listed(c.getString(0), c.getString(1), key in wasHere, keeps[key]))
                }
            }
        }
        val musicHere = music?.local()?.map { MusicFiles.key(it.path) }?.toSet() ?: emptySet()
        val musicAllowed = music != null && music.allowedNow

        val wanted = ArrayList<Listed>()
        for (l in listed) {
            val path = normalize(l.path) ?: continue
            if (l.hash.length != 64 || !l.hash.all { it.isDigit() || it.lowercaseChar() in 'a'..'f' }) continue // not something to write to disk
            if (isMusic(path)) {
                if (music == null || MusicFiles.key(path) in musicHere) continue
                if (l.keep == 0L) continue
                if (l.wasHere && l.keep != 1L) {
                    // It was here and is gone: removed from this phone on purpose. Remembered, not undone
                    // (unless it is explicitly asked for again: keep = 1).
                    db.execSQL("INSERT INTO sync_keep(path, keep) VALUES (?, 0) ON CONFLICT(path) DO UPDATE SET keep = 0", arrayOf<Any?>(MusicFiles.key(path)))
                    continue
                }
                if (l.keep != 1L) continue // the phone only holds the tracks chosen for it
                if (!musicAllowed) {
                    report.toFetch++
                    continue
                }
            } else if (file(path).exists()) {
                continue
            }
            wanted += Listed(path, l.hash.lowercase(), l.wasHere, l.keep)
        }

        for ((i, w) in wanted.sortedBy { isMusic(it.path) }.withIndex()) {
            if (i > 0 && SystemClock.elapsedRealtime() - start > budgetMs) {
                report.toFetch += wanted.size - i
                return
            }
            if (isMusic(w.path)) {
                val temp = File(cacheDir, "sync-music-${w.hash}.part")
                try {
                    if (!hub.downloadFile(w.hash, temp)) {
                        report.missing++
                        continue
                    }
                    music!!.save(w.path, temp)
                    db.execSQL("INSERT OR REPLACE INTO sync_file_local(path, size, modified, hash, uploaded) VALUES (?, ?, 0, ?, 1)", arrayOf<Any?>(w.path, temp.length(), w.hash))
                    report.downloaded++
                    report.downloadedMusic++
                } finally {
                    temp.delete()
                }
            } else {
                val target = file(w.path)
                if (hub.downloadFile(w.hash, target)) {
                    db.execSQL(
                        "INSERT OR REPLACE INTO sync_file_local(path, size, modified, hash, uploaded) VALUES (?, ?, ?, ?, 1)",
                        arrayOf<Any?>(w.path, target.length(), target.lastModified(), w.hash),
                    )
                    report.downloaded++
                } else {
                    report.missing++ // listed by a device that has not sent the content yet
                }
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

    private fun sha256(open: () -> InputStream): String {
        val sha = MessageDigest.getInstance("SHA-256")
        open().use { input ->
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

        /** Only these top folders hold synchronised files. */
        private val roots = listOf("media/", "books/", "references/", "music/")

        /** Creates `sync_file` and this phone's own tables. Call before the row engine loads its tables. */
        fun ensureSchema(db: SupportSQLiteDatabase) {
            db.execSQL("CREATE TABLE IF NOT EXISTS sync_file(Id INTEGER PRIMARY KEY AUTOINCREMENT, Path TEXT NOT NULL UNIQUE, Hash TEXT NOT NULL, Size INTEGER NOT NULL)")
            db.execSQL(
                "CREATE TABLE IF NOT EXISTS sync_file_local(path TEXT PRIMARY KEY, size INTEGER NOT NULL, modified INTEGER NOT NULL, " +
                    "hash TEXT NOT NULL, uploaded INTEGER NOT NULL DEFAULT 0)",
            )
            // This phone only: music wanted here (1) or not (0), by catalogue key (lower case, forward slashes).
            db.execSQL("CREATE TABLE IF NOT EXISTS sync_keep(path TEXT PRIMARY KEY, keep INTEGER NOT NULL)")
            // This phone only: whole lists kept here (kind "album": name = the album; kind "mood": name = the
            // ambiance's Id). Their tracks, present and future, are wanted here unless one was removed by hand.
            db.execSQL("CREATE TABLE IF NOT EXISTS sync_keep_list(kind TEXT NOT NULL, name TEXT NOT NULL, PRIMARY KEY(kind, name))")
        }

        /** "media\covers\x.jpg" -> "media/covers/x.jpg"; null for anything that is not a plain path under one of
         * the synchronised folders. */
        fun normalize(path: String?): String? {
            if (path.isNullOrBlank()) return null
            val clean = path.replace('\\', '/').trimStart('/')
            if (clean.split('/').any { it.isEmpty() || it == "." || it == ".." }) return null
            return if (roots.any { clean.startsWith(it, ignoreCase = true) }) clean else null
        }

        fun isMusic(normalized: String) = normalized.startsWith("music/", ignoreCase = true)

        fun setKeepList(db: SupportSQLiteDatabase, kind: String, name: String, keep: Boolean) {
            ensureSchema(db)
            if (keep) db.execSQL("INSERT OR IGNORE INTO sync_keep_list(kind, name) VALUES (?, ?)", arrayOf<Any?>(kind, name))
            else db.execSQL("DELETE FROM sync_keep_list WHERE kind = ? AND name = ?", arrayOf<Any?>(kind, name))
        }

        fun keptLists(db: SupportSQLiteDatabase, kind: String): Set<String> {
            ensureSchema(db)
            return db.query("SELECT name FROM sync_keep_list WHERE kind = ?", arrayOf<Any?>(kind)).use { c -> buildSet { while (c.moveToNext()) add(c.getString(0)) } }
        }

        /** The tracks of the kept lists that this phone has no choice for yet become wanted here: that is how a
         * title added to a kept album or ambiance (on any device) arrives by itself. A track removed by hand
         * (keep = 0) stays removed. */
        fun applyKeptLists(db: SupportSQLiteDatabase) {
            ensureSchema(db)
            val paths = runCatching {
                db.query(
                    "SELECT FilePath FROM MusicTrack WHERE Album IN (SELECT name FROM sync_keep_list WHERE kind = 'album') " +
                        "OR Id IN (SELECT TrackId FROM TrackMood WHERE CAST(MoodId AS TEXT) IN (SELECT name FROM sync_keep_list WHERE kind = 'mood'))",
                ).use { c -> buildList { while (c.moveToNext()) add(c.getString(0)) } }
            }.getOrDefault(emptyList())
            for (p in paths) db.execSQL("INSERT OR IGNORE INTO sync_keep(path, keep) VALUES (?, 1)", arrayOf<Any?>(MusicFiles.key(p)))
        }

        /** The phone's choice for a track: keep its file here, or not. */
        fun setKeep(db: SupportSQLiteDatabase, path: String, keep: Boolean) {
            ensureSchema(db)
            db.execSQL(
                "INSERT INTO sync_keep(path, keep) VALUES (?, ?) ON CONFLICT(path) DO UPDATE SET keep = excluded.keep",
                arrayOf<Any?>(MusicFiles.key(path), if (keep) 1 else 0),
            )
        }
    }
}
