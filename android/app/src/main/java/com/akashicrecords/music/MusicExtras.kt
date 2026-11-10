package com.akashicrecords.music

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
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
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// "SIMILAIRES" for the playing track (desktop panel under the player). ▶ plays the local copy when
// the title is already in the library, else the 30 s clip; ⤓ queues it for download.
@Composable
internal fun SimilarPanel(
    db: AkashicDatabase,
    player: PlayerConnection,
    library: List<MusicTrack>,
    onPlayLocal: (MusicTrack) -> Unit,
) {
    val scope = rememberCoroutineScope()
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
    var queued by remember { mutableStateOf<Set<String>>(emptySet()) }
    var fetching by remember { mutableStateOf<String?>(null) }
    val context = LocalContext.current

    // Full track through yt-dlp (desktop ▶ "Écouter le titre entier"), 30 s clip if that fails.
    fun listen(t: SimilarTrack) {
        if (fetching != null) return
        fetching = t.title
        scope.launch {
            val file = Downloader.fetchStream(context, t.title, t.artist)
            when {
                file != null -> player.playSuggestion(t, android.net.Uri.fromFile(file), album, full = true)
                t.preview != null -> player.playSuggestion(t, android.net.Uri.parse(t.preview), album, full = false)
            }
            fetching = null
        }
    }

    LaunchedEffect(seed) {
        val (t, a) = seed ?: return@LaunchedEffect
        loading = true
        list = withContext(Dispatchers.IO) { Recommendations.similar(t, a) }
        queued = withContext(Dispatchers.IO) { db.downloadItemDao().getAll().map { it.Url }.toSet() }
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
        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            items(list, key = { it.title + "|" + it.artist }) { t ->
                val local = library.firstOrNull { Recommendations.matches(it.Title, t.title) }
                val url = Downloader.searchQuery(t.title, t.artist)
                Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f).clickable { if (local != null) onPlayLocal(local) else listen(t) }.padding(vertical = 6.dp)) {
                        Text(t.title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            t.artist + when { local != null -> " · dans la bibliothèque"; fetching == t.title -> " · chargement…"; else -> "" },
                            fontSize = 11.sp, color = Palette.TextMuted, maxLines = 1,
                        )
                    }
                    when {
                        local != null -> Text("✓", color = Palette.TextFaint, modifier = Modifier.padding(12.dp))
                        url in queued -> Text("En file", fontSize = 11.sp, color = Palette.TextFaint, modifier = Modifier.padding(12.dp))
                        else -> TextButton(onClick = {
                            scope.launch {
                                withContext(Dispatchers.IO) { Downloader.enqueue(db, url, "${t.title} — ${t.artist}", album) }
                                queued = queued + url
                            }
                        }) { Text("⤓", fontSize = 18.sp) }
                    }
                }
            }
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
