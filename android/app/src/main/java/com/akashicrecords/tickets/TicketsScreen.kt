package com.akashicrecords.tickets

import android.app.Activity
import android.app.DatePickerDialog
import android.content.Context
import android.content.ContextWrapper
import android.graphics.Bitmap
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateCentroid
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.akashicrecords.Palette
import com.akashicrecords.SwipePages
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.CalendarEvent
import com.akashicrecords.data.Ticket
import com.akashicrecords.journaux.ConfirmDelete
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

private val DAY = DateTimeFormatter.ofPattern("EEE d MMM", Locale.FRENCH)
private val LONG_DAY = DateTimeFormatter.ofPattern("EEEE d MMMM", Locale.FRENCH)

internal fun ticketDay(d: LocalDate): String {
    val today = LocalDate.now()
    return when (d) {
        today -> "Aujourd'hui"
        today.plusDays(1) -> "Demain"
        else -> d.format(LONG_DAY).replaceFirstChar { it.uppercase() }
    }
}

// Picker for PDFs and pictures, several at once (from the calendar or the Billets list).
@Composable
fun rememberTicketPicker(onPicked: (List<TicketStore.Incoming>) -> Unit): () -> Unit {
    val context = LocalContext.current
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        val items = uris.mapNotNull { TicketStore.describe(context, it) }
        if (items.isNotEmpty()) onPicked(items)
    }
    return { launcher.launch(arrayOf("application/pdf", "image/*")) }
}

// ---- Attach: « Rattacher à… » an upcoming event, or a day alone ----

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AttachTicketsSheet(
    db: AkashicDatabase,
    items: List<TicketStore.Incoming>,
    day: LocalDate? = null,
    eventId: Int? = null,
    onDone: (saved: Boolean) -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var events by remember { mutableStateOf<List<Pair<CalendarEvent, LocalDate>>>(emptyList()) }
    var event by remember { mutableStateOf<CalendarEvent?>(null) }
    var date by remember { mutableStateOf(day ?: LocalDate.now()) }
    // One name per file: aller / retour when two come together, otherwise the file's own name.
    val names = remember {
        mutableStateListOf(*items.mapIndexed { i, it ->
            when {
                items.size == 2 -> if (i == 0) "Aller" else "Retour"
                it.name.isNotBlank() && !it.name.matches(Regex("[0-9a-fA-F-_ ]{8,}")) -> it.name
                else -> ""
            }
        }.toTypedArray())
    }
    var saving by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        val all = withContext(Dispatchers.IO) { TicketStore.upcomingEvents(db) }
        // An event that ended before today but was asked for (from its row) still shows.
        val asked = eventId?.let { id -> all.firstOrNull { it.first.Id == id } ?: withContext(Dispatchers.IO) {
            db.calendarEventDao().getAll().firstOrNull { it.Id == id }?.let { e -> com.akashicrecords.calendar.Agenda.day(e.Date)?.let { e to it } }
        } }
        events = listOfNotNull(asked) + all.filter { it.first.Id != asked?.first?.Id }
        asked?.let { (e, start) -> event = e; if (day == null) date = start }
    }

    ModalBottomSheet(onDismissRequest = { onDone(false) }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = Palette.Surface1) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
            Text(if (items.size == 1) "Ajouter un billet" else "Ajouter ${items.size} billets", fontSize = 18.sp)
            Spacer(Modifier.height(8.dp))
            items.forEachIndexed { i, it ->
                OutlinedTextField(
                    names[i], { v -> names[i] = v },
                    label = { Text(if (it.mime == "application/pdf") "Nom du billet (PDF)" else "Nom du billet (image)") },
                    placeholder = { Text("Billet ${i + 1}") },
                    singleLine = true, modifier = Modifier.fillMaxWidth().padding(bottom = 4.dp),
                )
            }

            Text("RATTACHER À", fontSize = 11.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 12.dp, bottom = 4.dp))
            ChoiceRow(selected = event == null, title = "Un jour seulement", sub = null) { event = null }
            events.forEach { (e, start) ->
                ChoiceRow(selected = event?.Id == e.Id, title = e.Title, sub = ticketDay(start) + e.Location.takeIf { it.isNotBlank() }?.let { " · $it" }.orEmpty()) {
                    // Picking an event moves the day to its start, unless the day was already set inside the event.
                    val end = com.akashicrecords.calendar.Agenda.day(e.EndDate)?.takeIf { it > start } ?: start
                    if (date !in start..end) date = start
                    event = e
                }
            }
            if (events.isEmpty()) Text("Aucun événement à venir dans le calendrier.", fontSize = 12.sp, color = Palette.TextFaint, modifier = Modifier.padding(start = 4.dp, top = 2.dp))

            Text("JOUR DU BILLET", fontSize = 11.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 12.dp, bottom = 2.dp))
            TextButton(onClick = {
                DatePickerDialog(context, { _, y, m, d -> date = LocalDate.of(y, m + 1, d) }, date.year, date.monthValue - 1, date.dayOfMonth).show()
            }) { Text(date.format(LONG_DAY).replaceFirstChar { it.uppercase() } + " " + date.year + "  ✎") }

            Row(Modifier.padding(top = 8.dp)) {
                Spacer(Modifier.weight(1f))
                TextButton(onClick = { onDone(false) }) { Text("Annuler") }
                Button(enabled = !saving, onClick = {
                    saving = true
                    scope.launch {
                        val r = runCatching {
                            withContext(Dispatchers.IO) {
                                items.forEachIndexed { i, it ->
                                    TicketStore.add(context, db, it, names[i].ifBlank { if (items.size == 1) "Billet" else "Billet ${i + 1}" }, date, event)
                                }
                            }
                        }
                        saving = false
                        r.onSuccess {
                            Toast.makeText(context, if (items.size == 1) "Billet ajouté" else "${items.size} billets ajoutés", Toast.LENGTH_SHORT).show()
                            onDone(true)
                        }.onFailure { Toast.makeText(context, "Échec : ${it.message}", Toast.LENGTH_LONG).show() }
                    }
                }) { Text(if (saving) "…" else "Enregistrer") }
            }
        }
    }
}

@Composable
private fun ChoiceRow(selected: Boolean, title: String, sub: String?, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(vertical = 2.dp).clip(RoundedCornerShape(8.dp))
            .background(if (selected) Palette.AccentSoft else Color.Transparent).clickable(onClick = onClick).padding(horizontal = 4.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = onClick)
        Column {
            Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (sub != null) Text(sub, fontSize = 12.sp, color = Palette.TextMuted, maxLines = 1)
        }
    }
}

// ---- Viewer: full screen, brightness up, swipe between tickets ----

private tailrec fun Context.activity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.activity()
    else -> null
}

@Composable
fun TicketViewer(db: AkashicDatabase, tickets: List<Ticket>, start: Int, onClose: () -> Unit, onChanged: () -> Unit = {}) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var list by remember { mutableStateOf(tickets) }
    var page by remember { mutableStateOf(start.coerceIn(0, (tickets.size - 1).coerceAtLeast(0))) }
    var confirm by remember { mutableStateOf(false) }
    // The ticket on screen is zoomed in: one-finger drags move inside it instead of going to the next ticket.
    var zoomedIn by remember { mutableStateOf(false) }

    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        // Full brightness while a ticket is on screen, for the inspector's scanner; back to normal on close.
        val window = (androidx.compose.ui.platform.LocalView.current.parent as? androidx.compose.ui.window.DialogWindowProvider)?.window
        DisposableEffect(window) {
            val w = window ?: context.activity()?.window
            val before = w?.attributes?.screenBrightness
            w?.attributes = w?.attributes?.apply { screenBrightness = WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_FULL }
            w?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            onDispose {
                w?.attributes = w?.attributes?.apply { screenBrightness = before ?: WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE }
                w?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            }
        }
        if (list.isEmpty()) { LaunchedEffect(Unit) { onClose() }; return@Dialog }
        val current = list[page.coerceIn(0, list.lastIndex)]
        Column(Modifier.fillMaxSize().background(Color(0xFF0B0B0F)).systemBarsPadding()) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 4.dp)) {
                TextButton(onClick = onClose) { Text("✕", fontSize = 18.sp, color = Palette.Text) }
                Column(Modifier.weight(1f)) {
                    Text(current.Title, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    val day = runCatching { LocalDate.parse(current.Date) }.getOrNull()
                    Text(
                        listOfNotNull(day?.let(::ticketDay), current.EventTitle.takeIf { it.isNotBlank() }).joinToString(" · "),
                        fontSize = 12.sp, color = Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    )
                }
                if (list.size > 1) Text("${page + 1} / ${list.size}", fontSize = 12.sp, color = Palette.TextMuted)
                TextButton(onClick = { confirm = true }) { Text("🗑") }
            }
            SwipePages(
                count = list.size, page = page, onPageChange = { page = it }, modifier = Modifier.fillMaxWidth().weight(1f),
                key = { list[it].Id }, userScrollEnabled = !zoomedIn,
            ) { i ->
                TicketPages(list[i]) { z -> if (i == page) zoomedIn = z }
            }
        }
        if (confirm) ConfirmDelete("le billet « ${current.Title} »", onConfirm = {
            val gone = current
            list = list.filter { it.Id != gone.Id }
            page = page.coerceAtMost((list.size - 1).coerceAtLeast(0))
            scope.launch { withContext(Dispatchers.IO) { TicketStore.delete(context, db, gone) }; onChanged() }
        }, onDismiss = { confirm = false })
    }
}

private const val MAX_ZOOM = 6f
private const val DOUBLE_TAP_ZOOM = 2.5f

// One ticket, its pages one under the other on a white card (PDF pages are drawn on white anyway). Two fingers zoom in
// (up to ×6, around the fingers) and, once zoomed, one finger moves around; a double tap zooms on the spot / back out —
// QR codes on PDF tickets are often small. Pages are drawn at 3× the screen width so a zoomed code stays sharp.
// onZoomed: true while zoomed, so the viewer stops swiping to the next ticket.
@Composable
private fun TicketPages(t: Ticket, onZoomed: (Boolean) -> Unit) {
    val context = LocalContext.current
    val widthPx = with(LocalDensity.current) { LocalConfiguration.current.screenWidthDp.dp.toPx() }.toInt() * 3
    val pages by produceState<List<Bitmap>?>(null, t.Id) {
        value = withContext(Dispatchers.IO) { TicketStore.render(context, t, widthPx) }
    }
    var scale by remember(t.Id) { mutableFloatStateOf(1f) }
    var offset by remember(t.Id) { mutableStateOf(Offset.Zero) }
    var box by remember { mutableStateOf(IntSize.Zero) }
    val zoomed = scale > 1.01f
    LaunchedEffect(zoomed) { onZoomed(zoomed) }
    DisposableEffect(t.Id) { onDispose { onZoomed(false) } }

    // Never lets the content leave the screen: the scaled view (around its centre) moves at most by its overflow.
    fun clamp(o: Offset, s: Float): Offset {
        val maxX = (s - 1f) * box.width / 2f
        val maxY = (s - 1f) * box.height / 2f
        return Offset(o.x.coerceIn(-maxX, maxX), o.y.coerceIn(-maxY, maxY))
    }
    // New scale s2 keeping the content point under `focus` (box coordinates) where it is.
    fun zoomAround(focus: Offset, s2: Float) {
        val centre = Offset(box.width / 2f, box.height / 2f)
        val target = s2.coerceIn(1f, MAX_ZOOM)
        val rel = focus - centre
        offset = if (target <= 1.01f) Offset.Zero else clamp(rel - (rel - offset) * (target / scale), target)
        scale = target
    }

    val p = pages
    when {
        p == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Palette.Accent) }
        p.isEmpty() -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Text("Fichier introuvable ou illisible.", color = Palette.TextMuted) }
        else -> Box(
            Modifier.fillMaxSize().clipToBounds().onSizeChanged { box = it }
                .pointerInput(t.Id) {
                    detectTapGestures(onDoubleTap = { at -> zoomAround(at, if (scale > 1.01f) 1f else DOUBLE_TAP_ZOOM) })
                }
                .pointerInput(t.Id) {
                    // Read before the page scroll and the ticket pager (Initial pass): two fingers, or any drag once
                    // zoomed, are ours; one finger at normal size is left to them.
                    awaitEachGesture {
                        awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                        do {
                            val event = awaitPointerEvent(PointerEventPass.Initial)
                            val fingers = event.changes.count { it.pressed }
                            if (fingers >= 2 || scale > 1.01f) {
                                val zoom = event.calculateZoom()
                                if (fingers >= 2 && zoom != 1f) zoomAround(event.calculateCentroid(useCurrent = true), scale * zoom)
                                if (scale > 1.01f) offset = clamp(offset + event.calculatePan(), scale)
                                event.changes.forEach { if (it.positionChanged()) it.consume() }
                            }
                        } while (event.changes.any { it.pressed })
                    }
                },
        ) {
            Column(
                Modifier.fillMaxSize()
                    .graphicsLayer { scaleX = scale; scaleY = scale; translationX = offset.x; translationY = offset.y }
                    .verticalScroll(rememberScrollState(), enabled = !zoomed)
                    .padding(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                p.forEach { b ->
                    Image(
                        b.asImageBitmap(), null, contentScale = ContentScale.FillWidth,
                        modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(6.dp)).background(Color.White),
                    )
                }
            }
            if (!zoomed) Text(
                "Deux doigts ou double tap pour zoomer", fontSize = 11.sp, color = Palette.TextFaint,
                modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 10.dp),
            )
        }
    }
}

// ---- The Billets list (shortcut, calendar header): coming up first, then past ones ----

@Composable
fun TicketsHub(db: AkashicDatabase, onClose: () -> Unit, openCurrent: Boolean = false) {
    val scope = rememberCoroutineScope()
    var version by remember { mutableStateOf(0) }
    var coming by remember { mutableStateOf<List<Ticket>>(emptyList()) }
    var past by remember { mutableStateOf<List<Ticket>>(emptyList()) }
    var loaded by remember { mutableStateOf(false) }
    var showPast by remember { mutableStateOf(false) }
    var viewing by remember { mutableStateOf<Pair<List<Ticket>, Int>?>(null) }
    var adding by remember { mutableStateOf<List<TicketStore.Incoming>?>(null) }
    val pick = rememberTicketPicker { adding = it }

    LaunchedEffect(version) {
        val today = LocalDate.now().toString()
        withContext(Dispatchers.IO) {
            coming = db.ticketDao().from(today)
            past = db.ticketDao().before(today)
        }
        loaded = true
    }
    // From the « Billets » shortcut: straight to today's tickets (or the next ones), the list behind.
    LaunchedEffect(openCurrent) {
        if (!openCurrent) return@LaunchedEffect
        val now = withContext(Dispatchers.IO) { TicketStore.current(db) }
        if (now.isNotEmpty()) viewing = now to 0
    }

    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Palette.AppBg).systemBarsPadding()) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 4.dp)) {
                TextButton(onClick = onClose) { Text("←", fontSize = 18.sp) }
                Text("Billets", fontSize = 20.sp, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
                TextButton(onClick = pick) { Text("+ Ajouter") }
            }
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                if (loaded && coming.isEmpty()) item {
                    Text(
                        "Aucun billet à venir. Depuis SNCF Connect, Trainline ou un mail : Partager → « Billet Akashic ». Ou + Ajouter.",
                        color = Palette.TextMuted, modifier = Modifier.padding(16.dp),
                    )
                }
                coming.groupBy { it.Date }.forEach { (date, group) ->
                    item(key = "h$date") { DayHeader(date) }
                    items(group, key = { it.Id }) { t -> TicketRow(t) { viewing = group to group.indexOf(t) } }
                }
                if (past.isNotEmpty()) {
                    item(key = "past") {
                        TextButton(onClick = { showPast = !showPast }, modifier = Modifier.padding(start = 8.dp, top = 12.dp)) {
                            Text((if (showPast) "▾ " else "▸ ") + "Passés (${past.size})", color = Palette.TextMuted)
                        }
                    }
                    if (showPast) past.groupBy { it.Date }.forEach { (date, group) ->
                        item(key = "hp$date") { DayHeader(date) }
                        items(group, key = { "p${it.Id}" }) { t -> TicketRow(t) { viewing = group to group.indexOf(t) } }
                    }
                }
            }
        }
        viewing?.let { (list, i) -> TicketViewer(db, list, i, onClose = { viewing = null }, onChanged = { version++ }) }
        adding?.let { items -> AttachTicketsSheet(db, items) { saved -> adding = null; if (saved) version++ } }
    }
}

@Composable
private fun DayHeader(iso: String) {
    val d = runCatching { LocalDate.parse(iso) }.getOrNull()
    Text(
        d?.let { ticketDay(it) + if (it.year != LocalDate.now().year) " ${it.year}" else "" } ?: iso,
        fontSize = 13.sp, fontWeight = FontWeight.Bold, color = Palette.TextMuted,
        modifier = Modifier.padding(start = 16.dp, top = 12.dp, bottom = 2.dp),
    )
}

@Composable
private fun TicketRow(t: Ticket, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 3.dp).clip(RoundedCornerShape(10.dp))
            .background(Palette.Surface1).clickable(onClick = onClick).padding(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text("🎫", fontSize = 20.sp)
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text(t.Title, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                listOf(t.EventTitle, if (t.isPdf) "PDF" else "Image").filter { it.isNotBlank() }.joinToString(" · "),
                fontSize = 12.sp, color = Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

// Chip under a calendar row or day: « 🎫 2 billets », opening them.
@Composable
fun TicketChip(count: Int, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Text(
        "🎫 " + if (count == 1) "1 billet" else "$count billets",
        fontSize = 12.sp, color = Palette.Accent,
        modifier = modifier.clip(RoundedCornerShape(6.dp)).clickable(onClick = onClick)
            .background(Palette.Accent.copy(alpha = 0.12f)).padding(horizontal = 6.dp, vertical = 2.dp),
    )
}
