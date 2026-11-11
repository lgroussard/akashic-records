package com.akashicrecords.music

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ConfigStore
import com.akashicrecords.data.MusicTrack
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// The full player, opened from the mini-player anywhere in the app: the song, big controls, the position, then
// « À suivre » (the queue) and « Similaires » — what used to be two tabs of the music section.
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FullPlayer(db: AkashicDatabase, player: PlayerConnection, startOnSimilar: Boolean = false, onClose: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val ui = player.ui
    val library = remember { MusicLibrary.get(context) }
    // The tracks on this phone, for « Similaires » to play a local copy when there is one.
    val playable by produceState<List<MusicTrack>>(emptyList()) {
        value = withContext(Dispatchers.IO) { db.musicTrackDao().getAll() }.filter { library.uriFor(it) != null }
    }
    var section by remember { mutableStateOf(if (startOnSimilar) "Similar" else "Queue") }
    var dragging by remember { mutableStateOf<Float?>(null) }
    val saveUrl = ui.suggestionTitle?.let { Downloader.searchQuery(it, ui.suggestionArtist ?: "") }
    var saved by remember(saveUrl) { mutableStateOf(false) }
    LaunchedEffect(saveUrl) {
        saved = saveUrl != null && withContext(Dispatchers.IO) { db.downloadItemDao().countByUrl(saveUrl) > 0 }
    }

    ModalBottomSheet(
        onDismissRequest = onClose,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = Palette.AppBg,
    ) {
        Column(Modifier.fillMaxWidth().fillMaxHeight(0.92f).padding(horizontal = 16.dp)) {
            // The song
            Text(
                ui.title.ifBlank { "Aucune piste" }, fontSize = 22.sp, fontWeight = FontWeight.Bold,
                textAlign = TextAlign.Center, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.fillMaxWidth(),
            )
            Text(
                listOf(ui.artist, ui.album).filter { it.isNotBlank() }.joinToString(" · "),
                fontSize = 13.sp, color = Palette.TextMuted, textAlign = TextAlign.Center, maxLines = 1,
                overflow = TextOverflow.Ellipsis, modifier = Modifier.fillMaxWidth().padding(top = 2.dp),
            )

            // Position
            if (ui.durationMs > 0) {
                val pos = dragging ?: ui.positionMs.toFloat()
                Slider(
                    value = pos.coerceIn(0f, ui.durationMs.toFloat()),
                    onValueChange = { dragging = it },
                    onValueChangeFinished = { dragging?.let { player.seekTo(it.toLong()) }; dragging = null },
                    valueRange = 0f..ui.durationMs.toFloat(),
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                )
                Row(Modifier.fillMaxWidth()) {
                    Text(formatTime(pos / 1000.0), fontSize = 11.sp, color = Palette.TextFaint, modifier = Modifier.weight(1f))
                    Text(formatTime(ui.durationMs / 1000.0), fontSize = 11.sp, color = Palette.TextFaint)
                }
            }

            // Controls
            Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = { player.toggleShuffle() }) {
                    Text("⇄", fontSize = 24.sp, color = if (ui.shuffle) Palette.Accent else Palette.TextFaint)
                }
                TextButton(onClick = { player.previous() }) { Text("⏮", fontSize = 28.sp) }
                FilledIconButton(onClick = { player.togglePlay() }, modifier = Modifier.size(64.dp)) {
                    Text(if (ui.isPlaying) "❚❚" else "▶", fontSize = 26.sp)
                }
                TextButton(onClick = { player.next() }) { Text("⏭", fontSize = 28.sp) }
                // A suggestion playing: ⤓ keeps it (download into the library), ✓ once queued.
                if (saveUrl != null) {
                    TextButton(onClick = {
                        scope.launch {
                            withContext(Dispatchers.IO) { Downloader.enqueue(db, saveUrl, "${ui.suggestionTitle} — ${ui.suggestionArtist}", ui.album) }
                            saved = true
                            Downloader.runQueue(context, db, ConfigStore(context).musicFolder())
                        }
                    }, enabled = !saved) { Text(if (saved) "✓" else "⤓", fontSize = 24.sp) }
                } else Spacer(Modifier.width(48.dp))
            }

            // À suivre / Similaires
            Row(Modifier.padding(top = 4.dp)) {
                FilterChip(selected = section == "Queue", onClick = { section = "Queue" }, label = { Text("À suivre (${ui.queue.size})") })
                Spacer(Modifier.width(8.dp))
                FilterChip(selected = section == "Similar", onClick = { section = "Similar" }, label = { Text("Similaires") })
            }
            Box(Modifier.fillMaxWidth().weight(1f).padding(top = 4.dp)) {
                if (section == "Queue") QueueList(player)
                else Column(Modifier.fillMaxSize()) {
                    SimilarPanel(db, player, playable) { t -> library.uriFor(t)?.let { player.playNext(t, it) } }
                }
            }
        }
    }
}
