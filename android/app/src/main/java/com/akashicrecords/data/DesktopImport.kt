package com.akashicrecords.data

import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.sqlite.SQLiteDatabase
import android.net.Uri
import java.io.File
import java.util.zip.ZipInputStream

// One-shot copy of the desktop data onto the phone (no sync — MOBILITE-SYNC.md keeps that for
// later). Accepts either the desktop akashic.db alone, or a .zip of the desktop app folder
// containing data/akashic.db and optionally media/ (music/ is skipped: the phone uses its own
// music folder).
//
// The phone database is REPLACED table by table: every table the desktop also has is emptied
// and refilled with the desktop rows, Ids included, so all cross-references stay valid.
// Columns are matched by name (the Room entities mirror the desktop schema); a NOT NULL phone
// column the desktop lacks keeps its own default when it has one, and gets a neutral value otherwise.
object DesktopImport {

    data class Result(val tables: Int, val rows: Int, val mediaFiles: Int)

    // Tables the phone owns (the PC has them too, empty): never overwritten by an import. Their links into
    // Artwork/Observation, which the import does refill, are repaired right after (ReadingLinks.relinkAfterImport).
    private val PHONE_ONLY = setOf("Book", "BookHighlight", "RefBoard", "RefImage", "CrosswordGame", "Ticket", "ShoppingItem")

    // The phone database as it was before each import, the two latest kept: what was written on the phone only
    // is replaced by an import, and this is the way back (undo() below).
    private fun backupDir(context: Context) = File(context.filesDir, "backups")

    fun lastBackup(context: Context): File? =
        backupDir(context).listFiles { f -> f.name.startsWith("avant-import-") && f.name.endsWith(".db") }?.maxByOrNull { it.name }

    private fun backup(context: Context, db: AkashicDatabase) {
        // Room writes through a journal file: folded into akashic.db first, so the copy is the whole database.
        db.openHelper.writableDatabase.query("PRAGMA wal_checkpoint(TRUNCATE)").use { it.moveToFirst() }
        val dir = backupDir(context).apply { mkdirs() }
        val stamp = java.text.SimpleDateFormat("yyyy-MM-dd_HH-mm-ss", java.util.Locale.US).format(java.util.Date())
        context.getDatabasePath("akashic.db").copyTo(File(dir, "avant-import-$stamp.db"), overwrite = true)
        dir.listFiles { f -> f.name.startsWith("avant-import-") }?.sortedByDescending { it.name }?.drop(2)?.forEach { it.delete() }
    }

    // Puts the shared tables back as they were before the last import (the tables the phone owns were never
    // touched by it). Media files the import overwrote are not restored.
    fun undo(context: Context, db: AkashicDatabase): Result {
        val file = requireNotNull(lastBackup(context)) { "Aucune copie d'avant import." }
        return run(context, Uri.fromFile(file), db, keepBackup = false)
    }

    fun run(context: Context, source: Uri, db: AkashicDatabase, keepBackup: Boolean = true): Result {
        val work = File(context.cacheDir, "import").apply { deleteRecursively(); mkdirs() }
        val dbFile = File(work, "akashic.db")
        var media = 0

        val isZip = context.contentResolver.openInputStream(source)!!.use { s ->
            val head = ByteArray(4)
            s.read(head) == 4 && head[0] == 'P'.code.toByte() && head[1] == 'K'.code.toByte()
        }
        if (isZip) {
            media = unzip(context, source, dbFile)
            require(dbFile.isFile) { "Aucun fichier akashic.db trouvé dans le zip." }
        } else {
            context.contentResolver.openInputStream(source)!!.use { input ->
                dbFile.outputStream().use { input.copyTo(it) }
            }
        }

        val src = SQLiteDatabase.openDatabase(dbFile.path, null, SQLiteDatabase.OPEN_READONLY)
        var tables = 0
        var rows = 0
        try {
            val dest = db.openHelper.writableDatabase
            val destTables = dest.query(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('room_master_table','android_metadata','sqlite_sequence')",
            ).use { c -> buildList { while (c.moveToNext()) add(c.getString(0)) } }
            val srcTables = src.rawQuery("SELECT name FROM sqlite_master WHERE type='table'", null)
                .use { c -> buildSet { while (c.moveToNext()) add(c.getString(0)) } }

            // Some other SQLite file (or an empty one) would otherwise "import" nothing and report a success.
            require(destTables.any { it in srcTables && it !in PHONE_ONLY }) { "Ce fichier n'est pas une base Akashic." }
            if (keepBackup) backup(context, db)

            dest.beginTransaction()
            try {
                for (table in destTables) {
                    if (table !in srcTables || table in PHONE_ONLY) continue
                    val destCols = dest.query("PRAGMA table_info(`$table`)").use(::columns)
                    val srcCols = src.rawQuery("PRAGMA table_info(`$table`)", null).use(::columns)
                    val shared = destCols.keys.filter { it in srcCols.keys }
                    if (shared.isEmpty()) continue
                    // A column with a default of its own (Observation.OwnerKind = 'Artwork') is left to SQLite.
                    val fillers = destCols.filter { (name, col) -> name !in srcCols && col.notNull && !col.hasDefault }

                    // The phone's own layout of a poem (Mobile* columns): the PC has the columns but never
                    // fills them, so its NULLs would wipe what was set here. Kept by Id, put back below.
                    val mobileCols = if (table == "Poem") shared.filter { it.startsWith("Mobile") } else emptyList()
                    val mobileKept = mutableListOf<Pair<Long, ContentValues>>()
                    if (mobileCols.isNotEmpty()) {
                        dest.query("SELECT `Id`, ${mobileCols.joinToString { "`$it`" }} FROM `Poem`").use { c ->
                            while (c.moveToNext()) {
                                val v = ContentValues()
                                mobileCols.forEachIndexed { i, name -> if (!c.isNull(i + 1)) put(v, name, c, i + 1) }
                                if (v.size() > 0) mobileKept += c.getLong(0) to v
                            }
                        }
                    }

                    dest.execSQL("DELETE FROM `$table`")
                    src.rawQuery("SELECT ${shared.joinToString { "`$it`" }} FROM `$table`", null).use { c ->
                        while (c.moveToNext()) {
                            val v = ContentValues()
                            shared.forEachIndexed { i, name -> put(v, name, c, i) }
                            fillers.forEach { (name, col) ->
                                when (col.type) {
                                    "INTEGER" -> v.put(name, 0)
                                    "REAL" -> v.put(name, 0.0)
                                    else -> v.put(name, "")
                                }
                            }
                            dest.insert(table, SQLiteDatabase.CONFLICT_REPLACE, v)
                            rows++
                        }
                    }
                    for ((id, values) in mobileKept) {
                        for (name in values.keySet()) {
                            val one = ContentValues().apply { putAll(values); keySet().filter { it != name }.forEach(::remove) }
                            dest.update("Poem", SQLiteDatabase.CONFLICT_NONE, one, "`Id` = ? AND `$name` IS NULL", arrayOf<Any>(id))
                        }
                    }
                    tables++
                }
                // A desktop database from before observations had an owner: its rows are all artwork observations
                // (OwnerKind took its default above). Same backfill as the migration; a no-op on a current dump.
                dest.execSQL("UPDATE `Observation` SET `OwnerId` = `ArtworkId` WHERE `OwnerKind` = 'Artwork' AND `OwnerId` = 0")
                dest.setTransactionSuccessful()
            } finally {
                dest.endTransaction()
            }
        } finally {
            src.close()
            work.deleteRecursively()
        }
        return Result(tables, rows, media)
    }

    private data class Col(val type: String, val notNull: Boolean, val hasDefault: Boolean)

    private fun columns(c: Cursor): Map<String, Col> = buildMap {
        val name = c.getColumnIndexOrThrow("name")
        val type = c.getColumnIndexOrThrow("type")
        val notNull = c.getColumnIndexOrThrow("notnull")
        val default = c.getColumnIndexOrThrow("dflt_value")
        while (c.moveToNext()) put(c.getString(name), Col(c.getString(type).uppercase(), c.getInt(notNull) == 1, !c.isNull(default)))
    }

    private fun put(v: ContentValues, name: String, c: Cursor, i: Int) {
        when (c.getType(i)) {
            Cursor.FIELD_TYPE_NULL -> v.putNull(name)
            Cursor.FIELD_TYPE_INTEGER -> v.put(name, c.getLong(i))
            Cursor.FIELD_TYPE_FLOAT -> v.put(name, c.getDouble(i))
            Cursor.FIELD_TYPE_BLOB -> v.put(name, c.getBlob(i))
            else -> v.put(name, c.getString(i))
        }
    }

    // Extracts akashic.db to [dbOut] and media/** into <filesDir>/media. The zip may hold the
    // folders at its root or under one parent folder (e.g. net8.0-windows/data/akashic.db).
    private fun unzip(context: Context, source: Uri, dbOut: File): Int {
        val mediaRoot = File(context.filesDir, "media").canonicalFile
        var count = 0
        ZipInputStream(context.contentResolver.openInputStream(source)!!.buffered()).use { zip ->
            while (true) {
                val entry = zip.nextEntry ?: break
                val path = entry.name.replace('\\', '/')
                // A zip made on Windows names an empty folder "media\archive\": not a directory for
                // ZipEntry (it only knows "/"), and writing it as a file fails on the folder of that name.
                if (entry.isDirectory || path.endsWith("/")) continue
                val segments = path.split('/')
                when {
                    path.endsWith("data/akashic.db") || path == "akashic.db" ->
                        dbOut.outputStream().use { zip.copyTo(it) }
                    "media" in segments -> {
                        val rel = segments.drop(segments.indexOf("media") + 1).joinToString("/")
                        val target = File(mediaRoot, rel).canonicalFile
                        // Zip-slip guard: never write outside media/.
                        if (rel.isNotEmpty() && target.path.startsWith(mediaRoot.path + File.separator)) {
                            target.parentFile?.mkdirs()
                            target.outputStream().use { zip.copyTo(it) }
                            count++
                        }
                    }
                }
            }
        }
        return count
    }
}
