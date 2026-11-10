package com.akashicrecords.reading

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ArtisticProject
import com.akashicrecords.data.Artwork
import com.akashicrecords.data.Book
import com.akashicrecords.data.BookHighlight
import com.akashicrecords.data.ConfigStore
import com.akashicrecords.journaux.nowIso
import com.akashicrecords.media.LocalImage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Lecture (phone only): the shelf of imported epubs, and the reader. The open book is remembered (ConfigStore), so
// coming back to the section — or opening it from its home icon — lands on the page where reading stopped.
@Composable
fun LectureSection(db: AkashicDatabase, config: ConfigStore) {
    var openId by remember { mutableStateOf(config.lectureOpenBook()) }
    val open = openId
    if (open != null) {
        ReaderScreen(db, config, open) {
            openId = null
            config.saveLectureOpenBook(null)
        }
    } else {
        Shelf(db) { id ->
            openId = id
            config.saveLectureOpenBook(id)
        }
    }
}

// ---------------------------------------------------------------- shelf

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun Shelf(db: AkashicDatabase, onOpen: (Int) -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var books by remember { mutableStateOf<List<Book>?>(null) }
    var status by remember { mutableStateOf<String?>(null) }
    var info by remember { mutableStateOf<Book?>(null) }
    var version by remember { mutableStateOf(0) }

    LaunchedEffect(version) { books = withContext(Dispatchers.IO) { db.bookDao().getAll() } }

    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) scope.launch {
            status = "Import…"
            status = try {
                val book = ReadingStore.import(context, db, uri)
                version++
                "« ${book.Title} » ajouté."
            } catch (e: Exception) {
                e.message ?: "Ce fichier n'est pas un epub lisible."
            }
        }
    }

    Box(Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize()) {
            status?.let { Text(it, fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp)) }
            val list = books
            when {
                list == null -> {}
                list.isEmpty() -> Text(
                    "Aucun livre. « + » pour importer un epub depuis le téléphone (Téléchargements, Drive…).",
                    color = Palette.TextMuted, modifier = Modifier.padding(16.dp),
                )
                else -> LazyVerticalGrid(
                    GridCells.Adaptive(110.dp),
                    contentPadding = PaddingValues(start = 12.dp, end = 12.dp, top = 4.dp, bottom = 88.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalArrangement = Arrangement.spacedBy(14.dp),
                ) {
                    items(list, key = { it.Id }) { b ->
                        Column(Modifier.combinedClickable(onClick = { onOpen(b.Id) }, onLongClick = { info = b })) {
                            BookCover(b, Modifier.fillMaxWidth().aspectRatio(0.66f))
                            Spacer(Modifier.height(4.dp))
                            Text(b.Title, fontSize = 13.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            if (b.Author.isNotBlank()) Text(b.Author, fontSize = 11.sp, color = Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            if (b.Progress > 0) {
                                Spacer(Modifier.height(3.dp))
                                LinearProgressIndicator(progress = { b.Progress.toFloat() }, Modifier.fillMaxWidth().height(3.dp), color = Palette.Accent)
                            }
                        }
                    }
                }
            }
        }
        FloatingActionButton(
            onClick = { pick.launch(arrayOf("application/epub+zip", "application/octet-stream")) },
            containerColor = Palette.Accent,
            modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp),
        ) { Icon(Icons.Filled.Add, contentDescription = "Importer un epub") }
    }

    info?.let { b -> BookSheet(db, b, onDismiss = { info = null; version++ }) }
}

@Composable
private fun BookCover(b: Book, modifier: Modifier) {
    if (b.CoverPath != null) {
        LocalImage(b.CoverPath, modifier.clip(RoundedCornerShape(6.dp)), maxSide = 600)
    } else {
        Box(modifier.clip(RoundedCornerShape(6.dp)).background(Palette.Surface3).padding(8.dp), contentAlignment = Alignment.Center) {
            Text(b.Title, fontSize = 13.sp, fontStyle = FontStyle.Italic, textAlign = TextAlign.Center, maxLines = 5, overflow = TextOverflow.Ellipsis)
        }
    }
}

// Long press on a book: its Collections fiche (create, link to another, tier) and deletion.
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun BookSheet(db: AkashicDatabase, initial: Book, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var book by remember { mutableStateOf(initial) }
    var fiche by remember { mutableStateOf<Artwork?>(null) }
    var livres by remember { mutableStateOf<List<Artwork>>(emptyList()) }
    var choosing by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    var highlightCount by remember { mutableStateOf(0) }

    suspend fun reload() = withContext(Dispatchers.IO) {
        book = db.bookDao().getById(book.Id) ?: book
        val all = db.artworkDao().getAll()
        fiche = book.ArtworkId?.let { id -> all.firstOrNull { it.Id == id } }
        livres = all.filter { it.Category == ReadingLinks.LIVRES }
        highlightCount = db.bookHighlightDao().forBook(book.Id).size
    }
    LaunchedEffect(Unit) { reload() }

    ModalBottomSheet(onDismissRequest = onDismiss, containerColor = Palette.Surface1) {
        Column(Modifier.padding(horizontal = 20.dp).padding(bottom = 24.dp).verticalScroll(rememberScrollState())) {
            Text(book.Title, fontSize = 18.sp, fontWeight = FontWeight.Bold)
            if (book.Author.isNotBlank()) Text(book.Author, color = Palette.TextMuted)
            Text("${(book.Progress * 100).toInt()} % lu · $highlightCount passage(s) surligné(s)", fontSize = 12.sp, color = Palette.TextFaint)

            HorizontalDivider(color = Palette.BorderStrong, modifier = Modifier.padding(vertical = 12.dp))
            Text("Fiche dans Collections → Livres", fontSize = 15.sp)
            val f = fiche
            if (f != null) {
                Text(f.Title, modifier = Modifier.padding(top = 4.dp))
                Row(Modifier.padding(vertical = 6.dp)) {
                    listOf("S", "A", "B", "C", "D").forEachIndexed { tier, label ->
                        FilterChip(
                            selected = f.Tier == tier,
                            onClick = { scope.launch { withContext(Dispatchers.IO) { db.artworkDao().update(f.copy(Tier = tier)) }; reload() } },
                            label = { Text(label) },
                            modifier = Modifier.padding(end = 4.dp),
                        )
                    }
                }
                Text("Les passages surlignés y sont des observations, reliables aux projets artistiques.", fontSize = 11.sp, color = Palette.TextFaint)
                Row {
                    TextButton(onClick = { choosing = true }) { Text("Lier à une autre fiche") }
                    TextButton(onClick = { scope.launch { withContext(Dispatchers.IO) { db.bookDao().update(book.copy(ArtworkId = null)) }; reload() } }) { Text("Délier") }
                }
            } else {
                Text("Pas encore de fiche : elle se crée au premier passage surligné, ou maintenant.", fontSize = 12.sp, color = Palette.TextMuted)
                Row {
                    TextButton(onClick = { scope.launch { ReadingLinks.ensureArtwork(context, db, book); reload() } }) { Text("Créer / retrouver la fiche") }
                    if (livres.isNotEmpty()) TextButton(onClick = { choosing = true }) { Text("Choisir une fiche") }
                }
            }
            if (choosing) {
                livres.forEach { a ->
                    Text(
                        a.Title,
                        modifier = Modifier.fillMaxWidth().clickable {
                            choosing = false
                            scope.launch { withContext(Dispatchers.IO) { db.bookDao().update(book.copy(ArtworkId = a.Id)) }; reload() }
                        }.padding(vertical = 8.dp),
                    )
                }
            }

            HorizontalDivider(color = Palette.BorderStrong, modifier = Modifier.padding(vertical = 12.dp))
            TextButton(onClick = { confirmDelete = true }) { Text("Supprimer le livre du téléphone", color = Palette.TextMuted) }
        }
    }

    if (confirmDelete) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Supprimer « ${book.Title} » ?") },
            text = { Text("Le fichier et la position de lecture sont effacés. La fiche Collections et ses observations restent.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmDelete = false
                    scope.launch { ReadingStore.delete(context, db, book); onDismiss() }
                }) { Text("Supprimer") }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Annuler") } },
        )
    }
}

// ---------------------------------------------------------------- reader

private sealed interface HighlightEdit {
    data class New(val chapter: Int, val start: Int, val end: Int, val text: String) : HighlightEdit
    data class Existing(val highlight: BookHighlight) : HighlightEdit
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ReaderScreen(db: AkashicDatabase, config: ConfigStore, bookId: Int, onClose: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var book by remember { mutableStateOf<Book?>(null) }
    var epub by remember { mutableStateOf<Epub?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var highlights by remember { mutableStateOf<List<BookHighlight>>(emptyList()) }
    var chapter by remember { mutableStateOf(0) }
    var page by remember { mutableStateOf(0) }
    var pages by remember { mutableStateOf(1) }
    var chrome by remember { mutableStateOf(true) }
    var sheet by remember { mutableStateOf<String?>(null) } // "toc" | "look" | "marks"
    var edit by remember { mutableStateOf<HighlightEdit?>(null) }
    var web by remember { mutableStateOf<ReaderWeb?>(null) }
    var pendingMark by remember { mutableStateOf<Int?>(null) }
    val saved = remember { config.readerLook() }
    var look by remember { mutableStateOf(ReaderLook(saved.first, saved.second, saved.third)) }

    LaunchedEffect(bookId) {
        val b = withContext(Dispatchers.IO) { db.bookDao().getById(bookId) }
        if (b == null) { onClose(); return@LaunchedEffect }
        // Highlights and the resume chapter first, the epub last: setting it creates the page, which reads both.
        val opened = withContext(Dispatchers.IO) { runCatching { Epub.open(ReadingStore.fileOf(context, b)) }.getOrElse { e -> error = e.message; null } }
        highlights = withContext(Dispatchers.IO) { db.bookHighlightDao().forBook(b.Id) }
        chapter = ReadingStore.parseLocator(b.Locator).first.coerceIn(0, (opened?.chapters?.size ?: 1) - 1)
        book = b
        epub = opened
    }

    fun marksOf(ch: Int) = highlights.filter { it.Chapter == ch }.mapNotNull { h ->
        val (s, e) = h.Locator.split('-').mapNotNull { it.toIntOrNull() }.takeIf { it.size == 2 } ?: return@mapNotNull null
        BookHighlightMark(h.Id, s, e)
    }

    fun show(ch: Int, progression: Double) {
        val w = web ?: return
        chapter = ch
        page = 0
        w.showChapter(ch, progression, marksOf(ch))
    }

    // Position saved half a second after the page settles.
    LaunchedEffect(chapter, page, pages) {
        val e = epub ?: return@LaunchedEffect
        delay(500)
        val progression = if (pages > 1) page.toDouble() / (pages - 1) else 0.0
        withContext(Dispatchers.IO) { ReadingStore.savePosition(db, bookId, chapter, progression, e.chapters.size) }
    }
    // Highlights redrawn whenever the list changes (saved, edited, deleted).
    LaunchedEffect(highlights, web) { web?.setHighlights(marksOf(chapter)) }

    // Jump to a highlight chosen in the list, once its chapter is on screen.
    LaunchedEffect(pendingMark, chapter) {
        val id = pendingMark ?: return@LaunchedEffect
        delay(450)
        web?.scrollToHighlight(id)
        pendingMark = null
    }

    BackHandler {
        when {
            sheet != null -> sheet = null
            else -> onClose()
        }
    }

    val e = epub
    val b = book
    Box(Modifier.fillMaxSize().background(Color(look.background))) {
        if (error != null) {
            Column(Modifier.padding(16.dp)) {
                Text("Impossible d'ouvrir ce livre : $error", color = Palette.TextMuted)
                TextButton(onClick = onClose) { Text("← Bibliothèque") }
            }
            return@Box
        }
        if (e == null || b == null) return@Box

        val events = remember(e) {
            object : ReaderEvents {
                override fun onPage(page: Int, pages: Int) { chapterPage(page, pages) }
                fun chapterPage(p: Int, n: Int) { page = p; pages = n }
                override fun onEdge(next: Boolean) {
                    if (next && chapter < e.chapters.size - 1) show(chapter + 1, 0.0)
                    else if (!next && chapter > 0) show(chapter - 1, 1.0)
                }
                override fun onToggleChrome() { chrome = !chrome }
                override fun onSelection(start: Int, end: Int, text: String) { edit = HighlightEdit.New(chapter, start, end, text) }
                override fun onHighlightTapped(id: Int) { highlights.firstOrNull { it.Id == id }?.let { edit = HighlightEdit.Existing(it) } }
            }
        }

        AndroidView(
            factory = { ctx ->
                ReaderWeb(ctx, e, events).also { w ->
                    web = w
                    w.linkHandler = { ch -> show(ch, 0.0) }
                    w.applyLook(look)
                    w.showChapter(chapter, ReadingStore.parseLocator(b.Locator).second, marksOf(chapter))
                }
            },
            modifier = Modifier.fillMaxSize(),
        )
        DisposableEffect(Unit) { onDispose { web?.release(); web = null } }

        val chapterTitle = e.toc.lastOrNull { it.chapter <= chapter }?.title ?: "Chapitre ${chapter + 1}"
        if (chrome) {
            Column(Modifier.fillMaxWidth().align(Alignment.TopCenter).background(Palette.Surface1.copy(alpha = 0.96f))) {
                Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    TextButton(onClick = onClose) { Text("←") }
                    Column(Modifier.weight(1f)) {
                        Text(b.Title, fontSize = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(chapterTitle, fontSize = 11.sp, color = Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    TextButton(onClick = { sheet = "toc" }) { Text("☰") }
                    TextButton(onClick = { sheet = "marks" }) { Text("✎") }
                    TextButton(onClick = { sheet = "look" }) { Text("Aa") }
                }
            }
            Column(Modifier.fillMaxWidth().align(Alignment.BottomCenter).background(Palette.Surface1.copy(alpha = 0.96f)).padding(horizontal = 16.dp, vertical = 8.dp)) {
                val overall = ((chapter + if (pages > 1) page.toFloat() / (pages - 1) else 0f) / e.chapters.size).coerceIn(0f, 1f)
                LinearProgressIndicator(progress = { overall }, Modifier.fillMaxWidth().height(3.dp), color = Palette.Accent)
                Text(
                    "Chapitre ${chapter + 1}/${e.chapters.size} · page ${page + 1}/$pages · ${(overall * 100).toInt()} %",
                    fontSize = 11.sp, color = Palette.TextMuted, modifier = Modifier.padding(top = 4.dp),
                )
            }
        }
    }

    if (e == null || b == null) return

    when (sheet) {
        "toc" -> ModalBottomSheet(onDismissRequest = { sheet = null }, containerColor = Palette.Surface1) {
            LazyColumn(Modifier.padding(bottom = 24.dp)) {
                items(e.toc) { t ->
                    Text(
                        t.title,
                        fontWeight = if (t.chapter == chapter) FontWeight.Bold else FontWeight.Normal,
                        modifier = Modifier.fillMaxWidth().clickable { sheet = null; show(t.chapter, 0.0) }
                            .padding(start = (20 + 16 * t.depth).dp, end = 20.dp, top = 10.dp, bottom = 10.dp),
                    )
                }
            }
        }
        "look" -> ModalBottomSheet(onDismissRequest = { sheet = null }, containerColor = Palette.Surface1) {
            fun update(l: ReaderLook) { look = l; web?.applyLook(l); config.saveReaderLook(l.fontScale, l.font, l.theme) }
            Column(Modifier.padding(horizontal = 20.dp).padding(bottom = 28.dp)) {
                Text("Taille du texte", fontSize = 14.sp)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    TextButton(onClick = { update(look.copy(fontScale = (look.fontScale - 10).coerceAtLeast(70))) }) { Text("A−", fontSize = 18.sp) }
                    Text("${look.fontScale} %", modifier = Modifier.width(64.dp), textAlign = TextAlign.Center)
                    TextButton(onClick = { update(look.copy(fontScale = (look.fontScale + 10).coerceAtMost(200))) }) { Text("A+", fontSize = 18.sp) }
                }
                Text("Police", fontSize = 14.sp, modifier = Modifier.padding(top = 8.dp))
                Row {
                    listOf("serif" to "Serif", "sans" to "Sans", "book" to "Celle du livre").forEach { (key, label) ->
                        FilterChip(selected = look.font == key, onClick = { update(look.copy(font = key)) }, label = { Text(label) }, modifier = Modifier.padding(end = 6.dp))
                    }
                }
                Text("Fond", fontSize = 14.sp, modifier = Modifier.padding(top = 8.dp))
                Row {
                    listOf("dark" to "Sombre", "light" to "Clair", "sepia" to "Sépia").forEach { (key, label) ->
                        FilterChip(selected = look.theme == key, onClick = { update(look.copy(theme = key)) }, label = { Text(label) }, modifier = Modifier.padding(end = 6.dp))
                    }
                }
            }
        }
        "marks" -> ModalBottomSheet(onDismissRequest = { sheet = null }, containerColor = Palette.Surface1) {
            if (highlights.isEmpty()) {
                Text(
                    "Aucun passage surligné. Appui long sur le texte pour sélectionner, puis « Surligner ».",
                    color = Palette.TextMuted, modifier = Modifier.padding(20.dp),
                )
            }
            LazyColumn(Modifier.padding(bottom = 24.dp)) {
                items(highlights, key = { it.Id }) { h ->
                    Column(Modifier.fillMaxWidth().clickable {
                        sheet = null
                        if (h.Chapter != chapter) show(h.Chapter, 0.0)
                        pendingMark = h.Id
                    }.padding(horizontal = 20.dp, vertical = 10.dp)) {
                        Text("« ${h.Text.trim()} »", fontStyle = FontStyle.Italic, maxLines = 3, overflow = TextOverflow.Ellipsis)
                        val where = e.toc.lastOrNull { it.chapter <= h.Chapter }?.title ?: "Chapitre ${h.Chapter + 1}"
                        Text(where + if (h.Note.isNotBlank()) " · ${h.Note}" else "", fontSize = 11.sp, color = Palette.TextMuted, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
        }
    }

    edit?.let { current ->
        HighlightDialog(
            db = db,
            edit = current,
            onDismiss = { edit = null },
            onSave = { note, projects ->
                scope.launch {
                    val saved = when (current) {
                        is HighlightEdit.New -> {
                            val chapterTitle = e.toc.lastOrNull { it.chapter <= current.chapter }?.title
                            ReadingLinks.addHighlight(
                                context, db, b,
                                BookHighlight(
                                    BookId = b.Id, Chapter = current.chapter, Text = current.text, Note = note,
                                    Locator = "${current.start}-${current.end}", CreatedAt = nowIso(), ObservationId = null,
                                ),
                                subject = listOfNotNull(b.Title, chapterTitle).joinToString(" — "),
                            )
                        }
                        is HighlightEdit.Existing -> current.highlight.copy(Note = note).also { ReadingLinks.updateHighlight(db, it) }
                    }
                    val before = ReadingLinks.projectsOf(db, saved)
                    for (p in projects - before) ReadingLinks.setProject(db, saved, p, true)
                    for (p in before - projects) ReadingLinks.setProject(db, saved, p, false)
                    highlights = withContext(Dispatchers.IO) { db.bookHighlightDao().forBook(b.Id) }
                    book = withContext(Dispatchers.IO) { db.bookDao().getById(b.Id) } ?: b
                    web?.setHighlights(marksOf(chapter))
                    edit = null
                }
            },
            onDelete = (current as? HighlightEdit.Existing)?.let { ex ->
                {
                    scope.launch {
                        ReadingLinks.deleteHighlight(db, ex.highlight)
                        highlights = withContext(Dispatchers.IO) { db.bookHighlightDao().forBook(b.Id) }
                        web?.setHighlights(marksOf(chapter))
                        edit = null
                    }
                }
            },
        )
    }
}

// New or existing highlight: the passage, a note, the artistic projects it feeds (Reference), delete.
@Composable
private fun HighlightDialog(
    db: AkashicDatabase,
    edit: HighlightEdit,
    onDismiss: () -> Unit,
    onSave: (note: String, projects: Set<Int>) -> Unit,
    onDelete: (() -> Unit)?,
) {
    val text = when (edit) { is HighlightEdit.New -> edit.text; is HighlightEdit.Existing -> edit.highlight.Text }
    var note by remember { mutableStateOf((edit as? HighlightEdit.Existing)?.highlight?.Note.orEmpty()) }
    var projects by remember { mutableStateOf<List<ArtisticProject>>(emptyList()) }
    var chosen by remember { mutableStateOf<Set<Int>>(emptySet()) }
    LaunchedEffect(edit) {
        projects = withContext(Dispatchers.IO) { db.artisticProjectDao().getAll() }
        if (edit is HighlightEdit.Existing) chosen = ReadingLinks.projectsOf(db, edit.highlight)
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (edit is HighlightEdit.New) "Surligner ce passage" else "Passage surligné") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text("« ${text.trim()} »", fontStyle = FontStyle.Italic, maxLines = 8, overflow = TextOverflow.Ellipsis)
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(note, { note = it }, label = { Text("Note (facultatif)") }, modifier = Modifier.fillMaxWidth(), minLines = 2)
                Text(
                    "Enregistré comme observation dans la fiche du livre (Collections → Livres).",
                    fontSize = 11.sp, color = Palette.TextFaint, modifier = Modifier.padding(top = 6.dp),
                )
                if (projects.isNotEmpty()) {
                    Text("Projets artistiques", fontSize = 13.sp, modifier = Modifier.padding(top = 10.dp))
                    projects.forEach { p ->
                        Row(Modifier.fillMaxWidth().clickable { chosen = if (p.Id in chosen) chosen - p.Id else chosen + p.Id }, verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(checked = p.Id in chosen, onCheckedChange = { chosen = if (it) chosen + p.Id else chosen - p.Id })
                            Text(p.Title)
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = { onSave(note.trim(), chosen) }) { Text("Enregistrer") } },
        dismissButton = {
            Row {
                if (onDelete != null) TextButton(onClick = onDelete) { Text("Supprimer", color = Palette.TextMuted) }
                TextButton(onClick = onDismiss) { Text("Annuler") }
            }
        },
    )
}
