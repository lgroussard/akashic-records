package com.akashicrecords.music

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.akashicrecords.SwipePages
import com.akashicrecords.Palette
import com.akashicrecords.SubTabs
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ConfigStore
import com.akashicrecords.data.Mood
import com.akashicrecords.data.MusicTrack
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private fun audioPermission() =
    if (Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_AUDIO else Manifest.permission.READ_EXTERNAL_STORAGE

// Mobile version of the desktop MusicPlayerWindow: ambiances, library (album accordions or
// flat filtered list), per-track edit + ambiance toggles, queue. Playback lives in
// PlaybackService; the persistent mini-player is drawn by MiniPlayer at the app level.
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MusicSection(db: AkashicDatabase, config: ConfigStore, player: PlayerConnection, initialTab: String? = null, onOpenPlayer: (similar: Boolean) -> Unit = {}) {
    val context = LocalContext.current
    val library = remember { MusicLibrary.get(context) }
    val scope = rememberCoroutineScope()

    var granted by remember {
        mutableStateOf(ContextCompat.checkSelfPermission(context, audioPermission()) == PackageManager.PERMISSION_GRANTED)
    }
    val askPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted = it }

    var folder by remember { mutableStateOf(config.musicFolder()) }
    var tracks by remember { mutableStateOf<List<MusicTrack>>(emptyList()) }
    var moods by remember { mutableStateOf<List<Mood>>(emptyList()) }
    var selectedMood by remember { mutableStateOf<Int?>(null) }
    var moodTrackIds by remember { mutableStateOf<Set<Int>>(emptySet()) }
    var search by remember { mutableStateOf("") }
    var scanning by remember { mutableStateOf(false) }
    // Two tabs; the old ones arrive from links: Queue / Similar open the full player, Downloads its sheet.
    var tab by remember { mutableStateOf(if (initialTab == "Search") "Search" else "Library") }
    var showDownloads by remember { mutableStateOf(initialTab == "Downloads") }
    LaunchedEffect(initialTab) { if (initialTab == "Queue" || initialTab == "Similar") onOpenPlayer(initialTab == "Similar") }
    var editingFolder by remember { mutableStateOf(false) }
    var creatingMood by remember { mutableStateOf(false) }
    var confirmDeleteMood by remember { mutableStateOf<Mood?>(null) }

    suspend fun reloadMoods() {
        moods = withContext(Dispatchers.IO) { db.moodDao().getAll() }
        if (selectedMood != null && moods.none { it.Id == selectedMood }) selectedMood = null
    }

    fun rescan() = scope.launch {
        scanning = true
        tracks = withContext(Dispatchers.IO) { library.scanAndSync(folder) }
        reloadMoods()
        scanning = false
    }

    LaunchedEffect(granted, folder) { if (granted) rescan() }
    // Files added by a download run (from any screen): refresh the list.
    val libraryVersion by Downloader.libraryVersion.collectAsState()
    LaunchedEffect(libraryVersion) { if (granted && libraryVersion > 0) rescan() }
    LaunchedEffect(selectedMood, tracks) {
        moodTrackIds = selectedMood?.let { id -> withContext(Dispatchers.IO) { db.trackMoodDao().trackIdsForMood(id).toSet() } } ?: emptySet()
    }

    // Only tracks with a file on this phone are listed.
    val playable = tracks.filter { library.uriFor(it) != null }
    val filtered = playable
        .filter { selectedMood == null || it.Id in moodTrackIds }
        .filter {
            search.isBlank() || it.Title.contains(search, true) || it.Artist.contains(search, true) || it.Album.contains(search, true)
        }
        .sortedBy { it.Title.lowercase() }

    fun play(list: List<MusicTrack>, index: Int) {
        player.playQueue(list.mapNotNull { t -> library.uriFor(t)?.let { t to it } }, index)
    }

    Column(Modifier.fillMaxSize()) {

        if (!granted) {
            Column(Modifier.padding(12.dp)) {
                Text("L'accès aux fichiers audio est nécessaire pour lire le dossier « $folder ».", color = Palette.TextMuted)
                Spacer(Modifier.height(8.dp))
                Button(onClick = { askPermission.launch(audioPermission()) }) { Text("Autoriser") }
            }
            return@Column
        }

        val tabs = listOf("Bibliothèque" to "Library", "Rechercher" to "Search")
        // Downloads: behind ⤓ (badge while a run is going) rather than a tab of their own.
        val downloads by Downloader.state.collectAsState()
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.weight(1f)) { SubTabs(tabs, tab) { tab = it } }
            TextButton(onClick = { showDownloads = true }) {
                Text(if (downloads.running) "⤓ …" else "⤓", fontSize = 18.sp, color = if (downloads.running) Palette.Accent else Palette.Text)
            }
        }

        // The tabs are pages: a swipe moves between them like a tap on the chips.
        SwipePages(
            count = tabs.size,
            page = tabs.indexOfFirst { it.second == tab }.coerceAtLeast(0),
            onPageChange = { tab = tabs[it].second },
            modifier = Modifier.fillMaxWidth().weight(1f),
        ) { page ->
        Column(Modifier.fillMaxSize()) {
        when (tabs[page].second) {
            "Search" -> {
                SearchPanel(db, player, playable) { t -> library.uriFor(t)?.let { player.playNext(t, it) } }
                return@Column
            }
        }

        // Folder + rescan
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(
                "Dossier : $folder",
                fontSize = 11.sp,
                color = Palette.TextFaint,
                modifier = Modifier.weight(1f).clickable { editingFolder = true },
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            TextButton(onClick = { rescan() }, enabled = !scanning) { Text(if (scanning) "Scan…" else "⟳ Rescanner") }
        }

        // Ambiances
        Row(
            Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text("AMBIANCES", fontSize = 10.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold)
            Spacer(Modifier.width(6.dp))
            moods.forEach { m ->
                FilterChip(
                    selected = m.Id == selectedMood,
                    onClick = { selectedMood = if (selectedMood == m.Id) null else m.Id },
                    label = { Text(m.Name, maxLines = 1) },
                )
                Spacer(Modifier.width(4.dp))
            }
            TextButton(onClick = { creatingMood = true }) { Text("+") }
            selectedMood?.let { id ->
                TextButton(onClick = { confirmDeleteMood = moods.firstOrNull { it.Id == id } }) { Text("🗑") }
            }
        }

        OutlinedTextField(
            value = search,
            onValueChange = { search = it },
            placeholder = { Text("Rechercher titre, artiste, album") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
        )

        if (playable.isEmpty()) {
            Text(
                if (scanning) "Scan en cours…"
                else "Aucune piste sur ce téléphone. Copie des fichiers audio dans « $folder » " +
                    "(un sous-dossier = un album, comme le dossier music/ du PC), puis « Rescanner ».",
                color = Palette.TextMuted,
                modifier = Modifier.padding(12.dp),
            )
            return@Column
        }

        val showAlbums = selectedMood == null && search.isBlank()
        var expandedAlbum by remember { mutableStateOf<String?>(null) }
        var openTrack by remember { mutableStateOf<Int?>(null) }

        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            if (showAlbums) {
                val groups = playable.groupBy { it.Album }
                    .toSortedMap(compareBy { if (it.isEmpty()) "￿" else it.lowercase() })
                groups.forEach { (album, list) ->
                    val albumTracks = list.sortedBy { it.Title.lowercase() }
                    item(key = "album:$album") {
                        Row(
                            Modifier.fillMaxWidth()
                                .clickable { expandedAlbum = if (expandedAlbum == album) null else album }
                                .padding(horizontal = 12.dp, vertical = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            SmallAction("▶") { play(albumTracks, 0) }
                            Spacer(Modifier.width(8.dp))
                            Text(album.ifEmpty { "Sans album" }, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
                            Text("${albumTracks.size}  ${if (expandedAlbum == album) "▴" else "▾"}", color = Palette.TextFaint, fontSize = 12.sp)
                        }
                    }
                    if (expandedAlbum == album) {
                        itemsIndexed(albumTracks, key = { _, t -> "t:${t.Id}" }) { i, t ->
                            TrackRow(db, t, moods, open = openTrack == t.Id,
                                onToggleOpen = { openTrack = if (openTrack == t.Id) null else t.Id },
                                onPlay = { play(albumTracks, i) },
                                onPlayNext = { library.uriFor(t)?.let { player.playNext(t, it) } },
                                onSaved = { rescan() })
                        }
                    }
                }
            } else {
                itemsIndexed(filtered, key = { _, t -> "t:${t.Id}" }) { i, t ->
                    TrackRow(db, t, moods, open = openTrack == t.Id,
                        onToggleOpen = { openTrack = if (openTrack == t.Id) null else t.Id },
                        onPlay = { play(filtered, i) },
                        onPlayNext = { library.uriFor(t)?.let { player.playNext(t, it) } },
                        onSaved = { rescan() })
                }
                if (filtered.isEmpty()) item { Text("Aucun résultat.", color = Palette.TextMuted, modifier = Modifier.padding(12.dp)) }
            }
        }
        } // page Column
        } // SwipePages
    }

    if (showDownloads) {
        ModalBottomSheet(
            onDismissRequest = { showDownloads = false },
            sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
            containerColor = Palette.Surface1,
        ) {
            Text("Téléchargements", fontSize = 18.sp, modifier = Modifier.padding(horizontal = 16.dp))
            Column(Modifier.fillMaxWidth().fillMaxHeight(0.85f)) { DownloadsPanel(db, folder) { rescan() } }
        }
    }

    if (editingFolder) {
        var value by remember { mutableStateOf(folder) }
        AlertDialog(
            onDismissRequest = { editingFolder = false },
            title = { Text("Dossier de musique") },
            text = {
                Column {
                    Text("Chemin depuis la racine du stockage du téléphone.", fontSize = 12.sp, color = Palette.TextMuted)
                    OutlinedTextField(value, { value = it }, singleLine = true)
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    config.saveMusicFolder(value)
                    folder = config.musicFolder()
                    editingFolder = false
                }) { Text("Enregistrer") }
            },
            dismissButton = { TextButton(onClick = { editingFolder = false }) { Text("Annuler") } },
        )
    }

    if (creatingMood) {
        var name by remember { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { creatingMood = false },
            title = { Text("Nouvelle ambiance") },
            text = { OutlinedTextField(name, { name = it }, singleLine = true) },
            confirmButton = {
                TextButton(onClick = {
                    val n = name.trim()
                    creatingMood = false
                    if (n.isNotEmpty()) scope.launch {
                        withContext(Dispatchers.IO) { db.moodDao().add(Mood(Name = n)) }
                        reloadMoods()
                    }
                }) { Text("Créer") }
            },
            dismissButton = { TextButton(onClick = { creatingMood = false }) { Text("Annuler") } },
        )
    }

    confirmDeleteMood?.let { m ->
        AlertDialog(
            onDismissRequest = { confirmDeleteMood = null },
            title = { Text("Supprimer l'ambiance « ${m.Name} » ?") },
            text = { Text("Les pistes ne sont pas supprimées, seulement leur rattachement à cette ambiance.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmDeleteMood = null
                    scope.launch {
                        withContext(Dispatchers.IO) {
                            db.moodDao().deleteLinksForMood(m.Id)
                            db.moodDao().deleteById(m.Id)
                        }
                        reloadMoods()
                    }
                }) { Text("Supprimer") }
            },
            dismissButton = { TextButton(onClick = { confirmDeleteMood = null }) { Text("Annuler") } },
        )
    }
}

@Composable
private fun SmallAction(label: String, onClick: () -> Unit) {
    Text(
        label,
        color = Palette.Text,
        modifier = Modifier
            .clip(RoundedCornerShape(6.dp))
            .background(Palette.Surface3)
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 4.dp),
    )
}

internal fun formatTime(seconds: Double): String {
    val s = seconds.toLong()
    return "%d:%02d".format(s / 60, s % 60)
}

@Composable
private fun TrackRow(
    db: AkashicDatabase,
    track: MusicTrack,
    moods: List<Mood>,
    open: Boolean,
    onToggleOpen: () -> Unit,
    onPlay: () -> Unit,
    onPlayNext: () -> Unit,
    onSaved: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    Column(Modifier.fillMaxWidth().padding(start = 20.dp, end = 12.dp, top = 2.dp, bottom = 2.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f).clickable(onClick = onPlay).padding(vertical = 6.dp)) {
                Text(track.Title.ifBlank { "(sans titre)" }, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val sub = listOfNotNull(track.Artist.takeIf { it.isNotBlank() }, track.DurationSeconds?.let(::formatTime)).joinToString(" · ")
                if (sub.isNotEmpty()) Text(sub, fontSize = 11.sp, color = Palette.TextMuted)
            }
            TextButton(onClick = onPlayNext) { Text("⇥", fontSize = 16.sp) }
            TextButton(onClick = onToggleOpen) { Text(if (open) "▴" else "✎") }
        }
        if (open) {
            var title by remember(track.Id) { mutableStateOf(track.Title) }
            var artist by remember(track.Id) { mutableStateOf(track.Artist) }
            var applied by remember(track.Id) { mutableStateOf<Set<Int>>(emptySet()) }
            LaunchedEffect(track.Id) {
                applied = withContext(Dispatchers.IO) { db.trackMoodDao().forTrack(track.Id).map { it.MoodId }.toSet() }
            }
            OutlinedTextField(title, { title = it }, label = { Text("Titre") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(artist, { artist = it }, label = { Text("Artiste") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            TextButton(onClick = {
                scope.launch {
                    withContext(Dispatchers.IO) { db.musicTrackDao().updateMetadata(track.Id, title.trim(), artist.trim()) }
                    onSaved()
                }
            }) { Text("Enregistrer") }
            if (moods.isEmpty()) {
                Text("Aucune ambiance définie.", fontSize = 11.sp, color = Palette.TextFaint)
            } else {
                Row(Modifier.horizontalScroll(rememberScrollState())) {
                    moods.forEach { m ->
                        val on = m.Id in applied
                        FilterChip(
                            selected = on,
                            onClick = {
                                applied = if (on) applied - m.Id else applied + m.Id
                                scope.launch(Dispatchers.IO) {
                                    if (on) db.trackMoodDao().unlink(track.Id, m.Id) else db.trackMoodDao().link(track.Id, m.Id)
                                }
                            },
                            label = { Text(m.Name) },
                        )
                        Spacer(Modifier.width(4.dp))
                    }
                }
            }
            HorizontalDivider(color = Palette.BorderStrong, modifier = Modifier.padding(vertical = 4.dp))
        }
    }
}

@Composable
internal fun QueueList(player: PlayerConnection) {
    val ui = player.ui
    if (ui.queue.isEmpty()) {
        Text("File d'attente vide.", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
        return
    }
    LazyColumn(Modifier.fillMaxWidth()) {
        itemsIndexed(ui.queue) { i, (_, label) ->
            Row(
                Modifier.fillMaxWidth().clickable { player.jumpTo(i) }.padding(horizontal = 12.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    (if (i == ui.currentIndex) "▶  " else "${i + 1}.  ") + label,
                    fontWeight = if (i == ui.currentIndex) FontWeight.Bold else FontWeight.Normal,
                    color = if (i == ui.currentIndex) Palette.Accent else Palette.Text,
                    modifier = Modifier.weight(1f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                if (i != ui.currentIndex) TextButton(onClick = { player.removeAt(i) }) { Text("✕") }
            }
        }
    }
}

// Persistent bar at the bottom of every section once something is queued; a tap on the song opens the full player
// (FullPlayer: shuffle, position, À suivre, Similaires).
@Composable
fun MiniPlayer(player: PlayerConnection, db: AkashicDatabase, onOpen: () -> Unit) {
    val ui = player.ui
    if (ui.currentIndex < 0) return
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    // Desktop SaveButton: ⤓ on a playing suggestion queues it for download, ✓ once queued.
    val saveUrl = ui.suggestionTitle?.let { Downloader.searchQuery(it, ui.suggestionArtist ?: "") }
    var saved by remember(saveUrl) { mutableStateOf(false) }
    LaunchedEffect(saveUrl) {
        saved = saveUrl != null && withContext(Dispatchers.IO) { db.downloadItemDao().countByUrl(saveUrl) > 0 }
    }
    var dragging by remember { mutableStateOf<Float?>(null) }
    Column(
        Modifier.fillMaxWidth()
            .background(Palette.Surface2)
            .padding(horizontal = 12.dp, vertical = 4.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f).clickable(onClick = onOpen)) {
                Text(ui.title.ifBlank { "Aucune piste" }, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.Bold)
                if (ui.artist.isNotBlank()) Text(ui.artist, fontSize = 11.sp, color = Palette.TextMuted, maxLines = 1)
            }
            if (saveUrl != null) {
                TextButton(
                    onClick = {
                        scope.launch {
                            withContext(Dispatchers.IO) {
                                Downloader.enqueue(db, saveUrl, "${ui.suggestionTitle} — ${ui.suggestionArtist}", ui.album)
                            }
                            saved = true
                            // Straight away (a run in progress picks it up as the next item).
                            Downloader.runQueue(context, db, ConfigStore(context).musicFolder())
                        }
                    },
                    enabled = !saved,
                ) { Text(if (saved) "✓" else "⤓", fontSize = 20.sp) }
            }
            TextButton(onClick = { player.previous() }) { Text("⏮", fontSize = 18.sp) }
            TextButton(onClick = { player.togglePlay() }) { Text(if (ui.isPlaying) "❚❚" else "▶", fontSize = 18.sp) }
            TextButton(onClick = { player.next() }) { Text("⏭", fontSize = 18.sp) }
        }
        // Where the song is, as a thin line (the full player has the slider).
        if (ui.durationMs > 0) {
            LinearProgressIndicator(
                progress = { (ui.positionMs.toFloat() / ui.durationMs).coerceIn(0f, 1f) },
                modifier = Modifier.fillMaxWidth().padding(bottom = 2.dp).height(2.dp),
                color = Palette.Accent,
            )
        }
    }
}
