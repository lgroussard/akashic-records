package com.akashicrecords.sync

import android.content.ContentValues
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest

// The hub's shared computing, seen from the phone: the phone sends a photo and asks for it to be upscaled; the
// PC (when Akashic runs there) or the hub itself does the work; the result stays on the hub and comes to the
// phone only when it is opened. The phone never runs a model: it would be slow, hot and hard on the battery.
object HubCompute {
    class Model(val name: String, val label: String, val description: String, val hub: Boolean, val runners: List<String>)

    class Job(
        val id: String, val model: String, val inputHash: String, val inputName: String, val status: String,
        val addedAt: String, val workerName: String?, val seconds: Double?, val resultHash: String?,
        val resultSize: Long?, val resultName: String?, val error: String?,
        val attachType: String? = null, val attachedPath: String? = null, val attachError: String? = null,
    ) {
        val waiting get() = status == "queued"
        val running get() = status == "running"
        val done get() = status == "done"
        val failed get() = status == "failed"
    }

    class Board(val enabled: Boolean, val models: List<Model>, val workers: List<String>, val jobs: List<Job>) {
        val busy get() = jobs.any { it.waiting || it.running }
        fun model(name: String?) = models.firstOrNull { it.name == name }
    }

    private val _board = MutableStateFlow<Board?>(null)
    val board: StateFlow<Board?> = _board
    private val _error = MutableStateFlow<String?>(null)
    val error: StateFlow<String?> = _error

    /** The hub works under the name "hub": shown as the server. */
    fun who(name: String?) = if (name == null || name == "hub") "le serveur" else name

    private fun hub(context: Context): HubClient? = SyncService.credentials(context)?.let { (url, token) -> HubClient(url, token) }

    private fun parse(o: JSONObject): Board {
        fun JSONObject.str(k: String) = if (isNull(k)) null else optString(k)
        val models = o.optJSONArray("models")
        val workers = o.optJSONArray("workers")
        val jobs = o.optJSONArray("jobs")
        return Board(
            enabled = o.optBoolean("enabled", true),
            models = (0 until (models?.length() ?: 0)).map { models!!.getJSONObject(it) }.map { m ->
                val r = m.optJSONArray("runners")
                Model(m.getString("name"), m.optString("label"), m.optString("description"), m.optBoolean("hub"),
                    (0 until (r?.length() ?: 0)).map { r!!.getString(it) })
            },
            workers = (0 until (workers?.length() ?: 0)).map { workers!!.getJSONObject(it).optString("name") },
            jobs = (0 until (jobs?.length() ?: 0)).map { jobs!!.getJSONObject(it) }.map {
                Job(it.getString("id"), it.getString("model"), it.getString("input_hash"), it.getString("input_name"),
                    it.getString("status"), it.optString("added_at"), it.str("worker_name"),
                    if (it.isNull("seconds")) null else it.optDouble("seconds"), it.str("result_hash"),
                    if (it.isNull("result_size")) null else it.optLong("result_size"), it.str("result_name"), it.str("error"),
                    it.str("attach_type"), it.str("attached_path"), it.str("attach_error"))
            },
        )
    }

    private suspend fun act(context: Context, action: (HubClient) -> Unit): Boolean = withContext(Dispatchers.IO) {
        val hub = hub(context) ?: return@withContext false
        try {
            action(hub)
            _board.value = parse(hub.compute())
            _error.value = null
            true
        } catch (e: Exception) {
            _error.value = e.message ?: e.javaClass.simpleName
            false
        }
    }

    suspend fun refresh(context: Context) = act(context) { }
    suspend fun retry(context: Context, id: String) = act(context) { it.retryCompute(id) }
    suspend fun remove(context: Context, id: String) = act(context) { it.removeCompute(id) }

    private fun dir(context: Context) = File(context.cacheDir, "compute").apply { mkdirs() }

    /**
     * Sends a photo picked in the gallery and asks for it to be upscaled. The upscaler reads jpg, png and webp:
     * anything else (heic...) is turned into a jpg first. The original is kept in the cache: "before" needs no download.
     */
    suspend fun request(context: Context, uri: Uri, model: String) = act(context) { hub ->
        val resolver = context.contentResolver
        var name = resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0) else null
        } ?: "photo.jpg"
        val ext = name.substringAfterLast('.', "").lowercase()
        val temp = File(dir(context), "upload-" + System.nanoTime())
        try {
            if (ext in setOf("jpg", "jpeg", "png", "webp")) {
                resolver.openInputStream(uri)!!.use { input -> temp.outputStream().use { input.copyTo(it) } }
            } else {
                val bitmap = resolver.openInputStream(uri)!!.use { BitmapFactory.decodeStream(it) } ?: throw HubException("Format d'image non lu.")
                temp.outputStream().use { bitmap.compress(Bitmap.CompressFormat.JPEG, 95, it) }
                name = name.substringBeforeLast('.') + ".jpg"
            }
            val hash = sha256(temp)
            if (!hub.hasFile(hash)) hub.uploadFile(temp, hash)
            val keep = File(dir(context), hash + "." + name.substringAfterLast('.').lowercase())
            if (!keep.exists()) temp.copyTo(keep)
            hub.addComputeJob(model, hash, name)
        } finally {
            temp.delete()
        }
    }

    /** The kinds of rows whose picture can be upscaled in place (same names as the tables and the hub's). */
    val inPlaceKinds = setOf("Photo", "JournalPhoto", "RefImage", "CanvasElement")

    /**
     * Upscales a picture already in the app (a row of Photo, JournalPhoto, RefImage or CanvasElement): the PC or the
     * hub does the work and the hub adds the result next to the original (same album, entry, board or canvas); it
     * reaches this phone at a sync. A row not yet on the hub is synchronised first. Returns null when it went,
     * else a message for the user.
     */
    suspend fun requestFrom(context: Context, table: String, localId: Long): String? = withContext(Dispatchers.IO) {
        if (table !in inPlaceKinds) return@withContext "Ce type d'image ne peut pas être agrandi."
        val hub = hub(context) ?: return@withContext "Ce téléphone n'est pas appairé au serveur (Réglages → Synchronisation)."
        var syncId = syncIdOf(context, table, localId)
        if (syncId == null) {
            SyncService.run(context) // just added here: it goes to the hub first, with its file
            syncId = syncIdOf(context, table, localId) ?: return@withContext "Cette image n'a pas encore pu être envoyée au serveur : réessaie dans un moment."
        }
        try {
            hub.addComputeJobFrom(table, syncId)
            refresh(context)
            null
        } catch (e: Exception) {
            e.message ?: e.javaClass.simpleName
        }
    }

    /** [requestFrom] as the line to show the user, for the « Agrandir ×4 » buttons of the viewers. */
    suspend fun upscaleNote(context: Context, table: String, localId: Long): String =
        requestFrom(context, table, localId) ?: "Envoyée : la version ×4 arrivera à côté de l'original."

    /** The id a row has on the hub (sync_map), or null when it has not been synchronised yet. */
    private fun syncIdOf(context: Context, table: String, localId: Long): String? = try {
        com.akashicrecords.data.AkashicDatabase.get(context.applicationContext).openHelper.readableDatabase
            .query("SELECT sync_id FROM sync_map WHERE entity_type = ? AND local_id = ? AND deleted = 0 LIMIT 1", arrayOf<Any>(table, localId))
            .use { c -> if (c.moveToFirst()) c.getString(0) else null }
    } catch (e: android.database.sqlite.SQLiteException) {
        null // never synchronised: no sync_map yet
    }

    /** A picture kept on the hub (an original or a result), as a file here: fetched once, then kept in the cache. */
    suspend fun local(context: Context, hash: String, ext: String): File = withContext(Dispatchers.IO) {
        val target = File(dir(context), "$hash.$ext")
        if (target.exists()) return@withContext target
        val hub = hub(context) ?: throw HubException("Ce téléphone n'est pas appairé.")
        if (!hub.downloadFile(hash, target)) throw HubException("Cette image n'est plus sur le serveur.")
        target
    }

    /** A copy in the phone's gallery (Pictures/Akashic). Android 10 and later; older phones get a "save as". */
    fun saveToGallery(context: Context, file: File, name: String) {
        check(Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) { "Android 10 ou plus récent requis" }
        val resolver = context.contentResolver
        val values = ContentValues().apply {
            put(MediaStore.Images.Media.DISPLAY_NAME, name)
            put(MediaStore.Images.Media.MIME_TYPE, "image/png")
            put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/Akashic")
            put(MediaStore.Images.Media.IS_PENDING, 1)
        }
        val uri = requireNotNull(resolver.insert(MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), values)) { "galerie indisponible" }
        try {
            resolver.openOutputStream(uri)!!.use { out -> file.inputStream().use { it.copyTo(out) } }
            resolver.update(uri, ContentValues().apply { put(MediaStore.Images.Media.IS_PENDING, 0) }, null, null)
        } catch (e: Exception) {
            resolver.delete(uri, null, null) // no half-written file left in the gallery
            throw e
        }
    }

    private fun sha256(file: File): String {
        val sha = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(81920)
            while (true) {
                val n = input.read(buffer)
                if (n < 0) break
                sha.update(buffer, 0, n)
            }
        }
        return sha.digest().joinToString("") { "%02x".format(it) }
    }
}
