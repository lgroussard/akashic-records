package com.akashicrecords.sync

import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.media.MediaScannerConnection
import android.net.ConnectivityManager
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import java.io.File
import java.io.InputStream

/** A music file of this phone, named the way the catalogue names it ("music/Album/Titre.mp3"). */
class LocalMusic(val path: String, val size: Long, val modified: Long, val uri: Uri)

/**
 * The phone's music folder seen from the sync: on the phone the audio is not in the app's own folder but in
 * shared storage (Music/Akashic by default, read and written through MediaStore), under the same relative
 * layout as the PC's music folder. Same naming as music/MusicLibrary.kt, which the player uses.
 */
class MusicFiles(private val context: Context, folder: String) {
    private val root = folder.trim().trim('/')

    /** Music is big: it only moves on a network that is not metered (Wi-Fi), never on mobile data. */
    val allowedNow: Boolean
        get() = !(context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager).isActiveNetworkMetered

    fun local(): List<LocalMusic> {
        if (root.isEmpty()) return emptyList()
        val collection = MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
        val modern = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q

        @Suppress("DEPRECATION")
        val pathColumn = if (modern) MediaStore.Audio.Media.RELATIVE_PATH else MediaStore.Audio.Media.DATA
        val projection = arrayOf(
            MediaStore.Audio.Media._ID, MediaStore.Audio.Media.DISPLAY_NAME, MediaStore.Audio.Media.SIZE,
            MediaStore.Audio.Media.DATE_MODIFIED, pathColumn,
        )
        val out = ArrayList<LocalMusic>()
        try {
            context.contentResolver.query(collection, projection, "$pathColumn LIKE ?", arrayOf(if (modern) "$root/%" else "%/$root/%"), null)?.use { c ->
                while (c.moveToNext()) {
                    val name = c.getString(1) ?: continue
                    val rawPath = c.getString(4) ?: continue
                    val dir = if (modern) {
                        rawPath.trimEnd('/').removePrefix(root).trim('/')
                    } else {
                        val i = rawPath.indexOf("/$root/")
                        if (i < 0) continue
                        rawPath.substring(i + root.length + 2).substringBeforeLast('/', "")
                    }
                    val segments = dir.split('/').filter { it.isNotEmpty() }
                    out += LocalMusic(
                        path = (listOf("music") + segments + name).joinToString("/"),
                        size = c.getLong(2),
                        modified = c.getLong(3),
                        uri = ContentUris.withAppendedId(collection, c.getLong(0)),
                    )
                }
            }
        } catch (_: SecurityException) {
            // Permission not granted: no music of this phone takes part.
        }
        return out
    }

    fun open(file: LocalMusic): InputStream = context.contentResolver.openInputStream(file.uri) ?: throw java.io.IOException("fichier illisible : ${file.path}")

    /** Puts a fetched track ([temp], complete and verified) into the music folder, where the player finds it. */
    fun save(path: String, temp: File) {
        val parts = path.split('/').drop(1) // without "music"
        val name = parts.last()
        val relative = (listOf(root) + parts.dropLast(1)).joinToString("/")
        val mime = if (name.endsWith(".m4a", true)) "audio/mp4" else "audio/mpeg"
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val resolver = context.contentResolver
            val values = ContentValues().apply {
                put(MediaStore.Audio.Media.DISPLAY_NAME, name)
                put(MediaStore.Audio.Media.MIME_TYPE, mime)
                put(MediaStore.Audio.Media.RELATIVE_PATH, "$relative/")
                put(MediaStore.Audio.Media.IS_PENDING, 1)
            }
            val uri = resolver.insert(MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), values)
                ?: throw java.io.IOException("MediaStore a refusé le fichier.")
            try {
                resolver.openOutputStream(uri)!!.use { out -> temp.inputStream().use { it.copyTo(out) } }
                values.clear()
                values.put(MediaStore.Audio.Media.IS_PENDING, 0)
                resolver.update(uri, values, null, null)
            } catch (e: Exception) {
                resolver.delete(uri, null, null) // never leave half a track behind
                throw e
            }
        } else {
            @Suppress("DEPRECATION")
            val dir = File(Environment.getExternalStorageDirectory(), relative).apply { mkdirs() }
            val target = File(dir, name)
            temp.copyTo(target, overwrite = false)
            MediaScannerConnection.scanFile(context, arrayOf(target.path), arrayOf(mime), null)
        }
    }

    companion object {
        /** The catalogue's key for a path: the PC compares them without case, with either slash. */
        fun key(path: String) = path.replace('\\', '/').trimStart('/').lowercase()
    }
}
