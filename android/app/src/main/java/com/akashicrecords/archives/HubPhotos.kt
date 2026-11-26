package com.akashicrecords.archives

import android.content.Context
import android.os.SystemClock
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Photo
import com.akashicrecords.media.MediaFiles
import com.akashicrecords.sync.FileSync
import com.akashicrecords.sync.MusicFiles
import com.akashicrecords.sync.SyncService
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * The Archives' photos work like the music: the phone shows every photo of the hub while it answers, and only
 * stores the ones kept here ("Garder", or a kept album); offline, only those show.
 *
 * The gallery shows the hub's small previews (GET /media/<hash>/thumb, ~30 KB) and a photo opened full screen
 * is fetched whole. Both go to a cache folder that never grows past [CACHE_BYTES]: the least recently seen go
 * first, and Android may also empty it when space runs short. Nothing there is needed offline.
 */
object HubPhotos {
    private const val CACHE_BYTES = 200L * 1024 * 1024
    private var lastTrim = 0L

    /** What the gallery needs to know about each photo, read once per load. */
    class State(val hashes: Map<String, String>, val kept: Set<String>, val keptAlbums: Set<String>)

    fun key(photo: Photo): String = MusicFiles.key(photo.ImagePath)

    fun isHere(context: Context, photo: Photo) = MediaFiles.resolve(context, photo.ImagePath)?.isFile == true

    fun load(db: AkashicDatabase): State {
        val sql = db.openHelper.writableDatabase
        FileSync.ensureSchema(sql)
        val hashes = sql.query("SELECT Path, Hash FROM sync_file").use { c ->
            buildMap {
                while (c.moveToNext()) {
                    val path = FileSync.normalize(c.getString(0)) ?: continue
                    val hash = c.getString(1).lowercase()
                    if (hash.length == 64 && hash.all { it.isDigit() || it in 'a'..'f' }) put(MusicFiles.key(path), hash)
                }
            }
        }
        val kept = sql.query("SELECT path FROM sync_keep WHERE keep = 1").use { c -> buildSet { while (c.moveToNext()) add(c.getString(0)) } }
        return State(hashes, kept, FileSync.keptLists(sql, "photoalbum"))
    }

    private fun cacheDir(context: Context) = File(context.cacheDir, "photos").apply { mkdirs() }

    /** The hub's preview of a picture, from the cache or fetched now; null offline or when the hub has none. */
    fun thumb(context: Context, hash: String): File? = fetch(context, "$hash/thumb", File(cacheDir(context), "t-$hash.jpg"))

    /** The whole picture: the kept file when there is one, else the cache, else the hub. */
    fun original(context: Context, photo: Photo, hash: String?): File? {
        MediaFiles.resolve(context, photo.ImagePath)?.takeIf { it.isFile }?.let { return it }
        hash ?: return null
        return fetch(context, hash, File(cacheDir(context), "o-$hash"))
    }

    private fun fetch(context: Context, what: String, target: File): File? {
        if (target.isFile && target.length() > 0) {
            target.setLastModified(System.currentTimeMillis()) // recently seen: trimmed last
            return target
        }
        val (url, token) = SyncService.credentials(context) ?: return null
        val part = File(target.path + ".part")
        val conn = URL("$url/media/$what").openConnection() as HttpURLConnection
        try {
            conn.connectTimeout = 5_000
            conn.readTimeout = 30_000
            conn.setRequestProperty("Authorization", "Bearer $token")
            if (conn.responseCode != 200) return null
            conn.inputStream.use { input -> part.outputStream().use { input.copyTo(it) } }
            if (!part.renameTo(target)) return null
        } catch (e: Exception) {
            return null
        } finally {
            conn.disconnect()
            part.delete()
        }
        trim(context)
        return target
    }

    private fun trim(context: Context) {
        val now = SystemClock.elapsedRealtime()
        if (now - lastTrim < 30_000) return
        lastTrim = now
        val files = cacheDir(context).listFiles()?.filter { it.isFile }?.sortedByDescending { it.lastModified() } ?: return
        var total = 0L
        for (f in files) {
            total += f.length()
            if (total > CACHE_BYTES) f.delete()
        }
    }

    /**
     * Keeps a photo on this phone (copied from the cache at once when it was just seen, else fetched by the next
     * sync), or stops keeping it: its file is then removed from the phone, but only once the hub holds it.
     */
    fun setKept(context: Context, db: AkashicDatabase, photo: Photo, keep: Boolean, hash: String?) {
        val sql = db.openHelper.writableDatabase
        FileSync.setKeep(sql, photo.ImagePath, keep)
        val local = MediaFiles.resolve(context, photo.ImagePath) ?: return
        if (keep) {
            if (!local.isFile && hash != null) {
                val cached = File(cacheDir(context), "o-$hash")
                if (cached.isFile) {
                    local.parentFile?.mkdirs()
                    runCatching { cached.copyTo(local, overwrite = false) }
                }
            }
            if (!local.isFile) SyncService.runNow(context)
        } else if (hash != null && local.isFile) {
            local.delete()
        }
    }

    fun setAlbumKept(context: Context, db: AkashicDatabase, album: String, keep: Boolean) {
        FileSync.setKeepList(db.openHelper.writableDatabase, "photoalbum", album, keep)
        if (keep) SyncService.runNow(context)
    }

    /** The photos stored here that are neither kept nor in a kept album, and that the hub already holds. */
    fun releasable(context: Context, photos: List<Photo>, albumNames: Map<Int, String>, state: State): List<Photo> =
        photos.filter { p ->
            val k = key(p)
            k !in state.kept && albumNames[p.AlbumId] !in state.keptAlbums && state.hashes[k] != null && isHere(context, p)
        }

    /** Removes those files from the phone (they stay on the hub, and in the gallery while online). */
    fun release(context: Context, photos: List<Photo>): Long {
        var freed = 0L
        for (p in photos) {
            val f = MediaFiles.resolve(context, p.ImagePath) ?: continue
            val size = f.length()
            if (f.isFile && f.delete()) freed += size
        }
        return freed
    }
}
