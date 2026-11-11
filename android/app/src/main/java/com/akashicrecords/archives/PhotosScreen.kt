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
import com.akashicrecords.media.LocalImage
import com.akashicrecords.media.MediaFiles
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

    LaunchedEffect(version) {
        withContext(Dispatchers.IO) {
            photos = db.photoDao().getAll().sortedByDescending { it.TakenDate ?: it.ImportedAt }
            albums = db.photoAlbumDao().getAll()
        }
    }
    val shown = when (filter) {
        ALL -> photos
        FAVORITES -> photos.filter { it.IsFavorite }
        else -> photos.filter { it.AlbumId == filter }
    }
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
        Viewer(db, shown, start, onChanged = { version++ }, onClose = { viewing = null })
        return
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 4.dp)) {
            FilterChip(selected = filter == ALL, onClick = { filter = ALL }, label = { Text("Tout · ${photos.size}") })
            Spacer(Modifier.width(6.dp))
            FilterChip(selected = filter == FAVORITES, onClick = { filter = FAVORITES }, label = { Text("★ Favoris") })
            albums.forEach { a ->
                Spacer(Modifier.width(6.dp))
                FilterChip(selected = filter == a.Id, onClick = { filter = a.Id }, label = { Text(a.Name) })
            }
        }
        Box(Modifier.fillMaxSize()) {
            if (shown.isEmpty()) {
                Text(
                    if (photos.isEmpty()) "Aucune photo. Touche + pour en ajouter depuis le téléphone, ou importe celles du PC (Plus → Réglages)."
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
                        Thumb(p, Modifier.fillMaxSize(), maxSide = 360, crop = true)
                        if (p.IsFavorite) Text("★", color = Color(0xFFF2C14E), modifier = Modifier.align(Alignment.TopEnd).padding(4.dp))
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

// The picture, or its title when the file is not on this phone (imported without its media).
@Composable
private fun Thumb(p: Photo, modifier: Modifier, maxSide: Int, crop: Boolean) {
    val context = LocalContext.current
    val present = remember(p.ImagePath) { MediaFiles.resolve(context, p.ImagePath)?.isFile == true }
    if (present) LocalImage(p.ImagePath, modifier, maxSide = maxSide, crop = crop)
    else Box(modifier, contentAlignment = Alignment.Center) {
        Text(p.Title.ifBlank { "Photo absente" }, fontSize = 11.sp, color = Palette.TextFaint, textAlign = TextAlign.Center, modifier = Modifier.padding(6.dp), maxLines = 3, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun Viewer(db: AkashicDatabase, photos: List<Photo>, start: Int, onChanged: () -> Unit, onClose: () -> Unit) {
    BackHandler(onBack = onClose)
    val scope = rememberCoroutineScope()
    var page by remember { mutableStateOf(start.coerceIn(0, (photos.size - 1).coerceAtLeast(0))) }
    // Local copy, so ★ and deletions show at once.
    var list by remember { mutableStateOf(photos) }
    var confirm by remember { mutableStateOf(false) }
    if (list.isEmpty()) { onClose(); return }
    val current = list[page.coerceIn(0, list.lastIndex)]

    Column(Modifier.fillMaxSize().background(Color(0xFF07070A))) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 4.dp)) {
            TextButton(onClick = onClose) { Text("← Photos") }
            Spacer(Modifier.weight(1f))
            Text("${page + 1} / ${list.size}", fontSize = 12.sp, color = Palette.TextMuted)
            TextButton(onClick = {
                val updated = current.copy(IsFavorite = !current.IsFavorite)
                list = list.map { if (it.Id == updated.Id) updated else it }
                scope.launch { withContext(Dispatchers.IO) { db.photoDao().update(updated) }; onChanged() }
            }) { Text(if (current.IsFavorite) "★" else "☆", fontSize = 22.sp, color = if (current.IsFavorite) Color(0xFFF2C14E) else Palette.Text) }
            TextButton(onClick = { confirm = true }) { Text("🗑") }
        }
        SwipePages(count = list.size, page = page, onPageChange = { page = it }, modifier = Modifier.fillMaxWidth().weight(1f), verticalAlignment = Alignment.CenterVertically) { i ->
            Thumb(list[i], Modifier.fillMaxSize(), maxSide = 2048, crop = false)
        }
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
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
