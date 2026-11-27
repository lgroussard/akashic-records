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
import androidx.compose.runtime.saveable.rememberSaveable
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
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Journaux → Archive artistique on the phone: the projects as cards, each opening its real canvas — notes, images,
// observation references and the lines between them, where the PC placed them — to look around with one finger and
// zoom with two. (Building the canvas stays on the PC.) An open project also has its « Fil »: everything it
// references, as a list. [onOpenItem] (kind, id, Collections category) opens the item a Fil card is about.
@Composable
internal fun ArtisticJournal(db: AkashicDatabase, onOpenItem: (String, Int, Int?) -> Unit = { _, _, _ -> }) {
    var projects by remember { mutableStateOf<List<ArtisticProject>>(emptyList()) }
    var counts by remember { mutableStateOf<Map<Int, Int>>(emptyMap()) }
    var filCounts by remember { mutableStateOf<Map<Int, Int>>(emptyMap()) }
    var open by remember { mutableStateOf<ArtisticProject?>(null) }
    LaunchedEffect(db) {
        withContext(Dispatchers.IO) {
            projects = db.artisticProjectDao().getAll()
            counts = projects.associate { it.Id to db.canvasElementDao().forProject(it.Id).size }
            filCounts = projects.associate { it.Id to db.referenceDao().forProject(it.Id).map { r -> r.ObservationId }.distinct().size }
        }
    }
    open?.let { p -> CanvasPage(db, p, onBack = { open = null }, onOpenItem = onOpenItem); return }

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
                Text("${counts[p.Id] ?: 0} élément(s) sur la toile · ${filCounts[p.Id] ?: 0} dans le fil", fontSize = 12.sp, color = Palette.TextMuted)
            }
        }
    }
}

private data class FilCard(val observation: Observation, val owner: ObservationOwner)

// The « Fil » of a project: every observation it references (Reference rows, whether or not the observation also
// sits on the canvas), grouped by the kind of item it is about, newest first. Read-only: linking is done on the PC
// (and, for book highlights, in Lecture). A card opens its item.
@Composable
private fun FilList(db: AkashicDatabase, project: ArtisticProject, onOpenItem: (String, Int, Int?) -> Unit) {
    var cards by remember { mutableStateOf<List<FilCard>?>(null) }
    LaunchedEffect(project.Id) {
        cards = withContext(Dispatchers.IO) {
            db.referenceDao().forProject(project.Id).map { it.ObservationId }.distinct()
                .mapNotNull { db.observationDao().getById(it) }
                .sortedWith(compareByDescending<Observation> { it.CreatedAt }.thenByDescending { it.Id })
                .map { FilCard(it, ownerOf(db, it)) }
        }
    }
    val all = cards ?: return
    if (all.isEmpty()) {
        Text("Rien dans le fil de ce projet (les observations s'y relient sur le PC).", color = Palette.TextMuted, modifier = Modifier.padding(16.dp))
        return
    }
    val known = OWNER_GROUPS.map { it.first }
    val groups = OWNER_GROUPS.map { (kind, header) -> header to all.filter { it.owner.kind == kind } } +
        ("Autres" to all.filter { it.owner.kind !in known })
    LazyColumn(Modifier.fillMaxSize()) {
        groups.filter { it.second.isNotEmpty() }.forEach { (header, group) ->
            item(key = header) {
                Text(
                    "${header.uppercase()} · ${group.size}", fontSize = 11.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold,
                    modifier = Modifier.padding(start = 14.dp, top = 12.dp, bottom = 2.dp),
                )
            }
            items(group, key = { it.observation.Id }) { card ->
                val o = card.observation
                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp).clip(RoundedCornerShape(10.dp))
                        .background(Palette.Surface1).clickable { onOpenItem(card.owner.kind, card.owner.id, card.owner.category) }.padding(10.dp),
                ) {
                    if (card.owner.imagePath != null) {
                        LocalImage(card.owner.imagePath, Modifier.size(52.dp).clip(RoundedCornerShape(6.dp)), maxSide = 200)
                        Spacer(Modifier.width(10.dp))
                    }
                    Column(Modifier.weight(1f)) {
                        Text(card.owner.title, fontSize = 12.sp, color = Palette.Accent, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        if (o.Subject.isNotBlank()) Text(o.Subject, fontWeight = FontWeight.SemiBold)
                        Text(o.Content, fontSize = 14.sp, color = Palette.TextMuted, maxLines = 8, overflow = TextOverflow.Ellipsis)
                        datePart(o.CreatedAt)?.let { Text(frenchDate(it), fontSize = 10.sp, color = Palette.TextFaint, modifier = Modifier.padding(top = 2.dp)) }
                    }
                }
            }
        }
        item { Spacer(Modifier.height(24.dp)) }
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
private fun CanvasPage(db: AkashicDatabase, project: ArtisticProject, onBack: () -> Unit, onOpenItem: (String, Int, Int?) -> Unit) {
    BackHandler(onBack = onBack)
    var fil by rememberSaveable(project.Id) { mutableStateOf(false) }
    var elements by remember { mutableStateOf<List<CanvasElement>>(emptyList()) }
    var connectors by remember { mutableStateOf<List<CanvasConnector>>(emptyList()) }
    var observations by remember { mutableStateOf<Map<Int, Observation>>(emptyMap()) }
    // Observation id -> the title of the item it is about (an artwork, a poem, a journal day…).
    var ownerTitles by remember { mutableStateOf<Map<Int, String>>(emptyMap()) }
    // The canvas pictures, for the large view opened by a tap on one (its bar has « Agrandir ×4 » when paired).
    val images = elements.filter { it.Type == 1 && !it.ImagePath.isNullOrBlank() }
    var viewing by remember { mutableStateOf<Int?>(null) } // index in [images]
    val scope = rememberCoroutineScope()
    viewing?.let { i ->
        PhotoViewer(
            images.map { it.ImagePath!! }, i, onDismiss = { viewing = null }, table = "CanvasElement", ids = images.map { it.Id },
            // An edited picture: its new path and height, reloaded so the canvas shows it too.
            onChanged = { scope.launch { elements = withContext(Dispatchers.IO) { db.canvasElementDao().forProject(project.Id) } } },
        )
    }
    LaunchedEffect(project.Id) {
        withContext(Dispatchers.IO) {
            elements = db.canvasElementDao().forProject(project.Id)
            connectors = db.canvasConnectorDao().forProject(project.Id)
            observations = elements.mapNotNull { it.ObservationId }.distinct().mapNotNull { db.observationDao().getById(it) }.associateBy { it.Id }
            ownerTitles = observations.mapValues { (_, o) -> ownerOf(db, o).title }
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
        }
        Row(Modifier.padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            FilterChip(selected = !fil, onClick = { fil = false }, label = { Text("Canvas") })
            Spacer(Modifier.width(6.dp))
            FilterChip(selected = fil, onClick = { fil = true }, label = { Text("Fil") })
            Spacer(Modifier.weight(1f))
            if (!fil) TextButton(onClick = { scale = null; offset = Offset.Zero }) { Text("Recentrer", fontSize = 12.sp) }
        }
        if (fil) {
            FilList(db, project, onOpenItem)
            return@Column
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
                            1 -> {
                                val index = images.indexOfFirst { it.Id == e.Id }
                                LocalImage(
                                    e.ImagePath,
                                    if (index >= 0) Modifier.fillMaxSize().clickable { viewing = index } else Modifier.fillMaxSize(),
                                    maxSide = 800, crop = false,
                                )
                            }
                            2 -> {
                                val o = e.ObservationId?.let { observations[it] }
                                Column(Modifier.padding(8.dp)) {
                                    Text(o?.let { ownerTitles[it.Id] } ?: "Observation", fontSize = 9.sp, color = Palette.Accent, maxLines = 1, overflow = TextOverflow.Ellipsis)
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
