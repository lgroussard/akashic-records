package com.akashicrecords.music

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.DownloadFolder
import com.akashicrecords.data.DownloadItem
import com.akashicrecords.data.MusicTrack
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// "SIMILAIRES" for the playing track (desktop panel under the player). Rows: see RemoteTrackList.
@Composable
internal fun SimilarPanel(
    db: AkashicDatabase,
    player: PlayerConnection,
    library: List<MusicTrack>,
    onPlayLocal: (MusicTrack) -> Unit,
) {
    val ui = player.ui
    // Seed = what is playing: a library track or a suggestion (chained or not), with the
    // suggestion's clean title/artist. Only a title started from this list keeps the current list
    // (tapping a row must not swap the list under the finger) — unless there is no list yet.
    var seed by remember { mutableStateOf<Pair<String, String>?>(null) }
    var album by remember { mutableStateOf("") }
    LaunchedEffect(ui.title, ui.artist, ui.fromSimilarTab) {
        if (ui.title.isBlank()) return@LaunchedEffect
        if (ui.fromSimilarTab && seed != null) return@LaunchedEffect
        seed = (ui.suggestionTitle ?: ui.title) to (ui.suggestionArtist ?: ui.artist)
        album = ui.album
    }
    var list by remember { mutableStateOf<List<SimilarTrack>>(emptyList()) }
    var loading by remember { mutableStateOf(false) }

    LaunchedEffect(seed) {
        val (t, a) = seed ?: return@LaunchedEffect
        loading = true
        list = withContext(Dispatchers.IO) { Recommendations.similar(t, a) }
        loading = false
    }

    val s = seed
    if (s == null) {
        Text("Lance une piste : les titres similaires apparaissent ici.", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
        return
    }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Autour de « ${s.first} »", fontSize = 12.sp, color = Palette.TextFaint, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
            TextButton(onClick = { seed = s.copy() }, enabled = !loading) { Text("⟳") }
        }
        when {
            loading -> Text("Recherche…", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
            list.isEmpty() -> Text("Aucune suggestion (pas de réseau, ou titre inconnu des catalogues).", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
        }
        // Suggestions go into the folder of the album that is playing (they are "around" it).
        RemoteTrackList(db, player, library, list, albumFor = { album }, onPlayLocal = onPlayLocal, modifier = Modifier.weight(1f))
    }
}

// "RECHERCHER", Spotify-style: best match, then songs (re-ranked, see SearchRanking), artists and albums.
// Tapping an artist or an album opens its songs; "← Résultats" comes back. Song rows: see RemoteTrackRow.
@Composable
internal fun SearchPanel(
    db: AkashicDatabase,
    player: PlayerConnection,
    library: List<MusicTrack>,
    onPlayLocal: (MusicTrack) -> Unit,
) {
    // The query survives tab swipes and visits for the life of the app; its results come back from the cache.
    var query by rememberSaveable { mutableStateOf("") }
    // yt-dlp ready before the first tap (unpacking, daily update): the tap only has the song to fetch.
    val warmContext = androidx.compose.ui.platform.LocalContext.current
    LaunchedEffect(Unit) { Downloader.warmUp(warmContext) }
    var tracks by remember { mutableStateOf<List<SimilarTrack>?>(null) }
    var extras by remember { mutableStateOf<OnlineExtras?>(null) }
    var extrasFor by remember { mutableStateOf("") }
    var loading by remember { mutableStateOf(false) }
    var showAllTracks by remember { mutableStateOf(false) }
    var openArtist by remember { mutableStateOf<OnlineArtist?>(null) }
    var openAlbum by remember { mutableStateOf<OnlineAlbum?>(null) }
    var detail by remember { mutableStateOf<List<SimilarTrack>?>(null) }
    // Seconds before the search may ask iTunes again (0 = free); the screen says so instead of "no result".
    var limitedSeconds by remember { mutableStateOf(0L) }
    val keyboard = LocalSoftwareKeyboardController.current
    // A song was chosen and plays: back to an empty search, as in any search bar (the artist/album page closes too).
    val resetSearch: () -> Unit = { query = ""; openArtist = null; openAlbum = null; keyboard?.hide() }
    val actions = rememberRemoteActions(db, player)
    val libraryArtists = remember(library) { library.mapTo(HashSet()) { SearchRanking.norm(it.Artist) } }

    // Search as you type. Each keystroke restarts this effect, which cancels the pending search: songs come
    // 350 ms after the last key (1 request), artists + albums once the typing has paused ~0.9 s (2 requests) —
    // iTunes allows ~20 requests a minute. The previous results stay on screen until the new ones arrive.
    LaunchedEffect(query) {
        val q = query.trim()
        showAllTracks = false
        limitedSeconds = 0
        if (q.length < 2) { tracks = null; extras = null; loading = false; return@LaunchedEffect }
        delay(350)
        openArtist = null; openAlbum = null
        loading = true
        try {
            tracks = retryWhenLimited({ limitedSeconds = it }) { OnlineSearch.tracks(q, libraryArtists) }
        } finally {
            loading = false
        }
        delay(550)
        extras = retryWhenLimited({ limitedSeconds = it }) { OnlineSearch.extras(q) }
        extrasFor = q
    }
    LaunchedEffect(openArtist, openAlbum) {
        detail = null
        detail = retryWhenLimited({ limitedSeconds = it }) {
            openArtist?.let { OnlineSearch.artistTracks(it) } ?: openAlbum?.let { OnlineSearch.albumTracks(it) }
        }
    }

    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            placeholder = { Text("Titre, artiste, album") },
            singleLine = true,
            trailingIcon = if (query.isNotEmpty()) ({ TextButton(onClick = { query = "" }) { Text("✕") } }) else null,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
            keyboardActions = KeyboardActions(onSearch = { keyboard?.hide() }),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp),
        )
        // Thin bar while a search is on its way (the old results stay visible meanwhile).
        if (limitedSeconds > 0) {
            Text(
                "Trop de recherches d'affilée pour iTunes : reprise automatique dans $limitedSeconds s. Les suggestions restent prioritaires.",
                fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp),
            )
        }
        Box(Modifier.fillMaxWidth().height(2.dp).padding(horizontal = 12.dp)) {
            if (loading) LinearProgressIndicator(Modifier.fillMaxSize(), color = Palette.Accent)
        }

        // --- Artist / album page ---
        val artist = openArtist
        val album = openAlbum
        if (artist != null || album != null) {
            BackHandler { openArtist = null; openAlbum = null }
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = { openArtist = null; openAlbum = null }) { Text("← Résultats") }
            }
            Row(Modifier.padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                if (album != null) RemoteImage(album.artwork, Modifier.size(64.dp).clip(RoundedCornerShape(6.dp)))
                else ArtistBadge(artist!!.name, 64.dp)
                Spacer(Modifier.width(12.dp))
                Column {
                    Text(album?.title ?: artist!!.name, fontWeight = FontWeight.Bold, fontSize = 17.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    Text(
                        if (album != null) listOfNotNull(album.artist, album.year).joinToString(" · ")
                        else listOfNotNull("Artiste", artist!!.genre).joinToString(" · "),
                        fontSize = 12.sp, color = Palette.TextMuted,
                    )
                }
            }
            val tracks = detail
            if (tracks == null) Text("Chargement…", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
            else if (tracks.isEmpty()) Text("Aucun titre trouvé.", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
            LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
                items(tracks.orEmpty(), key = { it.title + "|" + it.artist + "|" + it.album }) { t ->
                    // An album's songs go to that album's folder; an artist's to each song's own album.
                    RemoteTrackRow(t, actions, library, album?.title ?: t.album.orEmpty(), showAlbum = album == null, onPlayLocal = onPlayLocal, onPicked = resetSearch)
                }
            }
            return@Column
        }

        // --- Results ---
        val found = tracks
        // Artists/albums may still be those of a few letters ago while the typing goes on; the artist only takes
        // the best-match slot once they belong to the current query.
        val artists = extras?.artists.orEmpty()
        val albums = extras?.albums.orEmpty()
        val topArtist = extras?.topArtist?.takeIf { extrasFor == query.trim() }
        when {
            found == null -> Text(
                "Tape un titre, un artiste ou un album : les résultats arrivent pendant que tu écris. Touche un titre pour l'écouter en entier, ⤓ pour le télécharger dans la bibliothèque.",
                color = Palette.TextMuted, fontSize = 13.sp, modifier = Modifier.padding(12.dp),
            )
            !loading && found.isEmpty() && artists.isEmpty() && albums.isEmpty() ->
                Text("Aucun résultat (ou pas de réseau).", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
        }
        if (found == null) return@Column

        // Best match: the artist when the query is exactly their name, else the best-ranked song.
        val topTrack = if (topArtist == null) found.firstOrNull() else null
        val others = if (topTrack != null) found.drop(1) else found
        val shown = if (showAllTracks) others else others.take(6)

        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            if (topArtist != null || topTrack != null) {
                item { SectionTitle("MEILLEUR RÉSULTAT") }
                item {
                    if (topArtist != null) {
                        Row(
                            Modifier.fillMaxWidth().clickable { openArtist = topArtist }.padding(horizontal = 12.dp, vertical = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            ArtistBadge(topArtist.name, 72.dp)
                            Spacer(Modifier.width(12.dp))
                            Column {
                                Text(topArtist.name, fontWeight = FontWeight.Bold, fontSize = 18.sp)
                                Text(listOfNotNull("Artiste", topArtist.genre).joinToString(" · "), fontSize = 12.sp, color = Palette.TextMuted)
                            }
                        }
                    } else if (topTrack != null) {
                        RemoteTrackRow(topTrack, actions, library, topTrack.album.orEmpty(), showAlbum = true, onPlayLocal = onPlayLocal, coverSize = 72.dp, onPicked = resetSearch)
                    }
                }
            }
            if (shown.isNotEmpty()) {
                item { SectionTitle("TITRES") }
                items(shown, key = { "t|" + it.title + "|" + it.artist + "|" + it.album }) { t ->
                    RemoteTrackRow(t, actions, library, t.album.orEmpty(), showAlbum = true, onPlayLocal = onPlayLocal, onPicked = resetSearch)
                }
                if (others.size > shown.size || showAllTracks) item {
                    TextButton(onClick = { showAllTracks = !showAllTracks }, modifier = Modifier.padding(horizontal = 4.dp)) {
                        Text(if (showAllTracks) "Moins de titres" else "Tous les titres (${others.size})")
                    }
                }
            }
            if (artists.isNotEmpty()) {
                item { SectionTitle("ARTISTES") }
                item {
                    LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        items(artists, key = { it.id }) { a ->
                            Column(Modifier.width(84.dp).clickable { openArtist = a }, horizontalAlignment = Alignment.CenterHorizontally) {
                                ArtistBadge(a.name, 72.dp)
                                Text(a.name, fontSize = 12.sp, maxLines = 2, overflow = TextOverflow.Ellipsis, textAlign = TextAlign.Center)
                                // Homonyms are common in iTunes (several "Angèle"): the genre tells them apart.
                                a.genre?.let { Text(it, fontSize = 10.sp, color = Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis, textAlign = TextAlign.Center) }
                            }
                        }
                    }
                }
            }
            if (albums.isNotEmpty()) {
                item { SectionTitle("ALBUMS") }
                item {
                    LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        items(albums, key = { it.id }) { al ->
                            Column(Modifier.width(112.dp).clickable { openAlbum = al }) {
                                RemoteImage(al.artwork, Modifier.size(112.dp).clip(RoundedCornerShape(6.dp)))
                                Text(al.title, fontSize = 12.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                Text(listOfNotNull(al.artist, al.year).joinToString(" · "), fontSize = 10.sp, color = Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                        }
                    }
                }
                item { Spacer(Modifier.height(16.dp)) }
            }
        }
    }
}

// Runs a search step; when the iTunes limit holds it back, reports the wait (in seconds, counting down), sleeps it
// and tries again — the screen keeps its previous results meanwhile. Cancelled with the calling effect (new keystroke).
private suspend fun <T> retryWhenLimited(onWait: (Long) -> Unit, block: suspend () -> T): T {
    while (true) {
        try {
            return block().also { onWait(0) }
        } catch (e: ItunesRateLimited) {
            var left = (e.waitMs.coerceAtLeast(1_000) + 999) / 1_000
            while (left > 0) {
                onWait(left)
                delay(1_000)
                left--
            }
        }
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(text, fontSize = 11.sp, fontWeight = FontWeight.Bold, color = Palette.TextFaint,
        modifier = Modifier.padding(start = 12.dp, top = 12.dp, bottom = 4.dp))
}

// iTunes has no artist pictures: a round badge with the initial stands in.
@Composable
private fun ArtistBadge(name: String, size: Dp) {
    Box(Modifier.size(size).clip(CircleShape).background(Palette.Surface3), contentAlignment = Alignment.Center) {
        Text(name.trim().take(1).uppercase(), fontSize = (size.value * 0.4f).sp, fontWeight = FontWeight.Bold, color = Palette.Accent)
    }
}

// What the rows do, shared by every row of a list: ▶ fetches the full track through yt-dlp (desktop "Écouter le
// titre entier"), the 30 s clip if that fails; ⤓ queues it in Téléchargements. One fetch at a time.
internal class RemoteActions(
    private val db: AkashicDatabase,
    private val player: PlayerConnection,
    private val context: android.content.Context,
    private val scope: kotlinx.coroutines.CoroutineScope,
) {
    var queued by mutableStateOf<Set<String>>(emptySet())
    var fetching by mutableStateOf<String?>(null)

    suspend fun refreshQueued() {
        queued = withContext(Dispatchers.IO) { db.downloadItemDao().getAll().map { it.Url }.toSet() }
    }

    private var fetchJob: kotlinx.coroutines.Job? = null

    // Sound at once: the iTunes clip starts right away while yt-dlp fetches the full track, which then takes
    // its place at the same second. A new tap drops the fetch still running for the previous song.
    // [onStarted] runs once something actually plays (not when there is neither a clip nor a full track).
    fun listen(t: SimilarTrack, album: String, onStarted: () -> Unit = {}) {
        fetching?.let { previous -> if (previous == t.title) return; Downloader.cancelStream(previous) }
        fetchJob?.cancel()
        fetching = t.title
        fetchJob = scope.launch {
            val clip = t.preview
            if (clip != null) {
                player.playSuggestion(t, android.net.Uri.parse(clip), album, full = false)
                onStarted()
            }
            val file = Downloader.fetchStream(context, t.title, t.artist)
            if (file != null && isActive) {
                if (clip != null) player.upgradeSuggestion(t, android.net.Uri.fromFile(file), album)
                else { player.playSuggestion(t, android.net.Uri.fromFile(file), album, full = true); onStarted() }
            }
            if (fetching == t.title) fetching = null
        }
    }

    fun download(t: SimilarTrack, album: String) {
        val url = Downloader.searchQuery(t.title, t.artist)
        scope.launch {
            withContext(Dispatchers.IO) { Downloader.enqueue(db, url, "${t.title} — ${t.artist}", album) }
            queued = queued + url
        }
    }
}

@Composable
internal fun rememberRemoteActions(db: AkashicDatabase, player: PlayerConnection): RemoteActions {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val actions = remember { RemoteActions(db, player, context, scope) }
    LaunchedEffect(Unit) { actions.refreshQueued() }
    return actions
}

// One online song. Tap: the local copy if the title is already in the library, else listen. ⤓ downloads into the
// album folder [album]. With [coverSize], the iTunes cover is shown on the left.
@Composable
internal fun RemoteTrackRow(
    t: SimilarTrack,
    actions: RemoteActions,
    library: List<MusicTrack>,
    album: String,
    onPlayLocal: (MusicTrack) -> Unit,
    showAlbum: Boolean = false,
    coverSize: Dp? = 44.dp,
    // Called once the chosen song plays (the search uses it to reset itself). ⤓ never calls it.
    onPicked: () -> Unit = {},
) {
    val local = remember(t, library) { library.firstOrNull { Recommendations.matches(it.Title, t.title) } }
    val url = Downloader.searchQuery(t.title, t.artist)
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        if (coverSize != null && t.artwork != null) {
            RemoteImage(t.artwork, Modifier.size(coverSize).clip(RoundedCornerShape(4.dp)))
            Spacer(Modifier.width(10.dp))
        }
        Column(Modifier.weight(1f).clickable {
            if (local != null) { onPlayLocal(local); onPicked() } else actions.listen(t, album, onPicked)
        }.padding(vertical = 6.dp)) {
            Text(t.title, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = if (coverSize != null && coverSize > 60.dp) FontWeight.Bold else null)
            val where = listOfNotNull(t.artist, t.album?.takeIf { showAlbum && it.isNotBlank() }).joinToString(" · ")
            Text(
                where + when { local != null -> " · dans la bibliothèque"; actions.fetching == t.title -> if (t.preview != null) " · extrait, titre entier en route…" else " · chargement…"; else -> "" },
                fontSize = 11.sp, color = Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis,
            )
        }
        when {
            local != null -> Text("✓", color = Palette.TextFaint, modifier = Modifier.padding(12.dp))
            url in actions.queued -> Text("En file", fontSize = 11.sp, color = Palette.TextFaint, modifier = Modifier.padding(12.dp))
            else -> TextButton(onClick = { actions.download(t, album) }) { Text("⤓", fontSize = 18.sp) }
        }
    }
}

// Plain list of online songs (Similaires).
@Composable
internal fun RemoteTrackList(
    db: AkashicDatabase,
    player: PlayerConnection,
    library: List<MusicTrack>,
    tracks: List<SimilarTrack>,
    albumFor: (SimilarTrack) -> String,
    onPlayLocal: (MusicTrack) -> Unit,
    modifier: Modifier = Modifier,
) {
    val actions = rememberRemoteActions(db, player)
    LaunchedEffect(tracks) { actions.refreshQueued() }
    LazyColumn(modifier.fillMaxWidth()) {
        items(tracks, key = { it.title + "|" + it.artist + "|" + it.album }) { t ->
            RemoteTrackRow(t, actions, library, albumFor(t), onPlayLocal, coverSize = null)
        }
    }
}

// Online cover (iTunes artworkUrl100), downloaded once and kept in a small memory cache.
@Composable
internal fun RemoteImage(url: String?, modifier: Modifier) {
    val bitmap by produceState(RemoteImages.cached(url), url) {
        if (value == null && url != null) value = RemoteImages.load(url)
    }
    val b = bitmap
    if (b != null) Image(b.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Crop, modifier = modifier)
    else Box(modifier.background(Palette.Surface3))
}

internal object RemoteImages {
    private val cache = android.util.LruCache<String, android.graphics.Bitmap>(120)
    fun cached(url: String?): android.graphics.Bitmap? = url?.let { cache.get(it) }
    suspend fun load(url: String): android.graphics.Bitmap? = withContext(Dispatchers.IO) {
        try {
            val c = java.net.URL(url).openConnection() as java.net.HttpURLConnection
            c.connectTimeout = 8_000
            c.readTimeout = 10_000
            try {
                c.inputStream.use { android.graphics.BitmapFactory.decodeStream(it) }?.also { cache.put(url, it) }
            } finally {
                c.disconnect()
            }
        } catch (_: Exception) {
            null
        }
    }
}

// Desktop Téléchargements board, in list form: zones (album folders), queued / done / failed items,
// manual add (link or "title artist"), run, yt-dlp update.
@Composable
internal fun DownloadsPanel(db: AkashicDatabase, musicFolder: String, onLibraryChanged: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val state by Downloader.state.collectAsState()
    var items by remember { mutableStateOf<List<DownloadItem>>(emptyList()) }
    var zones by remember { mutableStateOf<List<DownloadFolder>>(emptyList()) }
    var input by remember { mutableStateOf("") }
    var zoneForNew by remember { mutableStateOf<String?>(null) }
    var zoneMenu by remember { mutableStateOf(false) }

    suspend fun reload() = withContext(Dispatchers.IO) {
        items = db.downloadItemDao().getAll()
        zones = db.downloadFolderDao().getAll()
    }
    LaunchedEffect(Unit) { reload() }
    // Refresh the list after each item and at the end of a run.
    LaunchedEffect(state.current, state.running) { reload() }

    val zoneName = zones.associate { it.Id to (it.Name.ifBlank { it.Subfolder ?: "" }) }
    val queued = items.count { it.Status == Downloader.QUEUED }

    Column(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                input, { input = it },
                placeholder = { Text("Lien, ou « titre artiste »") },
                singleLine = true,
                modifier = Modifier.weight(1f),
            )
            Spacer(Modifier.width(6.dp))
            Button(onClick = {
                val v = input.trim()
                if (v.isEmpty()) return@Button
                val isLink = v.startsWith("http://") || v.startsWith("https://")
                scope.launch {
                    withContext(Dispatchers.IO) {
                        Downloader.enqueue(db, if (isLink) v else "ytsearch1:${v.replace("\"", "")}", v, zoneForNew)
                    }
                    input = ""
                    reload()
                }
            }) { Text("+") }
        }
        Box {
            TextButton(onClick = { zoneMenu = true }) {
                Text("Dossier : " + (zoneForNew?.let { "$musicFolder/$it" } ?: "$musicFolder (racine)") + " ▾", fontSize = 12.sp)
            }
            DropdownMenu(expanded = zoneMenu, onDismissRequest = { zoneMenu = false }) {
                DropdownMenuItem(text = { Text("Racine (sans album)") }, onClick = { zoneForNew = null; zoneMenu = false })
                zones.mapNotNull { it.Subfolder?.takeIf { s -> s.isNotBlank() } }.distinct().forEach { z ->
                    DropdownMenuItem(text = { Text(z) }, onClick = { zoneForNew = z; zoneMenu = false })
                }
            }
        }

        Row(verticalAlignment = Alignment.CenterVertically) {
            Button(
                onClick = { Downloader.runQueue(context, db, musicFolder, onLibraryChanged) },
                enabled = !state.running && queued > 0,
            ) { Text("Télécharger ($queued)") }
            Spacer(Modifier.width(8.dp))
            TextButton(onClick = { Downloader.updateTool(context) }, enabled = !state.running) { Text("Mettre à jour yt-dlp") }
        }
        if (state.running) {
            Text(state.current?.let { "En cours : $it" } ?: (state.message ?: "…"), fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            LinearProgressIndicator(progress = { state.progress }, modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp))
        } else {
            state.message?.let { Text(it, fontSize = 12.sp, color = Palette.TextMuted) }
        }
        state.toolVersion?.let { Text("yt-dlp $it", fontSize = 10.sp, color = Palette.TextFaint) }

        if (items.isEmpty()) {
            Text(
                "File vide. Ajoute un titre ici, ou depuis l'onglet Similaires (⤓).",
                color = Palette.TextMuted,
                modifier = Modifier.padding(vertical = 12.dp),
            )
        }
        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            val order = listOf(Downloader.QUEUED, Downloader.FAILED, Downloader.DONE)
            order.forEach { status ->
                val group = items.filter { it.Status == status }
                if (group.isEmpty()) return@forEach
                item(key = "h$status") {
                    Text(
                        when (status) { Downloader.QUEUED -> "EN FILE"; Downloader.FAILED -> "ÉCHECS"; else -> "TERMINÉS" },
                        fontSize = 10.sp, fontWeight = FontWeight.Bold, color = Palette.TextFaint,
                        modifier = Modifier.padding(top = 10.dp, bottom = 2.dp),
                    )
                }
                items(group, key = { it.Id }) { d ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f).padding(vertical = 4.dp)) {
                            Text(d.Title.ifBlank { d.Url }, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(
                                "→ " + (d.FolderId?.let { zoneName[it] }?.takeIf { it.isNotBlank() } ?: "racine"),
                                fontSize = 11.sp, color = Palette.TextFaint,
                            )
                        }
                        if (d.Status == Downloader.FAILED) {
                            TextButton(onClick = {
                                scope.launch { withContext(Dispatchers.IO) { db.downloadItemDao().update(d.copy(Status = Downloader.QUEUED)) }; reload() }
                            }, enabled = !state.running) { Text("↻") }
                        }
                        // Removes the queue row only; a downloaded file stays in the music folder.
                        TextButton(onClick = {
                            scope.launch { withContext(Dispatchers.IO) { db.downloadItemDao().deleteById(d.Id) }; reload() }
                        }, enabled = !state.running || d.Status != Downloader.QUEUED) { Text("✕") }
                    }
                }
            }
        }
    }
}
