package com.akashicrecords.music

import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.MusicTrack
import com.akashicrecords.sync.FileSync
import com.akashicrecords.sync.MusicFiles
import com.akashicrecords.sync.SyncService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** One track of the shared catalogue, seen from this phone. */
class CatalogTrack(
    val track: MusicTrack,
    val key: String,
    val uri: Uri?,          // its file on this phone, when it is here
    val onServer: Boolean,  // the hub holds its file: it can be fetched
    val wanted: Boolean,    // chosen for this phone, not here yet
) {
    val onPhone get() = uri != null
}

class CatalogMood(val id: Int, val name: String, val trackIds: Set<Int>)

/** The music catalogue (every track known to the hub) and this phone's choice for each one. */
object MusicCatalog {
    fun load(context: Context, db: AkashicDatabase, folder: String): List<CatalogTrack> {
        val sql = db.openHelper.writableDatabase
        FileSync.ensureSchema(sql)
        val local = MusicFiles(context, folder).local().associate { MusicFiles.key(it.path) to it.uri }
        // Keys are folded in Kotlin (SQLite's lower() leaves accented capitals alone).
        val listed = sql.query("SELECT Path FROM sync_file").use { c -> buildSet { while (c.moveToNext()) add(MusicFiles.key(c.getString(0))) } }
        val keep = sql.query("SELECT path FROM sync_keep WHERE keep = 1").use { c -> buildSet { while (c.moveToNext()) add(c.getString(0)) } }
        return db.openHelper.readableDatabase.let {
            sql.query("SELECT Id, FilePath, Title, Artist, DurationSeconds, CoverImagePath, Album, AddedAt FROM MusicTrack").use { c ->
                buildList {
                    while (c.moveToNext()) {
                        val t = MusicTrack(
                            Id = c.getInt(0), FilePath = c.getString(1), Title = c.getString(2), Artist = c.getString(3),
                            DurationSeconds = if (c.isNull(4)) null else c.getDouble(4), CoverImagePath = if (c.isNull(5)) null else c.getString(5),
                            Album = c.getString(6), AddedAt = c.getString(7),
                        )
                        val key = MusicFiles.key(t.FilePath)
                        val uri = local[key]
                        add(CatalogTrack(t, key, uri, key in listed, key in keep && uri == null))
                    }
                }
            }
        }
    }

    fun setKeep(db: AkashicDatabase, keys: List<String>, keep: Boolean) {
        val sql = db.openHelper.writableDatabase
        keys.forEach { FileSync.setKeep(sql, it, keep) }
    }

    /** The ambiances, each with the ids of its tracks. */
    fun moods(db: AkashicDatabase): List<CatalogMood> {
        val sql = db.openHelper.writableDatabase
        val links = sql.query("SELECT MoodId, TrackId FROM TrackMood").use { c ->
            buildMap<Int, MutableSet<Int>> { while (c.moveToNext()) getOrPut(c.getInt(0)) { mutableSetOf() }.add(c.getInt(1)) }
        }
        return sql.query("SELECT Id, Name FROM Mood ORDER BY Name").use { c ->
            buildList { while (c.moveToNext()) add(CatalogMood(c.getInt(0), c.getString(1), links[c.getInt(0)] ?: emptySet())) }
        }
    }

    /** Whole lists kept on this phone: albums by name, ambiances by id (see FileSync.applyKeptLists). */
    fun keptAlbums(db: AkashicDatabase) = FileSync.keptLists(db.openHelper.writableDatabase, "album")
    fun keptMoods(db: AkashicDatabase) = FileSync.keptLists(db.openHelper.writableDatabase, "mood").mapNotNull { it.toIntOrNull() }.toSet()
    fun setKeepAlbum(db: AkashicDatabase, album: String, keep: Boolean) = FileSync.setKeepList(db.openHelper.writableDatabase, "album", album, keep)
    fun setKeepMood(db: AkashicDatabase, id: Int, keep: Boolean) = FileSync.setKeepList(db.openHelper.writableDatabase, "mood", id.toString(), keep)
}

// The "Hors ligne" tab of the music section: every track of the catalogue, and for each one whether its file lives
// on this phone. Any track the hub holds can be played without being here (HubStream); "Garder" fetches it (on
// Wi-Fi) so it also plays without network, "Retirer" takes it off this phone only. A whole album or ambiance
// can be kept: the choice is remembered and covers the titles added to it later.
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ServerCatalogPanel(db: AkashicDatabase, folder: String) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var items by remember { mutableStateOf<List<CatalogTrack>>(emptyList()) }
    var search by remember { mutableStateOf("") }
    var here by remember { mutableStateOf(false) }
    var open by remember { mutableStateOf<Set<String>>(emptySet()) }
    val sync by SyncService.state.collectAsState()
    val synced by SyncService.dataVersion.collectAsState()
    val library by Downloader.libraryVersion.collectAsState()

    var moods by remember { mutableStateOf<List<CatalogMood>>(emptyList()) }
    var keptAlbums by remember { mutableStateOf<Set<String>>(emptySet()) }
    var keptMoods by remember { mutableStateOf<Set<Int>>(emptySet()) }

    suspend fun reload() {
        class Loaded(val items: List<CatalogTrack>, val moods: List<CatalogMood>, val albums: Set<String>, val moodIds: Set<Int>)
        val l = withContext(Dispatchers.IO) {
            runCatching { Loaded(MusicCatalog.load(context, db, folder), MusicCatalog.moods(db), MusicCatalog.keptAlbums(db), MusicCatalog.keptMoods(db)) }.getOrNull()
        } ?: return
        items = l.items; moods = l.moods; keptAlbums = l.albums; keptMoods = l.moodIds
    }
    LaunchedEffect(synced, library, sync.busy, folder) { reload() }

    // Removing a file that the app did not create itself needs the system's own confirmation.
    val confirmDelete = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) {
        Downloader.notifyLibraryChanged()
    }

    fun keep(list: List<CatalogTrack>) = scope.launch {
        withContext(Dispatchers.IO) { MusicCatalog.setKeep(db, list.map { it.key }, true) }
        reload()
        // Straight away, without waiting for the background scheduler; what does not fit in one run is
        // carried on by it (SyncService re-queues itself while tracks are left to fetch).
        SyncService.run(context)
        SyncService.requestSoon(context)
        reload()
    }

    fun forget(list: List<CatalogTrack>) = scope.launch {
        withContext(Dispatchers.IO) { MusicCatalog.setKeep(db, list.map { it.key }, false) }
        val uris = list.mapNotNull { it.uri }
        if (uris.isNotEmpty()) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                confirmDelete.launch(IntentSenderRequest.Builder(MediaStore.createDeleteRequest(context.contentResolver, uris).intentSender).build())
            } else {
                withContext(Dispatchers.IO) { uris.forEach { runCatching { context.contentResolver.delete(it, null, null) } } }
                Downloader.notifyLibraryChanged()
            }
        }
        reload()
    }

    // A whole list: remembered, so the titles added to it later arrive by themselves.
    fun keepAlbum(album: String, tracks: List<CatalogTrack>) = scope.launch {
        withContext(Dispatchers.IO) { MusicCatalog.setKeepAlbum(db, album, true) }
        keep(tracks.filter { !it.onPhone })
    }

    fun keepMood(m: CatalogMood) = scope.launch {
        withContext(Dispatchers.IO) { MusicCatalog.setKeepMood(db, m.id, true) }
        keep(items.filter { it.track.Id in m.trackIds && !it.onPhone })
    }

    // No longer kept: its tracks leave this phone, except those another kept list still holds.
    fun unkeep(album: String?, mood: CatalogMood?) = scope.launch {
        withContext(Dispatchers.IO) {
            album?.let { MusicCatalog.setKeepAlbum(db, it, false) }
            mood?.let { MusicCatalog.setKeepMood(db, it.id, false) }
        }
        val albumsLeft = keptAlbums - setOfNotNull(album)
        val moodIdsLeft = moods.filter { it.id in keptMoods && it.id != mood?.id }.flatMap { it.trackIds }.toSet()
        forget(items.filter { (album != null && it.track.Album == album) || (mood != null && it.track.Id in mood.trackIds) }
            .filter { it.track.Album !in albumsLeft && it.track.Id !in moodIdsLeft })
    }

    val shown = items
        .filter { it.onPhone == here }
        .filter { search.isBlank() || it.track.Title.contains(search, true) || it.track.Artist.contains(search, true) || it.track.Album.contains(search, true) }
    val albums = shown.groupBy { it.track.Album.ifBlank { "Sans album" } }.toSortedMap(String.CASE_INSENSITIVE_ORDER)
    val waiting = items.count { it.wanted }
    val wifi = remember(sync.busy) { MusicFiles(context, folder).allowedNow }

    Column(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        Text(
            when {
                !sync.paired -> "Ce téléphone n'est pas relié au serveur (Plus → Réglages → Synchronisation)."
                waiting > 0 && !wifi -> "$waiting titre(s) à récupérer : en attente d'une connexion."
                waiting > 0 -> "$waiting titre(s) en cours de récupération…"
                else -> "${items.count { it.onPhone }} titre(s) gardé(s) sur ce téléphone, ${items.size} au catalogue. Les autres se lisent depuis le serveur."
            },
            fontSize = 12.sp,
            color = Palette.TextMuted,
        )
        OutlinedTextField(search, { search = it }, placeholder = { Text("Titre, artiste, album") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        Row(verticalAlignment = Alignment.CenterVertically) {
            FilterChip(selected = !here, onClick = { here = false }, label = { Text("Pas sur ce téléphone") })
            Spacer(Modifier.width(8.dp))
            FilterChip(selected = here, onClick = { here = true }, label = { Text("Sur ce téléphone") })
        }
        if (moods.isNotEmpty()) {
            Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()), verticalAlignment = Alignment.CenterVertically) {
                Text("Ambiances gardées :", fontSize = 11.sp, color = Palette.TextMuted)
                moods.forEach { m ->
                    Spacer(Modifier.width(6.dp))
                    val kept = m.id in keptMoods
                    FilterChip(selected = kept, onClick = { if (kept) unkeep(null, m) else keepMood(m) }, label = { Text("${m.name} (${m.trackIds.size})", maxLines = 1) })
                }
            }
        }

        LazyColumn(Modifier.fillMaxSize()) {
            albums.forEach { (album, tracks) ->
                item(key = "album:$album") {
                    val raw = tracks.first().track.Album
                    val kept = raw in keptAlbums
                    Row(Modifier.fillMaxWidth().clickable { open = if (album in open) open - album else open + album }, verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            (if (album in open) "▾ " else "▸ ") + (if (kept) "★ " else "") + album + "  (${tracks.size})",
                            fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f),
                        )
                        when {
                            kept -> TextButton(onClick = { unkeep(raw, null) }) { Text("Ne plus garder") }
                            here -> Row {
                                TextButton(onClick = { keepAlbum(raw, tracks) }) { Text("Garder l'album") }
                                TextButton(onClick = { forget(tracks) }) { Text("Retirer") }
                            }
                            else -> TextButton(onClick = { keepAlbum(raw, tracks) }) { Text("Garder l'album") }
                        }
                    }
                }
                if (album in open) {
                    tracks.sortedBy { it.track.Title.lowercase() }.forEach { t ->
                        item(key = "track:${t.track.Id}") {
                            Row(Modifier.fillMaxWidth().padding(start = 14.dp), verticalAlignment = Alignment.CenterVertically) {
                                Column(Modifier.weight(1f)) {
                                    Text(t.track.Title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    if (t.track.Artist.isNotBlank()) Text(t.track.Artist, fontSize = 11.sp, color = Palette.TextMuted, maxLines = 1)
                                }
                                when {
                                    t.onPhone -> TextButton(onClick = { forget(listOf(t)) }) { Text("Retirer") }
                                    t.wanted -> TextButton(onClick = { forget(listOf(t)) }) { Text("En attente ✕") }
                                    t.onServer -> TextButton(onClick = { keep(listOf(t)) }) { Text("Garder") }
                                    else -> Text("pas encore sur le serveur", fontSize = 11.sp, color = Palette.TextFaint)
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
