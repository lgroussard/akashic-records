package com.akashicrecords.calendar

import android.app.DatePickerDialog
import android.app.TimePickerDialog
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.SwipeInList
import com.akashicrecords.SwipePages
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Birthday
import com.akashicrecords.data.CalendarEvent
import com.akashicrecords.data.CalendarEventLink
import com.akashicrecords.journaux.dateIso
import com.akashicrecords.journaux.nowIso
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.LocalDate
import java.time.YearMonth
import java.time.temporal.ChronoUnit
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle
import java.util.Locale

// Calendar pager size: 200 years of months, today in the middle.
private const val MONTH_PAGES = 2400
private val DAY_HEADER = DateTimeFormatter.ofPattern("EEEE d MMMM", Locale.FRENCH)
private val SHORT_DAY = DateTimeFormatter.ofPattern("EEE d MMM", Locale.FRENCH)
// "décembre 2026": the format form. TextStyle.FULL_STANDALONE gave an empty name for December.
internal val MONTH_TITLE: DateTimeFormatter = DateTimeFormatter.ofPattern("MMMM yyyy", Locale.FRENCH)

private sealed interface Editing {
    data class Event(val event: CalendarEvent?, val day: LocalDate) : Editing
    data class Bday(val birthday: Birthday?, val day: LocalDate) : Editing
}

// Desktop CalendarView, phone layout: compact month grid with colored dots, the selected day's
// items under it, then "Prochainement" (next 30 days). + adds an event or a birthday; tapping an
// item edits it. Project deadlines are shown (edited in Organisation on the PC).
@Composable
fun CalendarSection(db: AkashicDatabase, openDate: LocalDate? = null, onOpenLink: (CalendarEventLink) -> Unit = {}) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var month by remember { mutableStateOf(YearMonth.from(openDate ?: LocalDate.now())) }
    var selected by remember { mutableStateOf(openDate ?: LocalDate.now()) }
    var monthItems by remember { mutableStateOf<List<AgendaItem>>(emptyList()) }
    var upcoming by remember { mutableStateOf<List<AgendaItem>>(emptyList()) }
    var version by remember { mutableStateOf(0) }
    var editing by remember { mutableStateOf<Editing?>(null) }
    var addMenu by remember { mutableStateOf(false) }
    // Links made on the PC (an event or a birthday → a poem, a recipe, a fiche…), by owner "Event:12" / "Birthday:12".
    var links by remember { mutableStateOf<Map<String, List<CalendarEventLink>>>(emptyMap()) }

    LaunchedEffect(month, version) {
        monthItems = withContext(Dispatchers.IO) { Agenda.month(db, month) }
        val today = LocalDate.now()
        upcoming = withContext(Dispatchers.IO) { Agenda.range(db, today, today.plusDays(30)) }
        links = withContext(Dispatchers.IO) { db.calendarEventLinkDao().getAll().groupBy { "${it.OwnerType}:${it.OwnerId}" } }
    }
    fun linksOf(item: AgendaItem) = when (item.source) {
        AgendaSource.Event -> links["Event:${item.refId}"]
        AgendaSource.Birthday -> links["Birthday:${item.refId}"]
        AgendaSource.Deadline -> null
    }.orEmpty()

    fun open(item: AgendaItem) {
        scope.launch {
            editing = withContext(Dispatchers.IO) {
                when (item.source) {
                    AgendaSource.Event -> db.calendarEventDao().getAll().firstOrNull { it.Id == item.refId }?.let { Editing.Event(it, item.date) }
                    AgendaSource.Birthday -> db.birthdayDao().getAll().firstOrNull { it.Id == item.refId }?.let { Editing.Bday(it, item.date) }
                    AgendaSource.Deadline -> null
                }
            }
        }
    }

    Box(Modifier.fillMaxSize()) {
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
            item {
                MonthHeader(month, onPrev = { month = month.minusMonths(1) }, onNext = { month = month.plusMonths(1) }) {
                    month = YearMonth.now()
                    selected = LocalDate.now()
                }
                // Swipe left/right to change month: the grid follows the finger (SwipePages). Pages are
                // months counted from a fixed origin, ±100 years around it.
                val origin = remember { YearMonth.now() }
                SwipePages(
                    count = MONTH_PAGES,
                    page = MONTH_PAGES / 2 + ChronoUnit.MONTHS.between(origin, month).toInt(),
                    onPageChange = { month = origin.plusMonths((it - MONTH_PAGES / 2).toLong()) },
                    modifier = SwipeInList,
                ) { p ->
                    val shown = origin.plusMonths((p - MONTH_PAGES / 2).toLong())
                    // Each month loads its own dots, so the neighbour already has them when it slides in.
                    val items by produceState(emptyList<AgendaItem>(), shown, version) {
                        value = withContext(Dispatchers.IO) { Agenda.month(db, shown) }
                    }
                    MonthGrid(shown, selected, items) { selected = it }
                }
                Text(
                    selected.format(DAY_HEADER).replaceFirstChar { it.uppercase() },
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.padding(start = 16.dp, top = 10.dp, bottom = 2.dp),
                )
            }
            val ofDay = monthItems.filter { it.date == selected }
            if (ofDay.isEmpty()) {
                item { Text("Rien ce jour-là.", color = Palette.TextFaint, fontSize = 13.sp, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) }
            }
            items(ofDay, key = { "d:" + it.notificationKey }) { AgendaRow(it, showDate = false, linksOf(it), onOpenLink) { open(it) } }

            item {
                Text(
                    "PROCHAINEMENT",
                    fontSize = 11.sp, fontWeight = FontWeight.Bold, color = Palette.TextFaint,
                    modifier = Modifier.padding(start = 16.dp, top = 16.dp, bottom = 2.dp),
                )
            }
            val next = upcoming.filter { !it.isSpanContinuation }
            if (next.isEmpty()) {
                item { Text("Rien dans les 30 prochains jours.", color = Palette.TextFaint, fontSize = 13.sp, modifier = Modifier.padding(horizontal = 16.dp)) }
            }
            items(next, key = { "u:" + it.notificationKey }) { AgendaRow(it, showDate = true, linksOf(it), onOpenLink) { open(it) } }
        }

        Box(Modifier.align(Alignment.BottomEnd).padding(16.dp)) {
            FloatingActionButton(onClick = { addMenu = true }, containerColor = Palette.Accent) {
                Icon(Icons.Filled.Add, contentDescription = "Ajouter")
            }
            DropdownMenu(expanded = addMenu, onDismissRequest = { addMenu = false }) {
                DropdownMenuItem(text = { Text("Événement") }, onClick = { addMenu = false; editing = Editing.Event(null, selected) })
                DropdownMenuItem(text = { Text("Anniversaire") }, onClick = { addMenu = false; editing = Editing.Bday(null, selected) })
            }
        }
    }

    when (val e = editing) {
        is Editing.Event -> EventEditor(db, e.event, e.day, onDone = { editing = null; version++; Reminders.checkNow(context) })
        is Editing.Bday -> BirthdayEditor(db, e.birthday, e.day, onDone = { editing = null; version++; Reminders.checkNow(context) })
        null -> {}
    }
}

@Composable
private fun MonthHeader(month: YearMonth, onPrev: () -> Unit, onNext: () -> Unit, onToday: () -> Unit) {
    // Month on the left on one line, controls on the right: "Décembre 2026" never wraps (it used
    // to, squeezed by "Auj.", and only "2026" stayed visible).
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(
            month.format(MONTH_TITLE).replaceFirstChar { it.uppercase() },
            fontWeight = FontWeight.Bold,
            fontSize = 18.sp,
            maxLines = 1,
            softWrap = false,
            modifier = Modifier.weight(1f),
        )
        if (month != YearMonth.now()) TextButton(onClick = onToday) { Text("Auj.") }
        TextButton(onClick = onPrev) { Text("‹", fontSize = 22.sp) }
        TextButton(onClick = onNext) { Text("›", fontSize = 22.sp) }
    }
}

// Monday-first grid; each day shows up to 3 colored dots (one per item). Adjacent months blank.
@Composable
private fun MonthGrid(month: YearMonth, selected: LocalDate, items: List<AgendaItem>, onPick: (LocalDate) -> Unit) {
    val byDay = items.groupBy { it.date }
    val today = LocalDate.now()
    val offset = month.atDay(1).dayOfWeek.value - 1
    val cells = offset + month.lengthOfMonth()
    Column(Modifier.padding(horizontal = 8.dp)) {
        Row {
            listOf("L", "M", "M", "J", "V", "S", "D").forEach {
                Text(it, Modifier.weight(1f), textAlign = TextAlign.Center, fontSize = 11.sp, color = Palette.TextFaint)
            }
        }
        for (week in 0 until (cells + 6) / 7) {
            Row {
                for (dow in 0 until 7) {
                    val n = week * 7 + dow - offset + 1
                    Box(Modifier.weight(1f).height(46.dp).padding(1.5.dp), contentAlignment = Alignment.Center) {
                        if (n in 1..month.lengthOfMonth()) {
                            val d = month.atDay(n)
                            val dayItems = byDay[d].orEmpty()
                            Column(
                                Modifier.fillMaxSize()
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(if (d == selected) Palette.AccentSoft else Palette.Surface1)
                                    .then(if (d == today) Modifier.border(BorderStroke(1.dp, Palette.Accent), RoundedCornerShape(8.dp)) else Modifier)
                                    .clickable { onPick(d) },
                                horizontalAlignment = Alignment.CenterHorizontally,
                                verticalArrangement = Arrangement.Center,
                            ) {
                                Text("$n", fontSize = 13.sp, fontWeight = if (d == today) FontWeight.Bold else FontWeight.Normal)
                                Row(Modifier.height(8.dp), verticalAlignment = Alignment.CenterVertically) {
                                    dayItems.take(3).forEach {
                                        Box(Modifier.padding(horizontal = 1.dp).size(5.dp).clip(CircleShape).background(it.color))
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun AgendaRow(
    item: AgendaItem,
    showDate: Boolean,
    links: List<CalendarEventLink> = emptyList(),
    onOpenLink: (CalendarEventLink) -> Unit = {},
    onClick: () -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().clickable(enabled = item.source != AgendaSource.Deadline, onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(10.dp).clip(CircleShape).background(item.color))
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text(item.title, maxLines = 1, overflow = TextOverflow.Ellipsis)
            val label = when (item.source) {
                AgendaSource.Birthday -> "Anniversaire"
                AgendaSource.Deadline -> "Échéance"
                AgendaSource.Event -> item.kind?.label ?: ""
            }
            val sub = listOf(label, item.subtitle).filter { it.isNotBlank() }.joinToString(" · ")
            Text(sub, fontSize = 11.sp, color = Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis)
            // Items linked on the PC (a poem for a birthday…): a tap opens them in their module.
            links.forEach { link ->
                Text(
                    "↗ " + link.Title.ifBlank { link.Kind },
                    fontSize = 12.sp, color = Palette.Accent, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 3.dp).clip(RoundedCornerShape(6.dp))
                        .clickable { onOpenLink(link) }
                        .background(Palette.Accent.copy(alpha = 0.12f)).padding(horizontal = 6.dp, vertical = 2.dp),
                )
            }
        }
        Column(horizontalAlignment = Alignment.End) {
            if (showDate) Text(relativeDay(item.date), fontSize = 12.sp, color = Palette.TextMuted)
            if (item.timeText.isNotEmpty()) Text(item.timeText, fontSize = 12.sp, color = Palette.Text)
        }
    }
}

private fun relativeDay(d: LocalDate): String {
    val today = LocalDate.now()
    return when (d) {
        today -> "Aujourd'hui"
        today.plusDays(1) -> "Demain"
        else -> d.format(SHORT_DAY)
    }
}

// ---- Editors ----

@Composable
private fun pickDate(initial: LocalDate, onPicked: (LocalDate) -> Unit): () -> Unit {
    val context = LocalContext.current
    return {
        DatePickerDialog(context, { _, y, m, d -> onPicked(LocalDate.of(y, m + 1, d)) }, initial.year, initial.monthValue - 1, initial.dayOfMonth).show()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun EventEditor(db: AkashicDatabase, existing: CalendarEvent?, day: LocalDate, onDone: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var title by remember { mutableStateOf(existing?.Title ?: "") }
    var kind by remember { mutableStateOf(existing?.let { EventKind.of(it.Kind) } ?: EventKind.Sortie) }
    var date by remember { mutableStateOf(existing?.let { Agenda.day(it.Date) } ?: day) }
    var endDate by remember { mutableStateOf(existing?.let { Agenda.day(it.EndDate) }) }
    var hour by remember { mutableStateOf(existing?.StartHour) }
    var minute by remember { mutableStateOf(existing?.StartMinute) }
    var yearly by remember { mutableStateOf(existing?.RecurringYearly ?: false) }
    var location by remember { mutableStateOf(existing?.Location ?: "") }
    var notes by remember { mutableStateOf(existing?.Notes ?: "") }
    var confirmDelete by remember { mutableStateOf(false) }

    val chooseDate = pickDate(date) { date = it; if (endDate != null && endDate!! < it) endDate = it }
    val chooseEnd = pickDate(endDate ?: date) { endDate = if (it > date) it else null }

    ModalBottomSheet(onDismissRequest = onDone, containerColor = Palette.Surface1) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
            Text(if (existing == null) "Nouvel événement" else "Événement", fontWeight = FontWeight.Bold, fontSize = 17.sp)
            OutlinedTextField(title, { title = it }, label = { Text("Titre") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            Row(Modifier.horizontalScroll(rememberScrollState()).padding(vertical = 6.dp)) {
                EventKind.entries.forEach { k ->
                    FilterChip(
                        selected = k == kind,
                        onClick = { kind = k },
                        label = { Text(k.label) },
                        leadingIcon = { Box(Modifier.size(8.dp).clip(CircleShape).background(k.color)) },
                    )
                    Spacer(Modifier.width(4.dp))
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                OutlinedButton(onClick = chooseDate) { Text(date.format(SHORT_DAY)) }
                Spacer(Modifier.width(8.dp))
                OutlinedButton(onClick = chooseEnd) { Text(endDate?.let { "→ " + it.format(SHORT_DAY) } ?: "+ jour de fin") }
                if (endDate != null) TextButton(onClick = { endDate = null }) { Text("✕") }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Toute la journée", modifier = Modifier.weight(1f))
                Switch(checked = hour == null, onCheckedChange = { allDay ->
                    if (allDay) { hour = null; minute = null } else {
                        TimePickerDialog(context, { _, h, m -> hour = h; minute = m }, 20, 0, true).show()
                    }
                })
            }
            if (hour != null) {
                TextButton(onClick = {
                    TimePickerDialog(context, { _, h, m -> hour = h; minute = m }, hour ?: 20, minute ?: 0, true).show()
                }) { Text("À %02d:%02d".format(hour, minute ?: 0)) }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Tous les ans", modifier = Modifier.weight(1f))
                Switch(checked = yearly, onCheckedChange = { yearly = it })
            }
            OutlinedTextField(location, { location = it }, label = { Text("Lieu") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(notes, { notes = it }, label = { Text("Notes") }, minLines = 2, modifier = Modifier.fillMaxWidth())
            Spacer(Modifier.height(10.dp))
            Row {
                if (existing != null) TextButton(onClick = { confirmDelete = true }) { Text("Supprimer", color = Palette.TextMuted) }
                Spacer(Modifier.weight(1f))
                TextButton(onClick = onDone) { Text("Annuler") }
                Button(onClick = {
                    val t = title.trim().ifEmpty { kind.label }
                    scope.launch {
                        withContext(Dispatchers.IO) {
                            val row = CalendarEvent(
                                Id = existing?.Id ?: 0,
                                Title = t,
                                Kind = kind.ordinal,
                                Date = dateIso(date),
                                EndDate = endDate?.let { dateIso(it) },
                                StartHour = hour,
                                StartMinute = if (hour != null) minute ?: 0 else null,
                                RecurringYearly = yearly,
                                Location = location.trim(),
                                Notes = notes.trim(),
                                CreatedAt = existing?.CreatedAt ?: nowIso(),
                            )
                            if (existing == null) db.calendarEventDao().add(row) else db.calendarEventDao().update(row)
                        }
                        onDone()
                    }
                }) { Text("Enregistrer") }
            }
        }
    }

    if (confirmDelete && existing != null) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Supprimer « ${existing.Title} » ?") },
            text = { Text("Cette suppression est définitive.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmDelete = false
                    scope.launch {
                        withContext(Dispatchers.IO) { db.calendarEventDao().deleteById(existing.Id) }
                        onDone()
                    }
                }) { Text("Supprimer") }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Annuler") } },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun BirthdayEditor(db: AkashicDatabase, existing: Birthday?, day: LocalDate, onDone: () -> Unit) {
    val scope = rememberCoroutineScope()
    var name by remember { mutableStateOf(existing?.Name ?: "") }
    var monthDay by remember { mutableStateOf(existing?.let { Agenda.safeDate(2024, it.Month, it.Day) } ?: day.withYear(2024)) }
    var year by remember { mutableStateOf(existing?.BirthYear?.toString() ?: "") }
    var notes by remember { mutableStateOf(existing?.Notes ?: "") }
    var confirmDelete by remember { mutableStateOf(false) }
    val choose = pickDate(monthDay.withYear(LocalDate.now().year)) { monthDay = Agenda.safeDate(2024, it.monthValue, it.dayOfMonth) }

    ModalBottomSheet(onDismissRequest = onDone, containerColor = Palette.Surface1) {
        Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
            Text(if (existing == null) "Nouvel anniversaire" else "Anniversaire", fontWeight = FontWeight.Bold, fontSize = 17.sp)
            OutlinedTextField(name, { name = it }, label = { Text("Prénom / nom") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(vertical = 6.dp)) {
                OutlinedButton(onClick = choose) { Text(monthDay.format(DateTimeFormatter.ofPattern("d MMMM", Locale.FRENCH))) }
                Spacer(Modifier.width(8.dp))
                OutlinedTextField(
                    year, { v -> year = v.filter { it.isDigit() }.take(4) },
                    label = { Text("Année (facultatif)") }, singleLine = true, modifier = Modifier.weight(1f),
                )
            }
            OutlinedTextField(notes, { notes = it }, label = { Text("Notes (idées cadeau…)") }, minLines = 2, modifier = Modifier.fillMaxWidth())
            Spacer(Modifier.height(10.dp))
            Row {
                if (existing != null) TextButton(onClick = { confirmDelete = true }) { Text("Supprimer", color = Palette.TextMuted) }
                Spacer(Modifier.weight(1f))
                TextButton(onClick = onDone) { Text("Annuler") }
                Button(onClick = {
                    val n = name.trim()
                    if (n.isEmpty()) return@Button
                    scope.launch {
                        withContext(Dispatchers.IO) {
                            val row = Birthday(
                                Id = existing?.Id ?: 0, Name = n, Month = monthDay.monthValue, Day = monthDay.dayOfMonth,
                                BirthYear = year.toIntOrNull()?.takeIf { it in 1900..2100 }, Notes = notes.trim(),
                            )
                            if (existing == null) db.birthdayDao().add(row) else db.birthdayDao().update(row)
                        }
                        onDone()
                    }
                }) { Text("Enregistrer") }
            }
        }
    }

    if (confirmDelete && existing != null) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Supprimer l'anniversaire de ${existing.Name} ?") },
            text = { Text("Cette suppression est définitive.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmDelete = false
                    scope.launch {
                        withContext(Dispatchers.IO) { db.birthdayDao().deleteById(existing.Id) }
                        onDone()
                    }
                }) { Text("Supprimer") }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Annuler") } },
        )
    }
}
