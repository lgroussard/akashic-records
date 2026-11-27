package com.akashicrecords.references

import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import android.widget.Toast
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.staggeredgrid.LazyVerticalStaggeredGrid
import androidx.compose.foundation.lazy.staggeredgrid.StaggeredGridCells
import androidx.compose.foundation.lazy.staggeredgrid.StaggeredGridItemSpan
import androidx.compose.foundation.lazy.staggeredgrid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.RefBoard
import com.akashicrecords.data.RefImage
import com.akashicrecords.journaux.ConfirmDelete
import com.akashicrecords.journaux.ObservationsBlock
import com.akashicrecords.journaux.REF_IMAGES_SHARED
import com.akashicrecords.journaux.nowIso
import com.akashicrecords.media.ImageEditor
import com.akashicrecords.media.LocalImage
import com.akashicrecords.media.MediaFiles
import com.akashicrecords.media.ZoomableBitmap
import com.akashicrecords.media.ZoomableLocalImage
import com.akashicrecords.media.rememberZoomState
import com.akashicrecords.music.RemoteImages
import com.akashicrecords.sync.HubCompute
import com.akashicrecords.sync.SyncService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private const val ALL = -1
private const val UNSORTED = -2

// Journaux → Références: image boards in place of Pinterest. Images come from the search (Wikimedia Commons,
// Cleveland Museum of Art) or from any app's Share → Akashic. Not filed yet = « À trier ».
@Composable
fun ReferencesJournal(db: AkashicDatabase) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var boards by remember { mutableStateOf<List<RefBoard>>(emptyList()) }
    var images by remember { mutableStateOf<List<RefImage>>(emptyList()) }
    var loaded by remember { mutableStateOf(false) } // until the first load, no "nothing here yet" message
    var filter by rememberSaveable { mutableStateOf(ALL) }
    var version by remember { mutableStateOf(0) }
    var query by rememberSaveable { mutableStateOf("") }
    var searchKey by rememberSaveable { mutableStateOf(0) } // 0 = boards, else one search run
    var viewing by remember { mutableStateOf<RefImage?>(null) }
    var naming by remember { mutableStateOf<RefBoard?>(null) } // new (Id 0) or renamed board
    var deleting by remember { mutableStateOf<RefBoard?>(null) }

    LaunchedEffect(version, ReferenceStore.changes) {
        withContext(Dispatchers.IO) {
            boards = db.refBoardDao().getAll()
            images = db.refImageDao().getAll()
            loaded = true
        }
        if (filter >= 0 && boards.none { it.Id == filter }) filter = ALL
    }

    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            singleLine = true,
            placeholder = { Text("Chercher des images…") },
            trailingIcon = {
                if (searchKey != 0) TextButton(onClick = { searchKey = 0 }) { Text("✕") }
            },
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
            keyboardActions = KeyboardActions(onSearch = { if (query.isNotBlank()) searchKey++ }),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp),
        )

        if (searchKey != 0) {
            SearchResults(db, query.trim(), searchKey, boards, onChanged = { version++ })
            return@Column
        }

        Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 6.dp)) {
            FilterChip(selected = filter == ALL, onClick = { filter = ALL }, label = { Text("Tout · ${images.size}") })
            Spacer(Modifier.width(6.dp))
            FilterChip(selected = filter == UNSORTED, onClick = { filter = UNSORTED }, label = { Text("À trier · ${images.count { it.BoardId == null }}") })
            boards.forEach { b ->
                Spacer(Modifier.width(6.dp))
                FilterChip(selected = filter == b.Id, onClick = { filter = b.Id }, label = { Text("${b.Title} · ${images.count { it.BoardId == b.Id }}") })
            }
            Spacer(Modifier.width(6.dp))
            TextButton(onClick = { naming = RefBoard(Title = "", CreatedAt = nowIso()) }) { Text("+ Tableau") }
        }
        boards.firstOrNull { it.Id == filter }?.let { b ->
            Row(Modifier.padding(horizontal = 4.dp)) {
                TextButton(onClick = { naming = b }) { Text("Renommer", fontSize = 12.sp) }
                TextButton(onClick = { deleting = b }) { Text("Supprimer le tableau", fontSize = 12.sp, color = Palette.TextMuted) }
            }
        }

        val shown = when (filter) {
            ALL -> images
            UNSORTED -> images.filter { it.BoardId == null }
            else -> images.filter { it.BoardId == filter }
        }
        if (!loaded) {
            Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Palette.Accent) }
        } else if (shown.isEmpty()) {
            Text(
                if (images.isEmpty()) "Aucune image pour l'instant. Cherche-en ci-dessus, ou partage une image ou un lien " +
                    "vers Akashic depuis Pinterest, Instagram ou ton navigateur."
                else "Rien dans ce tableau.",
                color = Palette.TextMuted, fontSize = 14.sp, modifier = Modifier.padding(16.dp),
            )
        } else {
            LazyVerticalStaggeredGrid(
                columns = StaggeredGridCells.Fixed(2),
                verticalItemSpacing = 6.dp,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                contentPadding = PaddingValues(8.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                items(shown, key = { it.Id }) { img ->
                    LocalImage(
                        img.ImagePath,
                        Modifier.fillMaxWidth().aspectRatio(ratio(img.Width, img.Height)).clip(RoundedCornerShape(8.dp))
                            .clickable { viewing = img },
                        maxSide = 600,
                    )
                }
            }
        }
    }

    viewing?.let { img ->
        ImageViewer(
            db, img, boards,
            onMove = { board ->
                scope.launch {
                    withContext(Dispatchers.IO) { db.refImageDao().update(img.copy(BoardId = board)) }
                    viewing = img.copy(BoardId = board)
                    version++
                }
            },
            onNewBoard = { naming = RefBoard(Title = "", CreatedAt = nowIso()) },
            onDelete = {
                scope.launch {
                    ReferenceStore.delete(context, db, img)
                    viewing = null
                    version++
                }
            },
            onEdited = {
                scope.launch {
                    viewing = withContext(Dispatchers.IO) { db.refImageDao().getById(img.Id) }
                    version++
                }
            },
            onClose = { viewing = null },
        )
    }
    naming?.let { b ->
        NameDialog(if (b.Id == 0) "Nouveau tableau" else "Renommer le tableau", b.Title, onDismiss = { naming = null }) { title ->
            naming = null
            scope.launch {
                withContext(Dispatchers.IO) {
                    if (b.Id == 0) db.refBoardDao().add(b.copy(Title = title)) else db.refBoardDao().update(b.copy(Title = title))
                }
                version++
            }
        }
    }
    deleting?.let { b ->
        ConfirmDelete("le tableau « ${b.Title} » (ses images vont dans À trier)", onConfirm = {
            deleting = null
            scope.launch {
                withContext(Dispatchers.IO) {
                    db.refImageDao().unfileBoard(b.Id)
                    db.refBoardDao().deleteById(b.Id)
                }
                filter = UNSORTED
                version++
            }
        }, onDismiss = { deleting = null })
    }
}

private fun ratio(w: Int, h: Int) = if (w > 0 && h > 0) (w.toFloat() / h).coerceIn(0.4f, 2.5f) else 1f

// ---- Online search ----

@Composable
private fun SearchResults(db: AkashicDatabase, query: String, key: Int, boards: List<RefBoard>, onChanged: () -> Unit) {
    val context = LocalContext.current
    var enabled by remember { mutableStateOf(ImageSearch.SOURCES.toSet()) }
    var hits by remember(key, enabled) { mutableStateOf<List<ImageHit>>(emptyList()) }
    var page by remember(key, enabled) { mutableStateOf(1) }
    var loading by remember { mutableStateOf(false) }
    var failed by remember(key, enabled) { mutableStateOf<List<String>>(emptyList()) }
    var exhausted by remember(key, enabled) { mutableStateOf(false) }
    var preview by remember { mutableStateOf<ImageHit?>(null) }
    var saved by remember(key) { mutableStateOf<Set<String>>(emptySet()) }

    LaunchedEffect(key, enabled, page) {
        if (enabled.isEmpty()) return@LaunchedEffect
        loading = true
        val results = coroutineScope {
            enabled.map { s -> async { s to runCatching { ImageSearch.search(s, query, page) } } }.awaitAll()
        }
        failed = results.filter { it.second.isFailure }.map { it.first }
        val lists = results.mapNotNull { it.second.getOrNull() }
        // Interleaved, so the first screen shows every source rather than 24 images from the first one.
        val merged = buildList { for (i in 0 until (lists.maxOfOrNull { it.size } ?: 0)) lists.forEach { l -> l.getOrNull(i)?.let(::add) } }
        exhausted = merged.isEmpty()
        hits = (hits + merged).distinctBy { it.fullUrl }
        loading = false
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 6.dp)) {
            ImageSearch.SOURCES.forEach { s ->
                FilterChip(
                    selected = s in enabled,
                    onClick = { enabled = if (s in enabled) enabled - s else enabled + s },
                    label = { Text(s, fontSize = 12.sp) },
                )
                Spacer(Modifier.width(6.dp))
            }
        }
        if (failed.isNotEmpty()) {
            Text("Pas de réponse de : ${failed.joinToString()}", fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.padding(horizontal = 16.dp))
        }
        when {
            enabled.isEmpty() -> Text("Choisis au moins une source.", color = Palette.TextMuted, modifier = Modifier.padding(16.dp))
            hits.isEmpty() && loading -> Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = Palette.Accent)
            }
            hits.isEmpty() -> Text("Aucune image trouvée pour « $query ».", color = Palette.TextMuted, modifier = Modifier.padding(16.dp))
            else -> LazyVerticalStaggeredGrid(
                columns = StaggeredGridCells.Fixed(2),
                verticalItemSpacing = 6.dp,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                contentPadding = PaddingValues(8.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                items(hits, key = { it.fullUrl }) { hit ->
                    Box {
                        WebImage(hit.thumbUrl, ratio(hit.width, hit.height), Modifier.clip(RoundedCornerShape(8.dp)).clickable { preview = hit })
                        if (hit.fullUrl in saved) {
                            Text(
                                "✓", color = Color.White, fontSize = 13.sp,
                                modifier = Modifier.align(Alignment.TopEnd).padding(6.dp).background(Palette.Accent, RoundedCornerShape(10.dp))
                                    .padding(horizontal = 7.dp, vertical = 1.dp),
                            )
                        }
                    }
                }
                item(span = StaggeredGridItemSpan.FullLine) {
                    Box(Modifier.fillMaxWidth().padding(12.dp), contentAlignment = Alignment.Center) {
                        when {
                            loading -> CircularProgressIndicator(color = Palette.Accent)
                            !exhausted -> TextButton(onClick = { page++ }) { Text("Plus de résultats") }
                        }
                    }
                }
            }
        }
    }

    preview?.let { hit ->
        var board by remember(hit) { mutableStateOf<Int?>(null) }
        var naming by remember(hit) { mutableStateOf(false) }
        var saving by remember(hit) { mutableStateOf(false) }
        val scope = rememberCoroutineScope()
        Dialog(onDismissRequest = { preview = null }, properties = DialogProperties(usePlatformDefaultWidth = false)) {
            Column(Modifier.fillMaxWidth(0.94f).clip(RoundedCornerShape(12.dp)).background(Palette.Surface1).padding(12.dp)) {
                ZoomableWebImage(hit.thumbUrl, ratio(hit.width, hit.height), Modifier.heightIn(max = 420.dp).clip(RoundedCornerShape(8.dp)))
                Spacer(Modifier.height(8.dp))
                if (hit.title.isNotBlank()) Text(hit.title, fontSize = 15.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(listOf(hit.author, hit.source).filter { it.isNotBlank() }.joinToString(" · "), fontSize = 12.sp, color = Palette.TextMuted)
                BoardMenu(boards, board, onPick = { board = it }, onNewBoard = { naming = true })
                Row(verticalAlignment = Alignment.CenterVertically) {
                    hit.pageUrl?.let { url -> TextButton(onClick = { open(context, url) }) { Text("Source ↗") } }
                    Spacer(Modifier.weight(1f))
                    TextButton(onClick = { preview = null }) { Text("Fermer") }
                    Button(enabled = !saving, onClick = {
                        saving = true
                        scope.launch {
                            val ok = runCatching { ReferenceStore.saveHit(context, db, hit, board) }
                            saving = false
                            ok.onSuccess { saved = saved + hit.fullUrl; preview = null; onChanged() }
                                .onFailure { Toast.makeText(context, "Échec : ${it.message}", Toast.LENGTH_LONG).show() }
                        }
                    }) { Text(if (saving) "…" else "Enregistrer") }
                }
            }
        }
        if (naming) NewBoardDialog(db, onDismiss = { naming = false }) { id -> naming = false; board = id; onChanged() }
    }
}

// ---- Viewer of a saved image ----

@Composable
private fun ImageViewer(
    db: AkashicDatabase, img: RefImage, boards: List<RefBoard>, onMove: (Int?) -> Unit, onNewBoard: () -> Unit, onDelete: () -> Unit,
    onEdited: () -> Unit, onClose: () -> Unit,
) {
    val context = LocalContext.current
    var confirm by remember { mutableStateOf(false) }
    var editing by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val paired = remember { SyncService.credentials(context) != null }
    var upscaling by remember { mutableStateOf(false) }
    var note by remember(img.Id) { mutableStateOf<String?>(null) } // the answer to « Agrandir ×4 »
    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Color(0xF00E0E12)).padding(12.dp)) {
            // Pinch / double tap to zoom (media/Zoomable.kt); a single tap closes, as before, unless zoomed in.
            val zoom = rememberZoomState(img.Id, img.ImagePath)
            ZoomableLocalImage(img.ImagePath, zoom, Modifier.weight(1f).fillMaxWidth(), onTap = { if (!zoom.isZoomed) onClose() })
            // Bounded, so a long list of observations scrolls here instead of squeezing the image out.
            Column(Modifier.fillMaxWidth().heightIn(max = 300.dp).verticalScroll(rememberScrollState())) {
                if (img.Title.isNotBlank()) Text(img.Title, fontSize = 15.sp, color = Palette.Text)
                Text(listOf(img.Author, img.Source).filter { it.isNotBlank() }.joinToString(" · "), fontSize = 12.sp, color = Palette.TextMuted)
                // Read-only, and only once the PC and the phone hold the same reference images (see REF_IMAGES_SHARED).
                if (REF_IMAGES_SHARED) ObservationsBlock(db, "RefImage", img.Id)
                BoardMenu(boards, img.BoardId, onPick = onMove, onNewBoard = onNewBoard)
                // Wraps onto a second line when the buttons do not fit the width.
                @OptIn(ExperimentalLayoutApi::class)
                FlowRow {
                    (img.PageUrl ?: img.SourceUrl)?.let { url -> TextButton(onClick = { open(context, url) }) { Text("Source ↗") } }
                    // Upscale ×4 on the PC or the hub: the result joins the same board, here at the next sync.
                    if (paired) TextButton(enabled = !upscaling, onClick = {
                        val id = img.Id
                        upscaling = true
                        note = "Envoi pour agrandissement…"
                        scope.launch { note = HubCompute.upscaleNote(context, "RefImage", id.toLong()); upscaling = false }
                    }) { Text("Agrandir ×4") }
                    // Rotate, crop, contrast, saturation; the editor also offers « Revenir à l'original » after an edit.
                    TextButton(onClick = { editing = true }) { Text("Modifier") }
                    TextButton(onClick = { confirm = true }) { Text("Supprimer", color = Palette.TextMuted) }
                    TextButton(onClick = onClose) { Text("Fermer") }
                }
                note?.let { Text(it, fontSize = 12.sp, color = Palette.Accent) }
            }
        }
    }
    if (confirm) ConfirmDelete("cette image", onConfirm = { confirm = false; onDelete() }, onDismiss = { confirm = false })
    if (editing) ImageEditor(
        table = "RefImage", id = img.Id, currentPath = img.ImagePath,
        source = { MediaFiles.resolve(context, img.ImagePath) },
        onClose = { editing = false },
        onChanged = { onEdited() },
    )
}

// ---- Share → Akashic ----

// What another app shared: an image (uri) or a text holding a link.
data class SharedRef(val uri: Uri?, val text: String?, val subject: String?) {
    val link: String? get() = text?.let { Regex("https?://\\S+").find(it)?.value?.trimEnd('.', ',', ')', '"') }

    companion object {
        fun from(intent: Intent?): SharedRef? {
            if (intent?.action != Intent.ACTION_SEND) return null
            @Suppress("DEPRECATION")
            val uri = intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)
            val text = intent.getStringExtra(Intent.EXTRA_TEXT)
            if (uri == null && text.isNullOrBlank()) return null
            return SharedRef(uri, text, intent.getStringExtra(Intent.EXTRA_SUBJECT) ?: intent.getStringExtra(Intent.EXTRA_TITLE))
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ShareToReferencesSheet(db: AkashicDatabase, share: SharedRef, onDone: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var boards by remember { mutableStateOf<List<RefBoard>>(emptyList()) }
    var board by remember { mutableStateOf<Int?>(null) }
    var title by remember { mutableStateOf(share.subject.orEmpty()) }
    var naming by remember { mutableStateOf(false) }
    var saving by remember { mutableStateOf(false) }
    var boardsVersion by remember { mutableStateOf(0) }
    // For a link: the image found on the page (null while looking, or when there is none).
    var link by remember { mutableStateOf<ReferenceStore.LinkImage?>(null) }
    var linkState by remember { mutableStateOf(if (share.uri != null) "image" else "looking") }

    LaunchedEffect(boardsVersion) { boards = withContext(Dispatchers.IO) { db.refBoardDao().getAll() } }
    LaunchedEffect(share) {
        if (share.uri != null) return@LaunchedEffect
        val url = share.link
        if (url == null) { linkState = "none"; return@LaunchedEffect }
        link = runCatching { ReferenceStore.resolveLink(url) }.getOrNull()
        linkState = if (link != null) "found" else "none"
        link?.title?.takeIf { it.isNotBlank() && title.isBlank() }?.let { title = it }
    }

    ModalBottomSheet(onDismissRequest = onDone, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Palette.Surface1) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
            Text("Ajouter aux références", fontSize = 18.sp)
            Spacer(Modifier.height(8.dp))
            when (linkState) {
                "image" -> SharedPreview(share.uri!!)
                "found" -> WebImage(link!!.imageUrl, 1f, Modifier.heightIn(max = 260.dp).clip(RoundedCornerShape(8.dp)), fit = true)
                "looking" -> Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(20.dp), color = Palette.Accent, strokeWidth = 2.dp)
                    Spacer(Modifier.width(8.dp))
                    Text("Recherche de l'image sur la page…", color = Palette.TextMuted)
                }
                else -> Text(
                    if (share.link == null) "Rien d'utilisable dans ce partage (ni image, ni lien)."
                    else "Pas d'image trouvée sur cette page. Partage plutôt l'image elle-même (appui long dessus → Partager).",
                    color = Palette.TextMuted,
                )
            }
            Spacer(Modifier.height(8.dp))
            OutlinedTextField(title, { title = it }, label = { Text("Titre") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            BoardMenu(boards, board, onPick = { board = it }, onNewBoard = { naming = true })
            Row {
                Spacer(Modifier.weight(1f))
                TextButton(onClick = onDone) { Text("Annuler") }
                Button(enabled = !saving && (linkState == "image" || linkState == "found"), onClick = {
                    saving = true
                    scope.launch {
                        val r = runCatching {
                            if (share.uri != null) ReferenceStore.saveShared(context, db, share.uri, title, board)
                            else link!!.let { ReferenceStore.saveUrl(context, db, it.imageUrl, it.pageUrl, title, "", "Partage", board) }
                        }
                        saving = false
                        r.onSuccess { Toast.makeText(context, "Ajoutée aux références", Toast.LENGTH_SHORT).show(); onDone() }
                            .onFailure { Toast.makeText(context, "Échec : ${it.message}", Toast.LENGTH_LONG).show() }
                    }
                }) { Text(if (saving) "…" else "Enregistrer") }
            }
        }
    }
    if (naming) NewBoardDialog(db, onDismiss = { naming = false }) { id -> naming = false; board = id; boardsVersion++ }
}

@Composable
private fun SharedPreview(uri: Uri) {
    val context = LocalContext.current
    val bitmap by produceState<Bitmap?>(null, uri) {
        value = withContext(Dispatchers.IO) {
            runCatching {
                val r = context.contentResolver
                val bounds = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
                r.openInputStream(uri)?.use { android.graphics.BitmapFactory.decodeStream(it, null, bounds) }
                var sample = 1
                while (bounds.outWidth / (sample * 2) >= 1024 || bounds.outHeight / (sample * 2) >= 1024) sample *= 2
                r.openInputStream(uri)?.use { android.graphics.BitmapFactory.decodeStream(it, null, android.graphics.BitmapFactory.Options().apply { inSampleSize = sample }) }
            }.getOrNull()
        }
    }
    val b = bitmap
    if (b != null) Image(b.asImageBitmap(), null, contentScale = ContentScale.Fit, modifier = Modifier.fillMaxWidth().heightIn(max = 320.dp).clip(RoundedCornerShape(8.dp)))
    else Box(Modifier.fillMaxWidth().height(160.dp).background(Palette.Surface3))
}

// ---- Small shared pieces ----

// A web image whose tile takes the picture's own shape once loaded when the source gave no size.
@Composable
private fun WebImage(url: String, knownRatio: Float, modifier: Modifier, fit: Boolean = false) {
    val bitmap by produceState(RemoteImages.cached(url), url) { if (value == null) value = RemoteImages.load(url) }
    val b = bitmap
    val r = if (b != null && b.height > 0) (b.width.toFloat() / b.height).coerceIn(0.4f, 2.5f) else knownRatio
    if (b != null) Image(
        b.asImageBitmap(), null,
        contentScale = if (fit) ContentScale.Fit else ContentScale.Crop,
        modifier = Modifier.fillMaxWidth().aspectRatio(r).then(modifier),
    ) else Box(Modifier.fillMaxWidth().aspectRatio(r).then(modifier).background(Palette.Surface3))
}

// The search preview, zoomable inside its frame (pinch / double tap, media/Zoomable.kt).
@Composable
private fun ZoomableWebImage(url: String, knownRatio: Float, modifier: Modifier) {
    val bitmap by produceState(RemoteImages.cached(url), url) { if (value == null) value = RemoteImages.load(url) }
    val b = bitmap
    val r = if (b != null && b.height > 0) (b.width.toFloat() / b.height).coerceIn(0.4f, 2.5f) else knownRatio
    val image = remember(b) { b?.asImageBitmap() }
    val zoom = rememberZoomState(url)
    ZoomableBitmap(image, zoom, Modifier.fillMaxWidth().aspectRatio(r).then(modifier)) {
        Box(Modifier.fillMaxSize().background(Palette.Surface3))
    }
}

@Composable
private fun BoardMenu(boards: List<RefBoard>, current: Int?, onPick: (Int?) -> Unit, onNewBoard: () -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        TextButton(onClick = { open = true }) {
            Text("Tableau : " + (boards.firstOrNull { it.Id == current }?.Title ?: "À trier") + " ▾", fontSize = 13.sp)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            DropdownMenuItem(text = { Text("À trier") }, onClick = { open = false; onPick(null) })
            boards.forEach { b -> DropdownMenuItem(text = { Text(b.Title) }, onClick = { open = false; onPick(b.Id) }) }
            DropdownMenuItem(text = { Text("+ Nouveau tableau…", color = Palette.Accent) }, onClick = { open = false; onNewBoard() })
        }
    }
}

@Composable
private fun NewBoardDialog(db: AkashicDatabase, onDismiss: () -> Unit, onCreated: (Int) -> Unit) {
    val scope = rememberCoroutineScope()
    NameDialog("Nouveau tableau", "", onDismiss) { title ->
        scope.launch {
            val id = withContext(Dispatchers.IO) { db.refBoardDao().add(RefBoard(Title = title, CreatedAt = nowIso())).toInt() }
            onCreated(id)
        }
    }
}

@Composable
private fun NameDialog(heading: String, initial: String, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var name by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = Palette.Surface1,
        title = { Text(heading) },
        text = { OutlinedTextField(name, { name = it }, singleLine = true, placeholder = { Text("Nom") }) },
        confirmButton = { TextButton(enabled = name.isNotBlank(), onClick = { onConfirm(name.trim()) }) { Text("OK") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Annuler") } },
    )
}

private fun open(context: android.content.Context, url: String) {
    runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
}
