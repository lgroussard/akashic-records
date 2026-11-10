package com.akashicrecords.journaux

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.JournalEntry
import com.akashicrecords.data.JournalPhoto
import com.akashicrecords.calendar.MONTH_TITLE
import com.akashicrecords.media.LocalImage
import com.akashicrecords.media.MediaFiles
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.time.LocalDate
import java.time.YearMonth
import java.time.format.TextStyle
import java.util.Locale

// Desktop "Journal personnel": calendar + one entry page. Opens on today; no row is created
// until something is typed (EnsureSelectedEntryExists); Préc./Suiv. step through the entries
// that actually exist; the calendar tints the days that have one.
@Composable
internal fun PersonalJournal(db: AkashicDatabase) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var entries by remember { mutableStateOf<List<JournalEntry>>(emptyList()) }
    var date by remember { mutableStateOf(LocalDate.now()) }
    var month by remember { mutableStateOf(YearMonth.now()) }
    var showCalendar by remember { mutableStateOf(false) }

    suspend fun reload() {
        entries = withContext(Dispatchers.IO) { db.journalEntryDao().getAll() }
    }
    // Re-read on every date change so a page reopened after editing never shows a stale copy
    // (the previous page flushes its last edit before leaving).
    var loadedFor by remember { mutableStateOf<LocalDate?>(null) }
    LaunchedEffect(date) {
        awaitPendingWrites()
        reload()
        loadedFor = date
    }

    val byDate = entries.mapNotNull { e -> datePart(e.EntryDate)?.let { it to e } }.toMap()
    val datesWithEntries = byDate.keys.sorted()
    val prev = datesWithEntries.lastOrNull { it < date }
    val next = datesWithEntries.firstOrNull { it > date }

    fun go(d: LocalDate) {
        date = d
        month = YearMonth.from(d)
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = { prev?.let(::go) }, enabled = prev != null) { Text("‹") }
            // One row: tapping the date opens/closes the month picker (▾/▴), "Auj." jumps back.
            Text(
                frenchDate(date) + if (showCalendar) "  ▴" else "  ▾",
                fontWeight = FontWeight.Bold,
                textAlign = TextAlign.Center,
                modifier = Modifier.weight(1f).clickable { showCalendar = !showCalendar }.padding(vertical = 8.dp),
            )
            if (date != LocalDate.now()) TextButton(onClick = { go(LocalDate.now()) }) { Text("Auj.") }
            TextButton(onClick = { next?.let(::go) }, enabled = next != null) { Text("›") }
        }
        if (showCalendar) {
            MonthCalendar(month, date, byDate.keys, onMonth = { month = it }) { go(it); showCalendar = false }
        }

        if (loadedFor == date) key(date) {
            EntryPage(db, date, byDate[date], onChanged = { scope.launch { reload() } }, context = context)
        }
    }
}

@Composable
private fun EntryPage(
    db: AkashicDatabase,
    date: LocalDate,
    existing: JournalEntry?,
    onChanged: () -> Unit,
    context: android.content.Context,
) {
    val scope = rememberCoroutineScope()
    var entryId by remember { mutableStateOf(existing?.Id) }
    var title by remember { mutableStateOf(existing?.Title ?: "") }
    var text by remember { mutableStateOf(existing?.Text ?: "") }
    var tags by remember { mutableStateOf(existing?.Tags ?: "") }
    var photos by remember { mutableStateOf<List<JournalPhoto>>(emptyList()) }
    var confirmPhoto by remember { mutableStateOf<JournalPhoto?>(null) }
    var confirmEntry by remember { mutableStateOf(false) }
    val lock = remember { Mutex() }

    LaunchedEffect(entryId) {
        entryId?.let { id -> photos = withContext(Dispatchers.IO) { db.journalPhotoDao().forEntry(id) } }
    }

    // Creates the row on first need (typing or adding a photo), never just by browsing.
    suspend fun ensureEntry(t: String, x: String, g: String): Int = lock.withLock {
        entryId ?: withContext(Dispatchers.IO) {
            db.journalEntryDao().add(JournalEntry(Title = t, Text = x, EntryDate = dateIso(date), Tags = g)).toInt()
        }.also { entryId = it; onChanged() }
    }

    AutoSave(Triple(title, text, tags)) { (t, x, g) ->
        val id = entryId
        if (id == null) {
            if (t.isNotBlank() || x.isNotBlank() || g.isNotBlank()) ensureEntry(t, x, g)
        } else {
            withContext(Dispatchers.IO) {
                db.journalEntryDao().update(JournalEntry(Id = id, Title = t, Text = x, EntryDate = existing?.EntryDate ?: dateIso(date), Tags = g))
            }
        }
    }

    val pickPhoto = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) scope.launch {
            val id = ensureEntry(title, text, tags)
            withContext(Dispatchers.IO) {
                val rel = MediaFiles.import(context, uri, "photos")
                db.journalPhotoDao().add(JournalPhoto(EntryId = id, ImagePath = rel))
            }
            photos = withContext(Dispatchers.IO) { db.journalPhotoDao().forEntry(id) }
        }
    }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 12.dp),
    ) {
        Field("Titre", title, { title = it })
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            placeholder = { Text("Écrire la journée…") },
            minLines = 10,
            modifier = Modifier.fillMaxWidth().padding(vertical = 3.dp),
        )
        Field("Tags (séparés par des virgules)", tags, { tags = it })

        Row(Modifier.horizontalScroll(rememberScrollState()).padding(vertical = 6.dp)) {
            photos.forEach { p ->
                Box(Modifier.padding(end = 6.dp)) {
                    LocalImage(p.ImagePath, Modifier.size(110.dp).clip(RoundedCornerShape(8.dp)), maxSide = 400)
                    Text(
                        "✕",
                        fontSize = 12.sp,
                        modifier = Modifier.align(Alignment.TopEnd).padding(3.dp)
                            .clip(CircleShape).background(Palette.AppBg).clickable { confirmPhoto = p }
                            .padding(horizontal = 6.dp, vertical = 2.dp),
                    )
                }
            }
            OutlinedButton(
                onClick = { pickPhoto.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                modifier = Modifier.size(110.dp),
                shape = RoundedCornerShape(8.dp),
            ) { Text("+ Photo") }
        }
        if (entryId != null) {
            TextButton(onClick = { confirmEntry = true }) { Text("Supprimer cette entrée", color = Palette.TextMuted) }
        }
        Spacer(Modifier.height(24.dp))
    }

    confirmPhoto?.let { p ->
        ConfirmDelete("cette photo", onConfirm = {
            scope.launch {
                withContext(Dispatchers.IO) {
                    MediaFiles.delete(context, p.ImagePath)
                    db.journalPhotoDao().deleteById(p.Id)
                }
                photos = photos - p
            }
        }, onDismiss = { confirmPhoto = null })
    }
    if (confirmEntry) {
        ConfirmDelete("l'entrée du ${frenchDate(date)}", onConfirm = {
            val id = entryId ?: return@ConfirmDelete
            scope.launch {
                withContext(Dispatchers.IO) {
                    db.journalPhotoDao().forEntry(id).forEach {
                        MediaFiles.delete(context, it.ImagePath)
                        db.journalPhotoDao().deleteById(it.Id)
                    }
                    db.journalEntryDao().deleteById(id)
                }
                entryId = null
                title = ""; text = ""; tags = ""; photos = emptyList()
                onChanged()
            }
        }, onDismiss = { confirmEntry = false })
    }
}

@Composable
private fun MonthCalendar(
    month: YearMonth,
    selected: LocalDate,
    marked: Set<LocalDate>,
    onMonth: (YearMonth) -> Unit,
    onPick: (LocalDate) -> Unit,
) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = { onMonth(month.minusMonths(1)) }) { Text("‹") }
            Text(
                month.format(MONTH_TITLE).replaceFirstChar { it.uppercase() },
                textAlign = TextAlign.Center,
                modifier = Modifier.weight(1f),
            )
            TextButton(onClick = { onMonth(month.plusMonths(1)) }) { Text("›") }
        }
        Row {
            listOf("L", "M", "M", "J", "V", "S", "D").forEach {
                Text(it, Modifier.weight(1f), textAlign = TextAlign.Center, fontSize = 11.sp, color = Palette.TextFaint)
            }
        }
        // Monday-first grid; days of adjacent months are left blank (as on the desktop).
        val offset = month.atDay(1).dayOfWeek.value - 1
        val cells = offset + month.lengthOfMonth()
        val today = LocalDate.now()
        for (week in 0 until (cells + 6) / 7) {
            Row {
                for (dow in 0 until 7) {
                    val dayNum = week * 7 + dow - offset + 1
                    Box(Modifier.weight(1f).aspectRatio(1.2f).padding(2.dp), contentAlignment = Alignment.Center) {
                        if (dayNum in 1..month.lengthOfMonth()) {
                            val d = month.atDay(dayNum)
                            val isSel = d == selected
                            Box(
                                Modifier.fillMaxSize()
                                    .clip(RoundedCornerShape(6.dp))
                                    .background(if (d in marked) Palette.AccentSoft else Palette.Surface1)
                                    .then(if (isSel) Modifier.border(BorderStroke(1.5.dp, Palette.Accent), RoundedCornerShape(6.dp)) else Modifier)
                                    .clickable { onPick(d) },
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(
                                    "$dayNum",
                                    fontSize = 13.sp,
                                    fontWeight = if (d == today) FontWeight.Bold else FontWeight.Normal,
                                    color = if (d == today) Palette.Accent else Palette.Text,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}
