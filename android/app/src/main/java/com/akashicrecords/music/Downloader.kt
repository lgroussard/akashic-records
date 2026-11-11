package com.akashicrecords.music

import android.content.ContentValues
import android.content.Context
import android.media.MediaScannerConnection
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.DownloadFolder
import com.akashicrecords.data.DownloadItem
import com.yausername.ffmpeg.FFmpeg
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLRequest
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

// Mobile counterpart of the desktop Téléchargements board + yt-dlp runner.
//
// Same queue table (DownloadItem / DownloadFolder) and same rules as the desktop:
//   * Url is a link, or a "ytsearch1:<title> <artist>" query (what the desktop ⤓ queues);
//   * a zone (DownloadFolder.Subfolder) is the album folder the item downloads into, empty = root;
//   * status ordinals match the desktop DownloadStatus enum: Queued=0, Done=1, Failed=2.
// The difference: yt-dlp (+ Python + ffmpeg) is bundled in the APK (youtubedl-android) instead of
// being installed by the user. What gets downloaded, and respecting each source's terms of use,
// stays the user's responsibility, as on the desktop.
//
// Downloads land in the app cache, then are published into <music folder>/<zone>/ through
// MediaStore, so the library scan picks them up like files copied by hand.
object Downloader {
    const val QUEUED = 0
    const val DONE = 1
    const val FAILED = 2

    data class State(
        val running: Boolean = false,
        val current: String? = null,
        val progress: Float = 0f,
        val message: String? = null,
        val toolVersion: String? = null,
    )

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val initLock = Mutex()
    private var ready = false

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state

    // Bumped after each run that added files, so an open library rescans itself.
    private val _libraryVersion = MutableStateFlow(0)
    val libraryVersion: StateFlow<Int> = _libraryVersion

    private fun set(f: State.() -> State) {
        _state.value = _state.value.f()
    }

    // First call unpacks Python + yt-dlp (a few seconds, once per install).
    private suspend fun ensureInit(context: Context) = initLock.withLock {
        if (ready) return@withLock
        YoutubeDL.getInstance().init(context.applicationContext)
        FFmpeg.getInstance().init(context.applicationContext)
        ready = true
        set { copy(toolVersion = YoutubeDL.getInstance().version(context.applicationContext)) }
    }

    // Called when the search opens: Python + yt-dlp unpacked (first time) and the daily update done now,
    // not in the tap → sound path.
    suspend fun warmUp(context: Context) = kotlinx.coroutines.withContext(Dispatchers.IO) {
        try {
            ensureInit(context)
            autoUpdate(context)
        } catch (_: Exception) {
        }
    }

    // Stops the yt-dlp run fetching [title] (another song was tapped meanwhile).
    fun cancelStream(title: String) {
        repeat(2) { attempt -> runCatching { YoutubeDL.getInstance().destroyProcessById(streamId(attempt, title)) } }
    }

    private fun streamId(attempt: Int, title: String) = "stream-$attempt-${title.hashCode()}"

    fun searchQuery(title: String, artist: String) = "ytsearch1:${"$title $artist".replace("\"", "").trim()}"

    // Desktop FetchAudioAsync: the full track as a temp m4a, to play a suggestion entirely (the
    // iTunes clip is only 30 s). First of 3 YouTube hits under 15 min, so album compilations are
    // skipped; two attempts. Null when nothing came down (the caller falls back to the clip).
    suspend fun fetchStream(context: Context, title: String, artist: String): File? = kotlinx.coroutines.withContext(Dispatchers.IO) {
        try {
            ensureInit(context)
            autoUpdate(context)
        } catch (_: Exception) {
            return@withContext null
        }
        val dir = streamDir(context).apply { mkdirs() }
        val query = "ytsearch3:${"$title $artist".replace("\"", "").trim()}"
        repeat(2) { attempt ->
            if (!isActive) return@withContext null // cancelled: another song was tapped
            val started = System.currentTimeMillis() - 1000
            val request = YoutubeDLRequest(query).apply {
                addOption("-f", "140/bestaudio[ext=m4a]/18")
                addOption("--match-filter", "duration<900")
                addOption("--max-downloads", 1)
                addOption("--no-mtime")
                addOption("-o", "${dir.absolutePath}/%(id)s.%(ext)s")
            }
            // Exit code 101 is the normal "--max-downloads reached" stop: judge by the file.
            try {
                YoutubeDL.getInstance().execute(request, streamId(attempt, title))
            } catch (_: Exception) {
            }
            dir.listFiles()
                ?.filter { it.isFile && it.lastModified() >= started && !it.name.endsWith(".part") }
                ?.maxByOrNull { it.lastModified() }
                ?.let { return@withContext it }
        }
        null
    }

    fun streamDir(context: Context) = File(context.cacheDir, "stream")

    // Queues a link or search once (desktop QueueDownload dedups by Url). Returns false if already there.
    suspend fun enqueue(db: AkashicDatabase, url: String, title: String, album: String?): Boolean {
        val dao = db.downloadItemDao()
        if (dao.countByUrl(url) > 0) return false
        dao.add(
            DownloadItem(
                Url = url,
                Title = title,
                Status = QUEUED,
                AddedAt = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSSSSSS")),
                FolderId = zoneFor(db, album),
            ),
        )
        return true
    }

    // Desktop ZoneForRun: the zone pointing at an album folder, created on first use.
    private suspend fun zoneFor(db: AkashicDatabase, album: String?): Int? {
        val name = sanitize(album)
        if (name.isEmpty()) return null
        val zones = db.downloadFolderDao().getAll()
        zones.firstOrNull { it.Subfolder?.trim().equals(name, ignoreCase = true) }?.let { return it.Id }
        return db.downloadFolderDao().add(DownloadFolder(Name = name, Subfolder = name, SortOrder = zones.size)).toInt()
    }

    // Desktop SanitizeSubfolderName: one safe path segment, empty = music root.
    fun sanitize(raw: String?): String {
        var name = (raw ?: "").trim().trimEnd('/', '\\')
        if (name.isEmpty()) return ""
        name = name.split('/', '\\').last()
        name = name.map { if (it in "<>:\"/\\|?*" || it.code < 32) '_' else it }.joinToString("")
        return if (name == "." || name == "..") "" else name
    }

    fun updateTool(context: Context) {
        if (_state.value.running) return
        scope.launch {
            set { copy(running = true, current = null, message = "Mise à jour de yt-dlp…") }
            val msg = try {
                ensureInit(context)
                val status = YoutubeDL.getInstance().updateYoutubeDL(context.applicationContext, YoutubeDL.UpdateChannel.STABLE)
                val v = YoutubeDL.getInstance().version(context.applicationContext)
                set { copy(toolVersion = v) }
                context.getSharedPreferences("akashic_downloader", Context.MODE_PRIVATE).edit().putLong("ytdlpLastUpdate", System.currentTimeMillis()).apply()
                if (status == YoutubeDL.UpdateStatus.ALREADY_UP_TO_DATE) "yt-dlp est déjà à jour ($v)." else "yt-dlp mis à jour ($v)."
            } catch (e: Exception) {
                "Échec de la mise à jour : ${e.message?.lineSequence()?.firstOrNull()}"
            }
            set { copy(running = false, message = msg) }
        }
    }

    // Processes every queued item in order (desktop "Télécharger maintenant"), then rescans.
    fun runQueue(context: Context, db: AkashicDatabase, musicFolder: String, onLibraryChanged: () -> Unit = {}) {
        if (_state.value.running) return
        scope.launch {
            set { copy(running = true, message = "Préparation de yt-dlp…", progress = 0f) }
            var done = 0
            var failed = 0
            var lastError: String? = null
            try {
                ensureInit(context)
                autoUpdate(context)
                val zones = db.downloadFolderDao().getAll().associateBy { it.Id }
                while (true) {
                    val next = db.downloadItemDao().getAll().firstOrNull { it.Status == QUEUED } ?: break
                    set { copy(current = next.Title.ifBlank { next.Url }, progress = 0f, message = null) }
                    val sub = sanitize(next.FolderId?.let { zones[it]?.Subfolder })
                    val ok = try {
                        downloadOne(context, next, musicFolder, sub)
                    } catch (e: Exception) {
                        lastError = e.message?.lineSequence()?.lastOrNull { it.isNotBlank() } ?: e.javaClass.simpleName
                        false
                    }
                    db.downloadItemDao().update(next.copy(Status = if (ok) DONE else FAILED))
                    if (ok) done++ else failed++
                }
                if (done > 0) {
                    MusicLibrary.get(context).scanAndSync(musicFolder)
                    _libraryVersion.value++
                    onLibraryChanged()
                }
                val msg = when {
                    done == 0 && failed == 0 -> "Rien à télécharger."
                    failed == 0 -> "$done téléchargement(s) terminé(s)."
                    else -> "$done terminé(s), $failed en échec" + (lastError?.let { " : $it" } ?: ".")
                }
                set { copy(running = false, current = null, message = msg) }
            } catch (e: Exception) {
                set { copy(running = false, current = null, message = "yt-dlp n'a pas pu démarrer : ${e.message}") }
            }
        }
    }

    // The yt-dlp bundled in the APK ages fast and YouTube then refuses it ("Please sign in"):
    // refresh it before downloading, at most once a day. A failed update just keeps the old one.
    private fun autoUpdate(context: Context) {
        val prefs = context.getSharedPreferences("akashic_downloader", Context.MODE_PRIVATE)
        val last = prefs.getLong("ytdlpLastUpdate", 0L)
        if (System.currentTimeMillis() - last < 24 * 60 * 60 * 1000L) return
        set { copy(message = "Mise à jour de yt-dlp…") }
        try {
            YoutubeDL.getInstance().updateYoutubeDL(context.applicationContext, YoutubeDL.UpdateChannel.STABLE)
            prefs.edit().putLong("ytdlpLastUpdate", System.currentTimeMillis()).apply()
            set { copy(toolVersion = YoutubeDL.getInstance().version(context.applicationContext)) }
        } catch (_: Exception) {
            // Offline or GitHub unreachable: try with the current version.
        }
    }

    private fun downloadOne(context: Context, item: DownloadItem, musicFolder: String, sub: String): Boolean {
        val work = File(context.cacheDir, "downloads/${item.Id}").apply { deleteRecursively(); mkdirs() }
        try {
            // Desktop default arguments ("-x --audio-format mp3 --embed-thumbnail --add-metadata"),
            // with the yt-dlp default name "Title [id].mp3" so the desktop title cleaning applies.
            val request = YoutubeDLRequest(item.Url).apply {
                addOption("-x")
                addOption("--audio-format", "mp3")
                addOption("--embed-thumbnail")
                addOption("--add-metadata")
                addOption("--no-playlist")
                addOption("--no-mtime")
                addOption("-o", "${work.absolutePath}/%(title)s [%(id)s].%(ext)s")
            }
            // A failing post-step (e.g. the thumbnail) makes yt-dlp exit non-zero although the mp3
            // is there: judge by the file, rethrow only when nothing came down.
            val error = try {
                YoutubeDL.getInstance().execute(request, "dl-${item.Id}") { progress, _, _ ->
                    set { copy(progress = (progress / 100f).coerceIn(0f, 1f)) }
                }
                null
            } catch (e: Exception) {
                e
            }
            val files = work.listFiles()?.filter { it.isFile && it.extension.equals("mp3", true) }.orEmpty()
            if (files.isEmpty()) throw error ?: IllegalStateException("Aucun fichier audio produit.")
            files.forEach { publish(context, it, musicFolder, sub) }
            return true
        } finally {
            work.deleteRecursively() // app cache only
        }
    }

    // Copies a finished file into shared storage: <musicFolder>/<sub>/<name>.
    private fun publish(context: Context, file: File, musicFolder: String, sub: String) {
        val relative = listOf(musicFolder.trim('/'), sub).filter { it.isNotEmpty() }.joinToString("/")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val resolver = context.contentResolver
            val values = ContentValues().apply {
                put(MediaStore.Audio.Media.DISPLAY_NAME, file.name)
                put(MediaStore.Audio.Media.MIME_TYPE, "audio/mpeg")
                put(MediaStore.Audio.Media.RELATIVE_PATH, "$relative/")
                put(MediaStore.Audio.Media.IS_PENDING, 1)
            }
            val collection = MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            val uri = resolver.insert(collection, values) ?: error("MediaStore a refusé le fichier.")
            resolver.openOutputStream(uri)!!.use { out -> file.inputStream().use { it.copyTo(out) } }
            values.clear()
            values.put(MediaStore.Audio.Media.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
        } else {
            @Suppress("DEPRECATION")
            val dir = File(Environment.getExternalStorageDirectory(), relative).apply { mkdirs() }
            val target = File(dir, file.name)
            file.copyTo(target, overwrite = false)
            MediaScannerConnection.scanFile(context, arrayOf(target.path), arrayOf("audio/mpeg"), null)
        }
    }
}
