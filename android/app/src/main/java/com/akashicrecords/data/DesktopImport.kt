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
// column the desktop lacks gets a neutral default.
object DesktopImport {

    data class Result(val tables: Int, val rows: Int, val mediaFiles: Int)

    fun run(context: Context, source: Uri, db: AkashicDatabase): Result {
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

            dest.beginTransaction()
            try {
                for (table in destTables) {
                    if (table !in srcTables) continue
                    val destCols = dest.query("PRAGMA table_info(`$table`)").use(::columns)
                    val srcCols = src.rawQuery("PRAGMA table_info(`$table`)", null).use(::columns)
                    val shared = destCols.keys.filter { it in srcCols.keys }
                    if (shared.isEmpty()) continue
                    val fillers = destCols.filter { (name, col) -> name !in srcCols && col.notNull }

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
                    tables++
                }
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

    private data class Col(val type: String, val notNull: Boolean)

    private fun columns(c: Cursor): Map<String, Col> = buildMap {
        val name = c.getColumnIndexOrThrow("name")
        val type = c.getColumnIndexOrThrow("type")
        val notNull = c.getColumnIndexOrThrow("notnull")
        while (c.moveToNext()) put(c.getString(name), Col(c.getString(type).uppercase(), c.getInt(notNull) == 1))
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
                if (entry.isDirectory) continue
                val path = entry.name.replace('\\', '/')
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
