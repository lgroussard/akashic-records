package com.akashicrecords.journaux

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ArtisticProject
import com.akashicrecords.data.CanvasConnector
import com.akashicrecords.data.CanvasElement
import com.akashicrecords.data.Observation
import com.akashicrecords.media.LocalImage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

// Journaux → Archive artistique on the phone: the projects as cards, each opening its real canvas — notes, images,
// observation references and the lines between them, where the PC placed them — to look around with one finger and
// zoom with two. (Building the canvas stays on the PC.)
@Composable
internal fun ArtisticJournal(db: AkashicDatabase) {
    var projects by remember { mutableStateOf<List<ArtisticProject>>(emptyList()) }
    var counts by remember { mutableStateOf<Map<Int, Int>>(emptyMap()) }
    var open by remember { mutableStateOf<ArtisticProject?>(null) }
    LaunchedEffect(db) {
        withContext(Dispatchers.IO) {
            projects = db.artisticProjectDao().getAll()
            counts = projects.associate { it.Id to db.canvasElementDao().forProject(it.Id).size }
        }
    }
    open?.let { p -> CanvasPage(db, p, onBack = { open = null }); return }

    if (projects.isEmpty()) {
        Text("Aucun projet artistique (ils se créent sur le PC).", color = Palette.TextMuted, modifier = Modifier.padding(16.dp))
        return
    }
    LazyColumn(Modifier.fillMaxSize()) {
        items(projects, key = { it.Id }) { p ->
            Column(
                Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 5.dp).clip(RoundedCornerShape(12.dp))
                    .background(Palette.Surface1).clickable { open = p }.padding(14.dp),
            ) {
                Text(p.Title, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                Text("${counts[p.Id] ?: 0} élément(s) sur la toile", fontSize = 12.sp, color = Palette.TextMuted)
            }
        }
    }
}

// The PC's default sizes for an element whose size was never set (JournauxView).
private fun sizeOf(e: CanvasElement): Pair<Float, Float> {
    val (w, h) = when (e.Type) {
        0 -> 200.0 to 110.0
        1 -> 220.0 to 160.0
        else -> 180.0 to 90.0
    }
    return (e.Width ?: w).toFloat() to (e.Height ?: h).toFloat()
}

@Composable
private fun CanvasPage(db: AkashicDatabase, project: ArtisticProject, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    var elements by remember { mutableStateOf<List<CanvasElement>>(emptyList()) }
    var connectors by remember { mutableStateOf<List<CanvasConnector>>(emptyList()) }
    var observations by remember { mutableStateOf<Map<Int, Observation>>(emptyMap()) }
    LaunchedEffect(project.Id) {
        withContext(Dispatchers.IO) {
            elements = db.canvasElementDao().forProject(project.Id)
            connectors = db.canvasConnectorDao().forProject(project.Id)
            observations = elements.mapNotNull { it.ObservationId }.distinct().mapNotNull { db.observationDao().getById(it) }.associateBy { it.Id }
        }
    }

    // The canvas in its own units (1 unit = 1 dp): everything is shifted so the top-left element sits at the margin.
    val margin = 24f
    val minX = elements.minOfOrNull { it.X.toFloat() } ?: 0f
    val minY = elements.minOfOrNull { it.Y.toFloat() } ?: 0f
    val width = (elements.maxOfOrNull { it.X.toFloat() + sizeOf(it).first } ?: 0f) - minX + 2 * margin
    val height = (elements.maxOfOrNull { it.Y.toFloat() + sizeOf(it).second } ?: 0f) - minY + 2 * margin
    fun local(e: CanvasElement) = Offset(e.X.toFloat() - minX + margin, e.Y.toFloat() - minY + margin)

    val density = LocalDensity.current
    var viewport by remember { mutableStateOf(IntSize.Zero) }
    var scale by remember { mutableStateOf<Float?>(null) }
    var offset by remember { mutableStateOf(Offset.Zero) }
    // First view: the whole width of the canvas on screen (never zoomed in beyond 1).
    LaunchedEffect(viewport, elements) {
        if (scale == null && viewport.width > 0 && elements.isNotEmpty()) {
            scale = (viewport.width / with(density) { width.dp.toPx() }).coerceIn(0.15f, 1f)
        }
    }

    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("← Projets") }
            Text(project.Title, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            TextButton(onClick = { scale = null; offset = Offset.Zero }) { Text("Recentrer", fontSize = 12.sp) }
        }
        Box(
            Modifier.fillMaxSize().background(Palette.AppBg).onSizeChanged { viewport = it }
                .pointerInput(Unit) {
                    detectTransformGestures { _, pan, zoom, _ ->
                        val s = ((scale ?: 1f) * zoom).coerceIn(0.1f, 4f)
                        scale = s
                        offset += pan
                    }
                },
        ) {
            if (elements.isEmpty()) {
                Text("Toile vide.", color = Palette.TextMuted, modifier = Modifier.padding(16.dp))
                return@Box
            }
            val s = scale ?: return@Box
            Box(
                Modifier.requiredSize(width.dp, height.dp).align(Alignment.TopStart)
                    .graphicsLayer {
                        transformOrigin = TransformOrigin(0f, 0f)
                        scaleX = s; scaleY = s
                        translationX = offset.x; translationY = offset.y
                    },
            ) {
                // Lines between linked elements, centre to centre.
                val byId = elements.associateBy { it.Id }
                Canvas(Modifier.fillMaxSize()) {
                    connectors.forEach { c ->
                        val a = byId[c.FromElementId] ?: return@forEach
                        val b = byId[c.ToElementId] ?: return@forEach
                        fun centre(e: CanvasElement): Offset {
                            val (w, h) = sizeOf(e)
                            val p = local(e)
                            return Offset((p.x + w / 2).dp.toPx(), (p.y + h / 2).dp.toPx())
                        }
                        drawLine(Palette.Accent.copy(alpha = 0.6f), centre(a), centre(b), strokeWidth = 2.dp.toPx())
                    }
                }
                elements.forEach { e ->
                    val (w, h) = sizeOf(e)
                    val p = local(e)
                    Box(
                        Modifier.offset(p.x.dp, p.y.dp).size(w.dp, h.dp).clip(RoundedCornerShape(8.dp))
                            .background(Palette.Surface1).border(1.dp, Palette.BorderStrong, RoundedCornerShape(8.dp)),
                    ) {
                        when (e.Type) {
                            1 -> LocalImage(e.ImagePath, Modifier.fillMaxSize(), maxSide = 800, crop = false)
                            2 -> {
                                val o = e.ObservationId?.let { observations[it] }
                                Column(Modifier.padding(8.dp)) {
                                    Text("Observation", fontSize = 9.sp, color = Palette.Accent)
                                    if (o != null) {
                                        if (o.Subject.isNotBlank()) Text(o.Subject, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                        Text(o.Content, fontSize = 11.sp, color = Palette.TextMuted, overflow = TextOverflow.Ellipsis)
                                    } else Text(e.TextContent.orEmpty(), fontSize = 11.sp, color = Palette.TextMuted)
                                }
                            }
                            else -> Text(e.TextContent.orEmpty(), fontSize = 13.sp, modifier = Modifier.padding(8.dp), overflow = TextOverflow.Ellipsis)
                        }
                    }
                }
            }
        }
    }
}
