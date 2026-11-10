package com.akashicrecords.reading

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.rememberTransformableState
import androidx.compose.foundation.gestures.transformable
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.SwipePages
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Book
import com.akashicrecords.data.ConfigStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

// Opens a book in the reader that fits its format: text (epub) or pages (PDF, comic).
@Composable
fun BookReader(db: AkashicDatabase, config: ConfigStore, bookId: Int, onClose: () -> Unit) {
    val context = LocalContext.current
    val book by produceState<Book?>(null, bookId) {
        value = withContext(Dispatchers.IO) { db.bookDao().getById(bookId) }
        if (value == null) onClose()
    }
    val b = book ?: return
    if (BookFormat.of(ReadingStore.fileOf(context, b)) == BookFormat.EPUB) ReaderScreen(db, config, bookId, onClose)
    else PageReader(db, b, onClose)
}

// Scanned PDF or comic: one page per screen, the app's swipe to turn pages, pinch (or double tap) to zoom into a
// panel and drag around it, a tap to show or hide the bars. The page where reading stopped is remembered.
// No highlights here: a scanned page has no text to select.
@Composable
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class) // transformable(canPan = …)
private fun PageReader(db: AkashicDatabase, book: Book, onClose: () -> Unit) {
    val context = LocalContext.current
    var doc by remember { mutableStateOf<PageDoc?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var page by remember { mutableStateOf(ReadingStore.parseLocator(book.Locator).first) }
    var chrome by remember { mutableStateOf(true) }
    var size by remember { mutableStateOf(IntSize.Zero) }
    // Zoom of the page on screen (1 = whole page). While zoomed, a drag pans the page instead of turning it.
    var scale by remember { mutableStateOf(1f) }
    var offset by remember { mutableStateOf(Offset.Zero) }

    LaunchedEffect(book.Id) {
        doc = withContext(Dispatchers.IO) { runCatching { PageDoc.open(ReadingStore.fileOf(context, book)) }.getOrElse { e -> error = e.message; null } }
        doc?.let { page = page.coerceIn(0, (it.pageCount - 1).coerceAtLeast(0)) }
    }
    DisposableEffect(Unit) { onDispose { doc?.close() } }

    val d = doc
    LaunchedEffect(page, d) {
        if (d == null) return@LaunchedEffect
        scale = 1f; offset = Offset.Zero
        delay(500)
        withContext(Dispatchers.IO) { ReadingStore.savePosition(db, book.Id, page, 0.0, d.pageCount) }
    }
    BackHandler { if (scale > 1f) { scale = 1f; offset = Offset.Zero } else onClose() }

    fun clamp(o: Offset, s: Float): Offset {
        val maxX = (s - 1f) * size.width / 2f
        val maxY = (s - 1f) * size.height / 2f
        return Offset(o.x.coerceIn(-maxX, maxX), o.y.coerceIn(-maxY, maxY))
    }
    val zoom = rememberTransformableState { zoomChange, pan, _ ->
        val s = (scale * zoomChange).coerceIn(1f, 5f)
        scale = s
        offset = if (s <= 1f) Offset.Zero else clamp(offset + pan * s, s)
    }

    Box(Modifier.fillMaxSize().background(Color(0xFF0E0E12)).onSizeChanged { size = it }) {
        when {
            error != null -> Column(Modifier.padding(16.dp)) {
                Text("Impossible d'ouvrir ce livre : $error", color = Palette.TextMuted)
                TextButton(onClick = onClose) { Text("← Bibliothèque") }
            }
            d == null || size == IntSize.Zero -> {}
            else -> SwipePages(
                count = d.pageCount,
                page = page,
                onPageChange = { page = it },
                modifier = Modifier.fillMaxSize()
                    // Two fingers zoom; one finger pans only once zoomed (otherwise it turns the page).
                    .transformable(zoom, canPan = { scale > 1.01f })
                    .pointerInput(Unit) {
                        detectTapGestures(
                            onTap = { chrome = !chrome },
                            onDoubleTap = { at ->
                                if (scale > 1f) { scale = 1f; offset = Offset.Zero }
                                else { scale = 2.5f; offset = clamp(Offset(size.width / 2f - at.x, size.height / 2f - at.y) * 2.5f, 2.5f) }
                            },
                        )
                    },
                verticalAlignment = Alignment.CenterVertically,
                userScrollEnabled = scale <= 1.01f,
            ) { p ->
                // Rendered a bit larger than the screen so a zoomed panel stays sharp enough.
                val width = (size.width * 1.5f).toInt()
                val bitmap by produceState<android.graphics.Bitmap?>(null, p, width) { value = d.render(p, width) }
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    val bmp = bitmap
                    if (bmp == null) CircularProgressIndicator(color = Palette.Accent)
                    else Image(
                        bmp.asImageBitmap(), contentDescription = "Page ${p + 1}",
                        contentScale = ContentScale.Fit,
                        modifier = Modifier.fillMaxSize().graphicsLayer {
                            if (p == page) { scaleX = scale; scaleY = scale; translationX = offset.x; translationY = offset.y }
                        },
                    )
                }
            }
        }

        if (chrome && d != null) {
            Row(
                Modifier.fillMaxWidth().align(Alignment.TopCenter).background(Palette.Surface1.copy(alpha = 0.94f)).padding(horizontal = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                TextButton(onClick = onClose) { Text("←") }
                Text(book.Title, fontSize = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            }
            Column(Modifier.fillMaxWidth().align(Alignment.BottomCenter).background(Palette.Surface1.copy(alpha = 0.94f)).padding(horizontal = 16.dp, vertical = 8.dp)) {
                val overall = if (d.pageCount > 1) page.toFloat() / (d.pageCount - 1) else 1f
                LinearProgressIndicator(progress = { overall }, Modifier.fillMaxWidth().height(3.dp), color = Palette.Accent)
                Text(
                    "Page ${page + 1}/${d.pageCount} · ${(overall * 100).toInt()} % · double tap ou deux doigts pour zoomer",
                    fontSize = 11.sp, color = Palette.TextMuted, modifier = Modifier.padding(top = 4.dp),
                )
            }
        }
    }
}
