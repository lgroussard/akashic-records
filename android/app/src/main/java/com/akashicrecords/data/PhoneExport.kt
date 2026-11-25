package com.akashicrecords.data

import android.content.Context
import android.net.Uri
import java.io.File
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

// The phone's data as one .zip, for the PC to merge with its own (merge-phone.ps1 at the repo root).
// Same layout as the desktop dump: data/akashic.db and media/**, plus backups/avant-import-*.db (the phone
// as it was before its last imports: helps the PC find which dump both sides started from).
// Reads only: nothing on the phone is changed.
object PhoneExport {

    data class Result(val files: Int, val bytes: Long)

    fun run(context: Context, target: Uri, db: AkashicDatabase): Result {
        // Room writes through a journal file: folded into akashic.db first, so the copy is the whole database.
        db.openHelper.writableDatabase.query("PRAGMA wal_checkpoint(TRUNCATE)").use { it.moveToFirst() }

        var files = 0
        var bytes = 0L
        val out = requireNotNull(context.contentResolver.openOutputStream(target, "wt")) { "Impossible d'écrire le fichier choisi." }
        ZipOutputStream(out.buffered()).use { zip ->
            fun add(name: String, file: File) {
                zip.putNextEntry(ZipEntry(name).apply { time = file.lastModified() })
                file.inputStream().use { bytes += it.copyTo(zip) }
                zip.closeEntry()
                files++
            }

            add("data/akashic.db", context.getDatabasePath("akashic.db"))
            File(context.filesDir, "backups").listFiles { f -> f.isFile && f.name.endsWith(".db") }
                ?.forEach { add("backups/${it.name}", it) }
            val media = File(context.filesDir, "media")
            media.walkTopDown().filter { it.isFile }.forEach { add("media/" + it.relativeTo(media).invariantSeparatorsPath, it) }
        }
        return Result(files, bytes)
    }
}
