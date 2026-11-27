package com.akashicrecords.media

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateCentroid
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.PointerInputScope
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.math.max
import kotlin.math.min

// The one zoom of the app's large pictures (journal and canvas photos, Archives, Références, upscale results):
// two fingers zoom (1× = whole picture, up to 6×) around the fingers; once zoomed, one finger moves around, never
// further than the picture's edges; a double tap zooms ×2.5 on the tapped spot, or back to the whole picture.
// At 1× one finger is left alone, so a pager around the picture still swipes to the next one; zoomed, the drags
// are taken first (the pager does not move). Nothing is drawn over the picture.

private const val MAX_ZOOM = 6f
private const val DOUBLE_TAP_ZOOM = 2.5f
private const val ZOOMED = 1.01f
// Above this zoom a sharper decode of the file is loaded (see ZoomableFileImage).
private const val SHARPER_ABOVE = 1.5f
// The size the viewers decode at (at least this long side, see MediaFiles.decode); shares the cache with them.
internal const val VIEW_SIDE = 2048

@Stable
class ZoomState {
    var scale by mutableFloatStateOf(1f)
        private set
    var offset by mutableStateOf(Offset.Zero)
        private set
    /** True while zoomed in (a pager stops swiping then). Changes only when crossing 1×. */
    val isZoomed: Boolean by derivedStateOf { scale > ZOOMED }
    internal var box by mutableStateOf(IntSize.Zero)
    internal var content by mutableStateOf(IntSize.Zero) // the picture's own pixels, for its shape

    fun reset() {
        scale = 1f
        offset = Offset.Zero
    }

    // The picture's size on screen at 1× (fitted in the box).
    private fun fitted(): Size {
        val bw = box.width.toFloat()
        val bh = box.height.toFloat()
        if (content.width <= 0 || content.height <= 0 || bw <= 0f || bh <= 0f) return Size(bw, bh)
        val k = min(bw / content.width, bh / content.height)
        return Size(content.width * k, content.height * k)
    }

    // The scaled picture (around the box centre) moves at most by how much it overflows the box.
    private fun clamp(o: Offset, s: Float): Offset {
        val fit = fitted()
        val maxX = ((fit.width * s - box.width) / 2f).coerceAtLeast(0f)
        val maxY = ((fit.height * s - box.height) / 2f).coerceAtLeast(0f)
        return Offset(o.x.coerceIn(-maxX, maxX), o.y.coerceIn(-maxY, maxY))
    }

    private val centre get() = Offset(box.width / 2f, box.height / 2f)

    // Zooms by [zoom] keeping the point under [focus] (box coordinates) under it, then moves by [pan].
    internal fun transform(focus: Offset, zoom: Float, pan: Offset) {
        val target = (scale * zoom).coerceIn(1f, MAX_ZOOM)
        if (target <= ZOOMED) { reset(); return }
        val rel = focus - centre
        offset = clamp(rel - (rel - offset) * (target / scale) + pan, target)
        scale = target
    }

    // Whole picture ↔ ×2.5 with the tapped spot brought to the centre.
    internal fun toggle(at: Offset) {
        if (isZoomed) { reset(); return }
        offset = clamp(-(at - centre) * DOUBLE_TAP_ZOOM, DOUBLE_TAP_ZOOM)
        scale = DOUBLE_TAP_ZOOM
    }
}

@Composable
fun rememberZoomState(vararg keys: Any?): ZoomState = remember(*keys) { ZoomState() }

/**
 * The zoom of one page of a pager: back to 1× as soon as it is no longer the current page, and [onZoomed] told
 * (while it is the current one) whether it is zoomed, so the pager can stop swiping.
 */
@Composable
fun rememberPageZoomState(isCurrent: Boolean, onZoomed: (Boolean) -> Unit): ZoomState {
    val state = remember { ZoomState() }
    val report by rememberUpdatedState(onZoomed)
    LaunchedEffect(isCurrent) { if (!isCurrent) state.reset() }
    LaunchedEffect(isCurrent, state.isZoomed) { if (isCurrent) report(state.isZoomed) }
    return state
}

// Read on the Initial pass, before a pager or a scroll around: two fingers, or any drag once zoomed, are ours.
private suspend fun PointerInputScope.zoomGestures(state: ZoomState) = awaitEachGesture {
    awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
    do {
        val event = awaitPointerEvent(PointerEventPass.Initial)
        val fingers = event.changes.count { it.pressed }
        if (fingers >= 2 || state.isZoomed) {
            val zoom = if (fingers >= 2) event.calculateZoom() else 1f
            val pan = event.calculatePan()
            if (zoom != 1f || pan != Offset.Zero) state.transform(event.calculateCentroid(useCurrent = true), zoom, pan)
            event.changes.forEach { if (it.positionChanged()) it.consume() }
        }
    } while (event.changes.any { it.pressed })
}

/**
 * [image] fitted in [modifier]'s box, zoomable (see the top of this file). [onTap]: a single tap (it waits for the
 * double-tap delay). [empty] shows while there is no image.
 */
@Composable
fun ZoomableBitmap(
    image: ImageBitmap?,
    state: ZoomState,
    modifier: Modifier = Modifier,
    contentDescription: String? = null,
    onTap: (() -> Unit)? = null,
    empty: @Composable () -> Unit = {},
) {
    val tap by rememberUpdatedState(onTap)
    val hasTap = onTap != null
    if (image != null) SideEffect { state.content = IntSize(image.width, image.height) }
    Box(
        modifier.clipToBounds()
            .onSizeChanged { state.box = it }
            .pointerInput(state) { zoomGestures(state) }
            .pointerInput(state, hasTap) {
                detectTapGestures(
                    onTap = if (hasTap) { _ -> tap?.invoke() } else null,
                    onDoubleTap = { at -> state.toggle(at) },
                )
            },
        contentAlignment = Alignment.Center,
    ) {
        if (image != null) Image(
            image, contentDescription, contentScale = ContentScale.Fit,
            modifier = Modifier.fillMaxSize().graphicsLayer {
                scaleX = state.scale; scaleY = state.scale
                translationX = state.offset.x; translationY = state.offset.y
            },
        ) else empty()
    }
}

/**
 * A picture file shown large and zoomable: decoded like the other viewers (long side ≥ 2048 px when the file has
 * it, shared memory cache under [name]); past ×1.5 a sharper decode (up to ~16 Mpx, less on a small heap) replaces
 * it until back to 1×. [placeholder] shows while the file is read; "image absente" when there is none.
 */
@Composable
fun ZoomableFileImage(
    name: String,
    state: ZoomState,
    modifier: Modifier = Modifier,
    placeholder: ImageBitmap? = null,
    onTap: (() -> Unit)? = null,
    file: suspend () -> File?,
) {
    val source by rememberUpdatedState(file)
    var image by remember(name) { mutableStateOf(cachedImage(name, VIEW_SIDE)) }
    var missing by remember(name) { mutableStateOf(false) }
    LaunchedEffect(name) {
        image = loadCachedImage(name, VIEW_SIDE) { source() }
        missing = image == null
    }

    var sharp by remember(name) { mutableStateOf<ImageBitmap?>(null) }
    val wantSharp by remember(state) { derivedStateOf { state.scale > SHARPER_ABOVE } }
    val zoomed = state.isZoomed
    LaunchedEffect(name, wantSharp, zoomed, image) {
        if (!zoomed) { sharp = null; return@LaunchedEffect } // released once back to the whole picture
        val base = image ?: return@LaunchedEffect
        if (!wantSharp || sharp != null) return@LaunchedEffect
        sharp = withContext(Dispatchers.IO) {
            runCatching {
                val f = source()?.takeIf { it.isFile } ?: return@runCatching null
                // At most a sixth of the heap (4 bytes a pixel), and never more than 16 Mpx (64 MB).
                val budget = min(16L * 1024 * 1024, Runtime.getRuntime().maxMemory() / 6 / 4)
                MediaFiles.decodeWithin(f, budget, longerThan = max(base.width, base.height))?.asImageBitmap()
            }.getOrNull()
        }
    }

    ZoomableBitmap(sharp ?: image ?: placeholder, state, modifier, onTap = onTap) {
        Box(Modifier.fillMaxSize().background(Palette.Surface3), contentAlignment = Alignment.Center) {
            if (missing) Text("image absente", fontSize = 10.sp, color = Palette.TextFaint)
        }
    }
}

/** [ZoomableFileImage] of a media file stored under a desktop-style relative path (as [LocalImage]). */
@Composable
fun ZoomableLocalImage(
    relativePath: String?,
    state: ZoomState,
    modifier: Modifier = Modifier,
    onTap: (() -> Unit)? = null,
) {
    val context = LocalContext.current
    ZoomableFileImage("$relativePath", state, modifier, onTap = onTap) { MediaFiles.resolve(context, relativePath) }
}
