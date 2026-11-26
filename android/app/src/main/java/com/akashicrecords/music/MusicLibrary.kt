package com.akashicrecords.music

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import kotlinx.coroutines.flow.collectLatest
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.MusicTrack
import com.akashicrecords.sync.FileSync
import com.akashicrecords.sync.SyncService
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
// only holds a user-chosen subset of the files; the other tracks of the catalogue are played
// from the hub (HubStream) when it holds them.
class MusicLibrary(private val context: Context, private val db: AkashicDatabase) {

    data class LocalFile(val uri: Uri, val relativePath: String, val album: String, val title: String, val artist: String, val durationMs: Long)

    // Normalized FilePath -> playable content Uri, rebuilt on each scan.
    @Volatile
    var available: Map<String, Uri> = emptyMap()
        private set

    // The catalogue as of the last scan: shown at once when the music section opens again, while a new scan runs.
    @Volatile
    var lastTracks: List<MusicTrack> = emptyList()
        private set

    // The library tab's lists as last worked out (see MusicLists), shown at once on the next visit.
    @Volatile
    var lastLists: MusicLists? = null

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
        remote = loadRemote()
        return dao.getAll().also { lastTracks = it }
    }

    // Catalogue key -> hash of the file on the hub, for the tracks the hub holds (empty when not paired).
    @Volatile
    private var remote: Map<String, String> = emptyMap()

    private fun loadRemote(): Map<String, String> {
        if (SyncService.credentials(context) == null) return emptyMap()
        return runCatching {
            val sql = db.openHelper.writableDatabase
            FileSync.ensureSchema(sql)
            sql.query("SELECT Path, Hash FROM sync_file").use { c ->
                buildMap { while (c.moveToNext()) FileSync.normalize(c.getString(0))?.takeIf(FileSync::isMusic)?.let { put(normalize(it), c.getString(1).lowercase()) } }
            }
        }.getOrDefault(emptyMap())
    }

    /** The hub's file for a catalogue key, or null when the hub does not hold it. */
    fun hashOf(key: String): String? {
        if (remote.isEmpty()) remote = loadRemote()
        return remote[key]?.takeIf { h -> h.length == 64 && h.all { it.isDigit() || it in 'a'..'f' } }
    }

    /** Whether the track's file is on this phone (playable without network). */
    fun isLocal(track: MusicTrack) = normalize(track.FilePath) in available

    /** What the player opens for a track: its file on this phone, else the hub's copy (streamed), else null. */
    fun uriFor(track: MusicTrack): Uri? {
        val key = normalize(track.FilePath)
        return available[key] ?: if (key in remote && hubReachable) HubStream.uri(key) else null
    }

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
        /** Whether the phone has a network right now (without one, the server's tracks are not offered). */
        fun online(context: Context): Boolean {
            val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as android.net.ConnectivityManager
            return cm.getNetworkCapabilities(cm.activeNetwork)?.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET) == true
        }

        /** Last answer of the hub (see [probe]). Android's own "connected" is not enough: Tailscale's VPN stays up
         * with mobile data and Wi-Fi off, so the phone looks online while nothing gets through. Read for every track of a
         * list: a plain field, never a call to the system (3000 of those took seconds). */
        @Volatile
        var hubReachable = true
            private set

        /** Asks the hub whether it answers (3 s at most); remembers and returns the result. */
        suspend fun probe(context: Context): Boolean = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            val creds = SyncService.credentials(context)
            val ok = online(context) && creds != null && runCatching {
                val conn = java.net.URL(creds.first + "/health").openConnection() as java.net.HttpURLConnection
                conn.connectTimeout = 3000
                conn.readTimeout = 3000
                try { conn.responseCode == 200 } finally { conn.disconnect() }
            }.getOrDefault(false)
            hubReachable = ok
            ok
        }

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

/** True while the hub answers: checked at once, at every network change and every 15 s, so the library hides the
 * server's tracks when they cannot be played. */
@androidx.compose.runtime.Composable
fun rememberOnline(): Boolean {
    val context = androidx.compose.ui.platform.LocalContext.current.applicationContext
    var online by androidx.compose.runtime.remember { androidx.compose.runtime.mutableStateOf(MusicLibrary.hubReachable) }
    val changes = androidx.compose.runtime.remember { kotlinx.coroutines.flow.MutableStateFlow(0) }
    androidx.compose.runtime.DisposableEffect(Unit) {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as android.net.ConnectivityManager
        val callback = object : android.net.ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: android.net.Network) { changes.value++ }
            override fun onLost(network: android.net.Network) { changes.value++ }
        }
        runCatching { cm.registerDefaultNetworkCallback(callback) }
        onDispose { runCatching { cm.unregisterNetworkCallback(callback) } }
    }
    androidx.compose.runtime.LaunchedEffect(Unit) {
        changes.collectLatest {
            while (true) {
                online = MusicLibrary.probe(context)
                kotlinx.coroutines.delay(15_000)
            }
        }
    }
    return online
}
