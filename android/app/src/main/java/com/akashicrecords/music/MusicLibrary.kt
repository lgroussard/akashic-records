package com.akashicrecords.music

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.MusicTrack
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

// Mobile counterpart of the desktop MusicLibraryScanner.
//
// The desktop scans <exe>/music/ and stores FilePath as "music\<album>\<file>.mp3".
// Here the scanned folder is a shared-storage folder (default Music/Akashic) read through
// MediaStore, and the SAME relative FilePath format is stored. That keeps rows imported from
// the PC database (with their ambiances) matched to files copied onto the phone, and keeps
// the path key stable for the future server sync.
//
// Difference with the desktop: rows whose file is not on the phone are NOT pruned. The phone
// only holds a user-chosen subset of the library (MOBILITE-SYNC.md, D3), so rows imported from
// the PC must survive; the UI simply lists the tracks that have a local file.
class MusicLibrary(private val context: Context, private val db: AkashicDatabase) {

    data class LocalFile(val uri: Uri, val relativePath: String, val album: String, val title: String, val artist: String, val durationMs: Long)

    // Normalized FilePath -> playable content Uri, rebuilt on each scan.
    @Volatile
    var available: Map<String, Uri> = emptyMap()
        private set

    suspend fun scanAndSync(folder: String): List<MusicTrack> {
        val files = queryFolder(folder)
        val dao = db.musicTrackDao()
        val existing = dao.getAll().associateBy { normalize(it.FilePath) }
        val now = LocalDateTime.now().format(DateTimeFormatter.ISO_LOCAL_DATE_TIME)

        for (f in files) {
            val key = normalize(f.relativePath)
            val row = existing[key]
            if (row == null) {
                dao.add(
                    MusicTrack(
                        FilePath = f.relativePath,
                        Title = f.title,
                        Artist = f.artist,
                        DurationSeconds = if (f.durationMs > 0) f.durationMs / 1000.0 else null,
                        CoverImagePath = null,
                        Album = f.album,
                        AddedAt = now,
                    ),
                )
            } else {
                if (row.Album != f.album) dao.updateAlbum(row.Id, f.album)
                if (row.DurationSeconds == null && f.durationMs > 0) dao.updateDuration(row.Id, f.durationMs / 1000.0)
            }
        }
        available = files.associate { normalize(it.relativePath) to it.uri }
        return dao.getAll()
    }

    fun uriFor(track: MusicTrack): Uri? = available[normalize(track.FilePath)]

    private fun queryFolder(folder: String): List<LocalFile> {
        val root = folder.trim().trim('/')
        if (root.isEmpty()) return emptyList()
        val collection = MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
        val modern = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q

        @Suppress("DEPRECATION")
        val pathColumn = if (modern) MediaStore.Audio.Media.RELATIVE_PATH else MediaStore.Audio.Media.DATA
        val projection = arrayOf(
            MediaStore.Audio.Media._ID,
            MediaStore.Audio.Media.DISPLAY_NAME,
            MediaStore.Audio.Media.ARTIST,
            MediaStore.Audio.Media.DURATION,
            pathColumn,
        )
        val selection = "$pathColumn LIKE ?"
        val arg = if (modern) "$root/%" else "%/$root/%"

        val out = ArrayList<LocalFile>()
        try {
            context.contentResolver.query(collection, projection, selection, arrayOf(arg), null)?.use { c ->
                val idCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media._ID)
                val nameCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DISPLAY_NAME)
                val artistCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.ARTIST)
                val durCol = c.getColumnIndexOrThrow(MediaStore.Audio.Media.DURATION)
                val pathCol = c.getColumnIndexOrThrow(pathColumn)
                while (c.moveToNext()) {
                    val name = c.getString(nameCol) ?: continue
                    val rawPath = c.getString(pathCol) ?: continue
                    // Directory of the file, relative to the scanned root ("" or "Album/Sub").
                    val dir = if (modern) {
                        rawPath.trimEnd('/').removePrefix(root).trim('/')
                    } else {
                        val i = rawPath.indexOf("/$root/")
                        if (i < 0) continue
                        rawPath.substring(i + root.length + 2).substringBeforeLast('/', "")
                    }
                    val segments = dir.split('/').filter { it.isNotEmpty() }
                    // Same layout as the desktop: "music\<dir>\<file>", album = top-level subfolder.
                    val relative = (listOf("music") + segments + name).joinToString("\\")
                    val artist = c.getString(artistCol)?.takeIf { it.isNotBlank() && it != "<unknown>" } ?: ""
                    out += LocalFile(
                        uri = ContentUris.withAppendedId(collection, c.getLong(idCol)),
                        relativePath = relative,
                        album = segments.firstOrNull() ?: "",
                        title = name.substringBeforeLast('.'),
                        artist = artist,
                        durationMs = c.getLong(durCol),
                    )
                }
            }
        } catch (_: SecurityException) {
            // Permission not granted yet: the library is just empty.
        }
        return out
    }

    companion object {
        // Desktop compares paths with '\' -> '/' and case-insensitively; same here.
        fun normalize(path: String) = path.replace('\\', '/').lowercase()

        @Volatile
        private var instance: MusicLibrary? = null

        fun get(context: Context): MusicLibrary =
            instance ?: synchronized(this) {
                instance ?: MusicLibrary(context.applicationContext, AkashicDatabase.get(context)).also { instance = it }
            }
    }
}
