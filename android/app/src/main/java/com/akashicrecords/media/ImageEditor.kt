package com.akashicrecords.media

import android.graphics.Bitmap
import android.graphics.Matrix
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CheckboxDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.akashicrecords.Palette
import com.akashicrecords.sync.HubCompute
import com.akashicrecords.sync.SyncService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.min
import kotlin.math.roundToInt

/** The crop, as fractions (0..1) of the rotated picture. */
private data class Crop(val l: Float, val t: Float, val r: Float, val b: Float) {
    val full get() = l <= 0f && t <= 0f && r >= 1f && b >= 1f
    fun turnedRight() = Crop(1f - b, l, 1f - t, r)
    fun turnedLeft() = Crop(t, 1f - r, b, 1f - l)
}

private val FULL = Crop(0f, 0f, 1f, 1f)
private const val MIN_CROP = 0.05f

/** A brush stroke of the AI zone: points as fractions (0..1) of the unturned picture, width as a fraction of its width. */
private data class ZoneStroke(val points: List<Offset>, val width: Float)

/**
 * Full-screen editor of a picture already in the app: ⟲ ⟳ quarter turns, a crop frame drawn on the picture
 * (drag its corners, or inside it to move it), Contraste and Saturation. The controls are in the bars above and
 * under the picture, never over it. The preview is a small copy; « Enregistrer » redoes it all on the full
 * picture, writes the new file next to the current one and points [table] row [id] at it (see [ImageEdits]).
 *
 * [source] finds the full picture (a local file, or the hub's copy for an Archives photo). [onChanged] gets the
 * row's new path, after a save or « Revenir à l'original ».
 *
 * When the phone is paired, « IA » turns the bars into the AI request (done by a PC with ComfyUI through the hub,
 * never on the phone): a sentence, a zone painted on the picture with the finger and/or named in a few words, erase
 * first or not, or « Restaurer ». The result comes back later as an edit of the picture.
 */
@Composable
fun ImageEditor(
    table: String,
    id: Int,
    currentPath: String,
    source: suspend () -> File?,
    onClose: () -> Unit,
    onChanged: (String) -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var file by remember { mutableStateOf<File?>(null) }
    var preview by remember { mutableStateOf<Bitmap?>(null) }
    var message by remember { mutableStateOf<String?>("Chargement…") }
    var busy by remember { mutableStateOf(false) }
    var turns by remember { mutableIntStateOf(0) }
    var crop by remember { mutableStateOf(FULL) }
    var cropping by remember { mutableStateOf(false) }
    var contrast by remember { mutableFloatStateOf(0f) }
    var saturation by remember { mutableFloatStateOf(0f) }
    val edited = ImageEdits.isEdited(currentPath)
    val aiAvailable = remember { SyncService.credentials(context) != null }
    var aiMode by remember { mutableStateOf(false) }
    var prompt by remember { mutableStateOf("") }
    var zoneWords by remember { mutableStateOf("") }
    var eraseFirst by remember { mutableStateOf(false) }
    var painting by remember { mutableStateOf(false) }
    var brush by remember { mutableFloatStateOf(0.06f) }
    val strokes = remember { mutableStateListOf<ZoneStroke>() }

    LaunchedEffect(currentPath) {
        val (f, bmp) = withContext(Dispatchers.IO) {
            val f = runCatching { source() }.getOrNull()?.takeIf { it.isFile }
            f to f?.let { runCatching { ImageEdits.decodeUpright(it, 1600) }.getOrNull() }
        }
        file = f
        preview = bmp
        message = when {
            f == null -> "Image introuvable sur ce téléphone (hors ligne ?)."
            bmp == null -> "Image illisible."
            else -> null
        }
    }

    val shown: ImageBitmap? = remember(preview, turns) {
        preview?.let { b ->
            if (turns == 0) b.asImageBitmap()
            else Bitmap.createBitmap(b, 0, 0, b.width, b.height, Matrix().apply { postRotate(90f * turns) }, true).asImageBitmap()
        }
    }
    val c = contrast.roundToInt()
    val s = saturation.roundToInt()
    val filter = remember(c, s) {
        if (c == 0 && s == 0) null else ColorFilter.colorMatrix(androidx.compose.ui.graphics.ColorMatrix(ImageEdits.colorMatrix(c, s).array))
    }

    fun save() {
        val f = file ?: return
        busy = true
        message = "Enregistrement…"
        scope.launch {
            runCatching {
                val saved = ImageEdits.save(context, f, currentPath, ImageEdits.Params(turns, crop.l, crop.t, crop.r, crop.b, c, s))
                ImageEdits.apply(context, table, id, saved)
                saved.path
            }.onSuccess { onChanged(it); onClose() }
                .onFailure { message = "Échec : ${it.message ?: it.javaClass.simpleName}"; busy = false }
        }
    }

    // The zone as a mask at the preview's size: white where painted (the PC scales it to the full picture).
    fun zoneMask(): Bitmap? {
        val p = preview ?: return null
        if (strokes.isEmpty()) return null
        val mask = Bitmap.createBitmap(p.width, p.height, Bitmap.Config.ARGB_8888)
        val canvas = android.graphics.Canvas(mask)
        canvas.drawColor(android.graphics.Color.BLACK)
        val paint = android.graphics.Paint().apply {
            color = android.graphics.Color.WHITE; style = android.graphics.Paint.Style.STROKE; isAntiAlias = true
            strokeCap = android.graphics.Paint.Cap.ROUND; strokeJoin = android.graphics.Paint.Join.ROUND
        }
        for (s in strokes) {
            paint.strokeWidth = s.width * p.width
            val path = android.graphics.Path()
            path.moveTo(s.points[0].x * p.width, s.points[0].y * p.height)
            for (pt in s.points.drop(1)) path.lineTo(pt.x * p.width, pt.y * p.height)
            if (s.points.size == 1) path.lineTo(s.points[0].x * p.width + 0.5f, s.points[0].y * p.height)
            canvas.drawPath(path, paint)
        }
        return mask
    }

    fun sendToAi(kind: String) {
        if (kind == "edit" && prompt.isBlank()) { message = "Écris d'abord ce qu'il faut changer."; return }
        busy = true
        message = "Envoi…"
        scope.launch {
            val failure = HubCompute.requestEdit(
                context, table, id.toLong(), kind, if (kind == "edit") prompt.trim() else null,
                if (kind == "edit") zoneMask() else null, zoneWords.trim().ifEmpty { null }.takeIf { kind == "edit" }, eraseFirst && kind == "edit",
            )
            busy = false
            message = failure ?: "Envoyée : l'image modifiée arrivera d'ici quelques minutes (Revenir à l'original restera possible)."
        }
    }

    fun revert() {
        busy = true
        scope.launch {
            ImageEdits.revert(context, table, id, currentPath)
                .onSuccess { onChanged(it); onClose() }
                .onFailure { message = it.message; busy = false }
        }
    }

    var confirmFinal by remember { mutableStateOf(false) }
    if (confirmFinal) ConfirmMakeFinal(
        onConfirm = {
            confirmFinal = false
            busy = true
            message = "Finalisation…"
            scope.launch {
                ImageEdits.makeFinal(context, table, id)
                    .onSuccess { onChanged(it); onClose() }
                    .onFailure { message = it.message; busy = false }
            }
        },
        onDismiss = { confirmFinal = false },
    )

    Dialog(onDismissRequest = { if (!busy) onClose() }, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Palette.AppBg)) {
            // Top bar: leave, what is going on, save.
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(enabled = !busy, onClick = onClose) { Text("Annuler") }
                Text(
                    message ?: "Modifier l'image",
                    fontSize = 13.sp,
                    color = Palette.TextMuted,
                    textAlign = TextAlign.Center,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                TextButton(enabled = !busy && shown != null, onClick = { save() }) { Text("Enregistrer") }
            }

            // The picture, with the crop frame drawn on it (the tool itself).
            BoxWithConstraints(Modifier.weight(1f).fillMaxWidth().padding(12.dp), contentAlignment = Alignment.Center) {
                val img = shown
                if (img == null) {
                    if (message == "Chargement…") CircularProgressIndicator(color = Palette.Accent)
                    return@BoxWithConstraints
                }
                val boxW = constraints.maxWidth.toFloat()
                val boxH = constraints.maxHeight.toFloat()
                val scale = min(boxW / img.width, boxH / img.height)
                val dw = img.width * scale
                val dh = img.height * scale
                val ox = (boxW - dw) / 2f
                val oy = (boxH - dh) / 2f
                val touch = with(LocalDensity.current) { 32.dp.toPx() }
                val handle = with(LocalDensity.current) { 7.dp.toPx() }
                val line = with(LocalDensity.current) { 2.dp.toPx() }
                val zoneColor = Color(0xFFFF3030).copy(alpha = 0.45f)
                Canvas(
                    Modifier.fillMaxSize().pointerInput(painting, ox, oy, dw, dh) {
                        // The AI zone: painted with the finger, on the unturned picture only.
                        if (!painting) return@pointerInput
                        fun frac(p: Offset) = Offset(((p.x - ox) / dw).coerceIn(0f, 1f), ((p.y - oy) / dh).coerceIn(0f, 1f))
                        detectDragGestures(
                            onDragStart = { pos -> strokes.add(ZoneStroke(listOf(frac(pos)), brush)) },
                            onDrag = { change, _ ->
                                change.consume()
                                val last = strokes.removeAt(strokes.lastIndex)
                                strokes.add(last.copy(points = last.points + frac(change.position)))
                            },
                        )
                    }.pointerInput(cropping, ox, oy, dw, dh) {
                        if (!cropping) return@pointerInput
                        var grab = -1 // 0 TL, 1 TR, 2 BR, 3 BL, 4 inside (move)
                        detectDragGestures(
                            onDragStart = { pos ->
                                val k = crop
                                val corners = listOf(
                                    Offset(ox + k.l * dw, oy + k.t * dh), Offset(ox + k.r * dw, oy + k.t * dh),
                                    Offset(ox + k.r * dw, oy + k.b * dh), Offset(ox + k.l * dw, oy + k.b * dh),
                                )
                                val near = corners.withIndex().minBy { (_, p) -> hypot(p.x - pos.x, p.y - pos.y) }
                                grab = when {
                                    hypot(near.value.x - pos.x, near.value.y - pos.y) <= touch -> near.index
                                    pos.x in corners[0].x..corners[2].x && pos.y in corners[0].y..corners[2].y -> 4
                                    else -> -1
                                }
                            },
                            onDragEnd = { grab = -1 },
                            onDragCancel = { grab = -1 },
                            onDrag = { change, drag ->
                                if (grab < 0) return@detectDragGestures
                                change.consume()
                                val dx = drag.x / dw
                                val dy = drag.y / dh
                                val k = crop
                                crop = when (grab) {
                                    0 -> k.copy(l = (k.l + dx).coerceIn(0f, k.r - MIN_CROP), t = (k.t + dy).coerceIn(0f, k.b - MIN_CROP))
                                    1 -> k.copy(r = (k.r + dx).coerceIn(k.l + MIN_CROP, 1f), t = (k.t + dy).coerceIn(0f, k.b - MIN_CROP))
                                    2 -> k.copy(r = (k.r + dx).coerceIn(k.l + MIN_CROP, 1f), b = (k.b + dy).coerceIn(k.t + MIN_CROP, 1f))
                                    3 -> k.copy(l = (k.l + dx).coerceIn(0f, k.r - MIN_CROP), b = (k.b + dy).coerceIn(k.t + MIN_CROP, 1f))
                                    else -> {
                                        val mx = dx.coerceIn(-k.l, 1f - k.r)
                                        val my = dy.coerceIn(-k.t, 1f - k.b)
                                        Crop(k.l + mx, k.t + my, k.r + mx, k.b + my)
                                    }
                                }
                            },
                        )
                    },
                ) {
                    drawImage(
                        img,
                        dstOffset = IntOffset(ox.roundToInt(), oy.roundToInt()),
                        dstSize = IntSize(dw.roundToInt().coerceAtLeast(1), dh.roundToInt().coerceAtLeast(1)),
                        colorFilter = filter,
                    )
                    for (s in strokes) {
                        val path = Path()
                        path.moveTo(ox + s.points[0].x * dw, oy + s.points[0].y * dh)
                        for (pt in s.points.drop(1)) path.lineTo(ox + pt.x * dw, oy + pt.y * dh)
                        if (s.points.size == 1) path.lineTo(ox + s.points[0].x * dw + 0.5f, oy + s.points[0].y * dh)
                        drawPath(path, zoneColor, style = Stroke(width = s.width * dw, cap = StrokeCap.Round, join = StrokeJoin.Round))
                    }
                    if (cropping || !crop.full) {
                        val l = ox + crop.l * dw
                        val t = oy + crop.t * dh
                        val r = ox + crop.r * dw
                        val b = oy + crop.b * dh
                        // What the crop leaves out, darkened (fully hidden once the frame is no longer being edited).
                        val shade = Color.Black.copy(alpha = if (cropping) 0.55f else 0.85f)
                        drawRect(shade, Offset(ox, oy), Size(dw, t - oy))
                        drawRect(shade, Offset(ox, b), Size(dw, oy + dh - b))
                        drawRect(shade, Offset(ox, t), Size(l - ox, b - t))
                        drawRect(shade, Offset(r, t), Size(ox + dw - r, b - t))
                        if (cropping) {
                            drawRect(Palette.Accent, Offset(l, t), Size(r - l, b - t), style = Stroke(line))
                            for (p in listOf(Offset(l, t), Offset(r, t), Offset(r, b), Offset(l, b))) {
                                drawCircle(Palette.Accent, handle, p)
                                drawCircle(Palette.AppBg, handle - line, p)
                            }
                        }
                    }
                }
            }

            // Bottom bars: turning and cropping, then the two sliders; or, in « IA » mode, the AI request.
            if (aiMode) Column(Modifier.fillMaxWidth().background(Palette.Surface1).padding(horizontal = 8.dp, vertical = 4.dp)) {
                Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()), verticalAlignment = Alignment.CenterVertically) {
                    TextButton(enabled = !busy, onClick = { aiMode = false; painting = false }) { Text("← Retouches") }
                    TextButton(enabled = !busy && turns == 0, onClick = { painting = !painting; cropping = false }) {
                        Text(if (painting) "✓ Peindre la zone" else "Peindre la zone", color = if (painting) Palette.Accent else Palette.Text)
                    }
                    TextButton(enabled = !busy && strokes.isNotEmpty(), onClick = { strokes.clear() }) { Text("Effacer la zone") }
                    TextButton(enabled = !busy && shown != null, onClick = { sendToAi("restore") }) { Text("Restaurer") }
                }
                if (painting) Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("Pinceau", fontSize = 13.sp, color = Palette.TextMuted, modifier = Modifier.width(84.dp))
                    Slider(
                        value = brush, onValueChange = { brush = it }, valueRange = 0.01f..0.2f,
                        colors = SliderDefaults.colors(thumbColor = Palette.Accent, activeTrackColor = Palette.Accent, inactiveTrackColor = Palette.Surface3),
                        modifier = Modifier.weight(1f),
                    )
                }
                OutlinedTextField(
                    prompt, { prompt = it }, enabled = !busy, minLines = 2, maxLines = 3,
                    label = { Text("Ce qu'il faut changer (en anglais de préférence)") },
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp),
                )
                OutlinedTextField(
                    zoneWords, { zoneWords = it }, enabled = !busy, singleLine = true,
                    label = { Text("Ou la zone en quelques mots (« blue ball »)") },
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp),
                )
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(eraseFirst, { eraseFirst = it }, enabled = !busy, colors = CheckboxDefaults.colors(checkedColor = Palette.Accent))
                    Text("Effacer d'abord la zone", fontSize = 13.sp, color = Palette.Text, modifier = Modifier.weight(1f))
                    TextButton(enabled = !busy && shown != null, onClick = { sendToAi("edit") }) { Text("Envoyer à l'IA") }
                }
            } else Column(Modifier.fillMaxWidth().background(Palette.Surface1).padding(horizontal = 8.dp, vertical = 4.dp)) {
                Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()), verticalAlignment = Alignment.CenterVertically) {
                    if (aiAvailable) TextButton(enabled = shown != null && !busy, onClick = { aiMode = true; cropping = false }) { Text("IA") }
                    // A turn drops the AI zone: it is painted on the stored picture's orientation.
                    TextButton(enabled = shown != null && !busy, onClick = { crop = crop.turnedLeft(); turns = (turns + 3) % 4; strokes.clear() }) { Text("⟲", fontSize = 20.sp) }
                    TextButton(enabled = shown != null && !busy, onClick = { crop = crop.turnedRight(); turns = (turns + 1) % 4; strokes.clear() }) { Text("⟳", fontSize = 20.sp) }
                    TextButton(enabled = shown != null && !busy, onClick = { cropping = !cropping }) {
                        Text(if (cropping) "✓ Recadrer" else "Recadrer", color = if (cropping) Palette.Accent else Palette.Text)
                    }
                    TextButton(enabled = !crop.full && !busy, onClick = { crop = FULL }) { Text("Sans recadrage") }
                    TextButton(enabled = !busy, onClick = {
                        turns = 0; crop = FULL; contrast = 0f; saturation = 0f; cropping = false
                    }) { Text("Réinitialiser") }
                }
                AdjustRow("Contraste", contrast, enabled = shown != null && !busy) { contrast = it }
                AdjustRow("Saturation", saturation, enabled = shown != null && !busy) { saturation = it }
                if (edited) Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text("Image déjà modifiée", fontSize = 12.sp, color = Palette.TextFaint, modifier = Modifier.weight(1f).padding(start = 8.dp))
                    TextButton(enabled = !busy, onClick = { revert() }) { Text("Revenir à l'original") }
                    TextButton(enabled = !busy, onClick = { confirmFinal = true }) { Text("Rendre définitif") }
                }
            }
        }
    }
}

/** The confirmation before « Rendre définitif » ([ImageEdits.makeFinal]), shared by every entry point. */
@Composable
fun ConfirmMakeFinal(onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        text = { Text(ImageEdits.FINAL_QUESTION) },
        confirmButton = { TextButton(onClick = onConfirm) { Text("Oui") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Annuler") } },
    )
}

@Composable
private fun AdjustRow(label: String, value: Float, enabled: Boolean, onChange: (Float) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, fontSize = 13.sp, color = Palette.TextMuted, modifier = Modifier.width(84.dp))
        Slider(
            value = value,
            onValueChange = { onChange(if (abs(it) < 2f) 0f else it) }, // a little stickiness at 0 (unchanged)
            valueRange = -100f..100f,
            enabled = enabled,
            colors = SliderDefaults.colors(thumbColor = Palette.Accent, activeTrackColor = Palette.Accent, inactiveTrackColor = Palette.Surface3),
            modifier = Modifier.weight(1f),
        )
        Box(Modifier.width(44.dp), contentAlignment = Alignment.CenterEnd) {
            Text(value.roundToInt().let { if (it > 0) "+$it" else "$it" }, fontSize = 12.sp, color = Palette.Text)
        }
    }
}
