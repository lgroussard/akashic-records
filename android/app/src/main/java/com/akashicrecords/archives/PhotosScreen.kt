package com.akashicrecords.archives

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.SwipePages
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Photo
import com.akashicrecords.data.PhotoAlbum
import com.akashicrecords.journaux.ConfirmDelete
import com.akashicrecords.journaux.datePart
import com.akashicrecords.journaux.frenchDate
import com.akashicrecords.journaux.nowIso
import com.akashicrecords.media.FileImage
import com.akashicrecords.media.ImageEdits
import com.akashicrecords.media.ImageEditor
import com.akashicrecords.media.LocalImage
import com.akashicrecords.media.MediaFiles
import com.akashicrecords.media.cachedImage
import com.akashicrecords.music.rememberOnline
import com.akashicrecords.sync.HubCompute
import com.akashicrecords.sync.SyncService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private const val ALL = -1
private const val FAVORITES = -2

// Archives → Photos on the phone: a gallery (album chips, square thumbnails), a full-screen viewer to swipe through,
// ★ favourite, and + to add pictures from the phone. (Same tables as the PC: a PC import replaces them, until the
// server syncs both ways.)
@Composable
fun PhotosTab(db: AkashicDatabase) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var photos by remember { mutableStateOf<List<Photo>>(emptyList()) }
    var albums by remember { mutableStateOf<List<PhotoAlbum>>(emptyList()) }
    var filter by remember { mutableStateOf(ALL) }
    var viewing by remember { mutableStateOf<Int?>(null) } // index in the shown list
    var version by remember { mutableStateOf(0) }
    var state by remember { mutableStateOf(HubPhotos.State(emptyMap(), emptySet(), emptySet())) }
    var here by remember { mutableStateOf<Set<Int>>(emptySet()) } // ids of the photos stored on this phone
    var releasing by remember { mutableStateOf<List<Photo>?>(null) }
    val online = rememberOnline()

    LaunchedEffect(version) {
        withContext(Dispatchers.IO) {
            photos = db.photoDao().getAll().sortedByDescending { it.TakenDate ?: it.ImportedAt }
            albums = db.photoAlbumDao().getAll()
            state = HubPhotos.load(db)
            here = photos.filter { HubPhotos.isHere(context, it) }.map { it.Id }.toSet()
        }
    }
    // Offline, only the photos stored here; online, every photo (the hub's ones load as previews).
    val visible = if (online) photos else photos.filter { it.Id in here }
    val shown = when (filter) {
        ALL -> visible
        FAVORITES -> visible.filter { it.IsFavorite }
        else -> visible.filter { it.AlbumId == filter }
    }
    val albumNames = albums.associate { it.Id to it.Name }
    val currentAlbum = albums.firstOrNull { it.Id == filter }
    val add = rememberLauncherForActivityResult(ActivityResultContracts.GetMultipleContents()) { uris ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        scope.launch {
            withContext(Dispatchers.IO) {
                uris.forEach { uri ->
                    runCatching { MediaFiles.import(context, uri, "archive") }.getOrNull()?.let { path ->
                        db.photoDao().add(
                            Photo(ImagePath = path, Title = "", Description = "", TakenDate = null, Tags = "", IsFavorite = false,
                                AlbumId = filter.takeIf { it >= 0 }, ImportedAt = nowIso()),
                        )
                    }
                }
            }
            version++
        }
    }

    viewing?.let { start ->
        Viewer(db, shown, start, state, albumNames, onChanged = { version++ }, onClose = { viewing = null })
        return
    }
    releasing?.let { list ->
        val bytes = remember(list) { list.sumOf { MediaFiles.resolve(context, it.ImagePath)?.length() ?: 0L } }
        val mb = if (bytes >= 1024L * 1024 * 1024) "%.1f Go".format(bytes / (1024.0 * 1024 * 1024)) else "%.1f Mo".format(bytes / (1024.0 * 1024))
        AlertDialog(
            onDismissRequest = { releasing = null },
            title = { Text("Libérer l'espace") },
            text = { Text("${list.size} photo(s) non gardées sont stockées sur ce téléphone (environ $mb). Elles seront retirées du téléphone mais restent sur le serveur : tu les verras toujours en ligne.") },
            confirmButton = {
                TextButton(onClick = {
                    releasing = null
                    scope.launch { withContext(Dispatchers.IO) { HubPhotos.release(context, list) }; version++ }
                }) { Text("Retirer du téléphone") }
            },
            dismissButton = { TextButton(onClick = { releasing = null }) { Text("Annuler") } },
        )
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 4.dp)) {
            FilterChip(selected = filter == ALL, onClick = { filter = ALL }, label = { Text("Tout · ${visible.size}") })
            Spacer(Modifier.width(6.dp))
            FilterChip(selected = filter == FAVORITES, onClick = { filter = FAVORITES }, label = { Text("★ Favoris") })
            albums.forEach { a ->
                Spacer(Modifier.width(6.dp))
                FilterChip(selected = filter == a.Id, onClick = { filter = a.Id }, label = { Text(a.Name) })
            }
        }
        // One line under the chips: keeping the open album, freeing the phone, or why some photos are hidden.
        val releasable = remember(photos, here, state, albums) { HubPhotos.releasable(context, photos.filter { it.Id in here }, albumNames, state) }
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            when {
                currentAlbum != null -> {
                    val kept = currentAlbum.Name in state.keptAlbums
                    Text(if (kept) "Album gardé hors ligne" else "Album visible en ligne", fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.weight(1f))
                    TextButton(onClick = {
                        scope.launch { withContext(Dispatchers.IO) { HubPhotos.setAlbumKept(context, db, currentAlbum.Name, !kept) }; version++ }
                    }) { Text(if (kept) "Ne plus garder" else "⤓ Garder l'album") }
                }
                !online && photos.size > visible.size ->
                    Text("Hors ligne : seules les photos gardées s'affichent (${photos.size - visible.size} sur le serveur).", fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.padding(vertical = 8.dp))
                releasable.isNotEmpty() -> {
                    Text("${releasable.size} photo(s) non gardées sur le téléphone", fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.weight(1f))
                    TextButton(onClick = { releasing = releasable }) { Text("Libérer l'espace") }
                }
            }
        }
        Box(Modifier.fillMaxSize()) {
            if (shown.isEmpty()) {
                Text(
                    if (photos.isEmpty()) "Aucune photo. Touche + pour en ajouter depuis le téléphone, ou importe celles du PC (Plus → Réglages)."
                    else if (!online && visible.isEmpty()) "Hors ligne, et aucune photo gardée sur ce téléphone. Ouvre une photo en ligne et touche ⤓ pour la garder."
                    else "Rien ici.",
                    color = Palette.TextMuted, modifier = Modifier.padding(16.dp),
                )
            }
            LazyVerticalGrid(
                columns = GridCells.Adaptive(110.dp),
                contentPadding = PaddingValues(start = 4.dp, end = 4.dp, bottom = 88.dp),
                horizontalArrangement = Arrangement.spacedBy(3.dp),
                verticalArrangement = Arrangement.spacedBy(3.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                items(shown.size, key = { shown[it].Id }) { i ->
                    val p = shown[i]
                    Box(Modifier.aspectRatio(1f).clip(RoundedCornerShape(4.dp)).background(Palette.Surface2).clickable { viewing = i }) {
                        val isHere = p.Id in here
                        Thumb(p, state.hashes[HubPhotos.key(p)], isHere, Modifier.fillMaxSize(), full = false)
                        if (p.IsFavorite) Text("★", color = Color(0xFFF2C14E), modifier = Modifier.align(Alignment.TopEnd).padding(4.dp))
                        if (!isHere) Text("☁", fontSize = 12.sp, color = Color.White.copy(alpha = 0.85f), modifier = Modifier.align(Alignment.BottomStart).padding(4.dp))
                    }
                }
            }
            FloatingActionButton(
                onClick = { add.launch("image/*") },
                containerColor = Palette.Accent,
                modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp),
            ) { Text("+", fontSize = 24.sp) }
        }
    }
}

// The picture: its file when stored here, else the hub's copy (a preview in the grid, the whole photo full
// screen, over the preview while it arrives). Its title when neither is there.
@Composable
private fun Thumb(p: Photo, hash: String?, isHere: Boolean, modifier: Modifier, full: Boolean) {
    val context = LocalContext.current
    val maxSide = if (full) 2048 else 360
    when {
        isHere -> LocalImage(p.ImagePath, modifier, maxSide = maxSide, crop = !full)
        hash != null -> {
            val preview = "hub-thumb:$hash"
            if (full) {
                FileImage("hub:$hash", modifier, maxSide, crop = false, placeholder = cachedImage(preview, 360)) { HubPhotos.original(context, p, hash) }
            } else {
                FileImage(preview, modifier, 360, crop = true) { HubPhotos.thumb(context, hash) }
            }
        }
        else -> Box(modifier, contentAlignment = Alignment.Center) {
            Text(p.Title.ifBlank { "Photo pas encore sur le serveur" }, fontSize = 11.sp, color = Palette.TextFaint, textAlign = TextAlign.Center, modifier = Modifier.padding(6.dp), maxLines = 3, overflow = TextOverflow.Ellipsis)
        }
    }
}

@Composable
private fun Viewer(
    db: AkashicDatabase, photos: List<Photo>, start: Int, state: HubPhotos.State, albumNames: Map<Int, String>,
    onChanged: () -> Unit, onClose: () -> Unit,
) {
    BackHandler(onBack = onClose)
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    // Kept here, by key: changes show at once, the gallery reloads on close.
    var kept by remember { mutableStateOf(state.kept) }
    var page by remember { mutableStateOf(start.coerceIn(0, (photos.size - 1).coerceAtLeast(0))) }
    // Local copy, so ★ and deletions show at once.
    var list by remember { mutableStateOf(photos) }
    var confirm by remember { mutableStateOf(false) }
    val paired = remember { SyncService.credentials(context) != null }
    var upscaling by remember { mutableStateOf(false) }
    var note by remember { mutableStateOf<String?>(null) } // the answer to « ×4 », under the photo
    var menu by remember { mutableStateOf(false) }
    var editing by remember { mutableStateOf<Photo?>(null) }
    var reverting by remember { mutableStateOf(false) }
    // A new path is a new file: the viewer and the gallery's image caches (keyed by path) show it at once.
    fun replacePath(id: Int, path: String) {
        list = list.map { if (it.Id == id) it.copy(ImagePath = path) else it }
        onChanged()
    }
    LaunchedEffect(page) { note = null }
    if (list.isEmpty()) { onClose(); return }
    val current = list[page.coerceIn(0, list.lastIndex)]
    editing?.let { p ->
        ImageEditor(
            table = "Photo", id = p.Id, currentPath = p.ImagePath,
            // The whole photo: the file kept here, else the hub's copy the viewer opens (fetched if needed).
            source = { HubPhotos.original(context, p, state.hashes[HubPhotos.key(p)]) },
            onClose = { editing = null },
            onChanged = { path -> replacePath(p.Id, path) },
        )
    }

    Column(Modifier.fillMaxSize().background(Color(0xFF07070A))) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 4.dp)) {
            TextButton(onClick = onClose) { Text("← Photos") }
            Spacer(Modifier.weight(1f))
            Text("${page + 1} / ${list.size}", fontSize = 12.sp, color = Palette.TextMuted)
            val k = HubPhotos.key(current)
            val hash = state.hashes[k]
            // (A kept album's photos are marked kept one by one at the next sync, see FileSync.applyKeptLists.)
            val isKept = k in kept
            // Kept here (⤓ filled) or only on the server (☁). A photo not on the server yet stays on this phone.
            if (hash != null) TextButton(onClick = {
                val keep = !isKept
                kept = if (keep) kept + k else kept - k
                scope.launch { withContext(Dispatchers.IO) { HubPhotos.setKept(context, db, current, keep, hash) }; onChanged() }
            }) { Text(if (isKept) "✓ Gardée" else "⤓ Garder", fontSize = 13.sp, color = if (isKept) Palette.Accent else Palette.Text) }
            // Upscale ×4 on the PC or the hub: the result is added to the same album, here at the next sync.
            if (paired) TextButton(enabled = !upscaling, onClick = {
                val id = current.Id
                upscaling = true
                note = "Envoi pour agrandissement…"
                scope.launch { note = HubCompute.upscaleNote(context, "Photo", id.toLong()); upscaling = false }
            }) { Text("×4", fontSize = 13.sp) }
            // ⋮ : « Modifier » (rotate, crop, contrast, saturation) and, after an edit, « Revenir à l'original ».
            Box {
                TextButton(enabled = !reverting, onClick = { menu = true }) { Text("⋮", fontSize = 18.sp) }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(text = { Text("Modifier") }, onClick = { menu = false; editing = current })
                    if (ImageEdits.isEdited(current.ImagePath)) DropdownMenuItem(text = { Text("Revenir à l'original") }, onClick = {
                        menu = false
                        val p = current
                        reverting = true
                        scope.launch {
                            ImageEdits.revert(context, "Photo", p.Id, p.ImagePath)
                                .onSuccess { path -> replacePath(p.Id, path); note = "Original rétabli." }
                                .onFailure { note = it.message }
                            reverting = false
                        }
                    })
                }
            }
            TextButton(onClick = {
                val updated = current.copy(IsFavorite = !current.IsFavorite)
                list = list.map { if (it.Id == updated.Id) updated else it }
                scope.launch { withContext(Dispatchers.IO) { db.photoDao().update(updated) }; onChanged() }
            }) { Text(if (current.IsFavorite) "★" else "☆", fontSize = 22.sp, color = if (current.IsFavorite) Color(0xFFF2C14E) else Palette.Text) }
            TextButton(onClick = { confirm = true }) { Text("🗑") }
        }
        SwipePages(count = list.size, page = page, onPageChange = { page = it }, modifier = Modifier.fillMaxWidth().weight(1f), verticalAlignment = Alignment.CenterVertically) { i ->
            val p = list[i]
            Thumb(p, state.hashes[HubPhotos.key(p)], remember(p.Id, p.ImagePath, kept) { HubPhotos.isHere(context, p) }, Modifier.fillMaxSize(), full = true)
        }
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            note?.let { Text(it, fontSize = 12.sp, color = Palette.Accent, modifier = Modifier.padding(bottom = 4.dp)) }
            if (current.Title.isNotBlank()) Text(current.Title, fontSize = 16.sp)
            val date = current.TakenDate?.let(::datePart)
            if (date != null) Text(frenchDate(date), fontSize = 12.sp, color = Palette.TextMuted)
            if (current.Description.isNotBlank()) Text(current.Description, fontSize = 13.sp, color = Palette.TextMuted, modifier = Modifier.padding(top = 4.dp))
        }
    }
    if (confirm) ConfirmDelete("cette photo", onConfirm = {
        confirm = false
        val gone = current
        list = list.filter { it.Id != gone.Id }
        page = page.coerceAtMost((list.size - 1).coerceAtLeast(0))
        scope.launch { withContext(Dispatchers.IO) { db.photoDao().deleteById(gone.Id) }; onChanged() }
    }, onDismiss = { confirm = false })
}
