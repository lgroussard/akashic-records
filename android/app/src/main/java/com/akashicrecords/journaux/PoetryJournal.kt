package com.akashicrecords.journaux

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Poem
import com.akashicrecords.data.Recueil
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Desktop "Recueil de poésie": search + recueil filter + poem list, then one poem page where
// the text dominates (serif italic by default) with alignment controls.
//
// Rich text: the desktop keeps both Text (plain) and RichContent (a XAML FlowDocument with
// per-word formatting) and prefers RichContent when it is set. The phone edits plain text
// only, so when the text is changed here RichContent is cleared: the desktop then rebuilds the
// poem from Text, keeping the words but losing per-word formatting from the PC. Poems only
// read on the phone are left untouched.
@Composable
internal fun PoetryJournal(db: AkashicDatabase, initialOpen: Int? = null) {
    val scope = rememberCoroutineScope()
    var poems by remember { mutableStateOf<List<Poem>>(emptyList()) }
    var recueils by remember { mutableStateOf<List<Recueil>>(emptyList()) }
    var search by remember { mutableStateOf("") }
    var recueilFilter by remember { mutableStateOf<Int?>(null) }
    var openId by remember { mutableStateOf(initialOpen) }
    var newRecueil by remember { mutableStateOf(false) }

    suspend fun reload() {
        withContext(Dispatchers.IO) {
            poems = db.poemDao().getAll()
            recueils = db.recueilDao().getAll()
        }
    }
    LaunchedEffect(Unit) { reload() }

    val filtered = poems.filter { p ->
        (recueilFilter == null || p.RecueilId == recueilFilter) &&
            (search.isBlank() || listOf(p.Title, p.Text, p.Tags).any { it.contains(search, true) })
    }

    val open = openId
    if (open != null) {
        BackHandler { openId = null; scope.launch { reload() } }
        val index = filtered.indexOfFirst { it.Id == open }
        PageNav(
            onBack = { openId = null; scope.launch { reload() } },
            onPrev = filtered.getOrNull(index - 1)?.let { p -> { openId = p.Id } },
            onNext = filtered.getOrNull(index + 1)?.takeIf { index >= 0 }?.let { n -> { openId = n.Id } },
            onDelete = null,
        )
        key(open) {
            PoemLoader(db, open, recueils, onDeleted = { openId = null; scope.launch { reload() } })
        }
        return
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                search, { search = it },
                placeholder = { Text("Rechercher dans les poèmes") },
                singleLine = true,
                modifier = Modifier.weight(1f),
            )
            Spacer(Modifier.width(8.dp))
            Button(onClick = {
                scope.launch {
                    val id = withContext(Dispatchers.IO) {
                        db.poemDao().add(
                            Poem(
                                RecueilId = recueilFilter, Title = "Sans titre", Text = "", Tags = "", ImagePath = null,
                                CreatedAt = nowIso(), TextAlignment = "Left", Margin = 0.0, FontSize = 19.0,
                                FontFamily = "Georgia", Bold = false, Italic = true, RichContent = "", EditorWidth = 900.0,
                            ),
                        ).toInt()
                    }
                    reload()
                    search = ""
                    openId = id
                }
            }) { Text("+") }
        }
        Row(
            Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            FilterChip(selected = recueilFilter == null, onClick = { recueilFilter = null }, label = { Text("Tous") })
            recueils.forEach { r ->
                Spacer(Modifier.width(4.dp))
                FilterChip(
                    selected = recueilFilter == r.Id,
                    onClick = { recueilFilter = if (recueilFilter == r.Id) null else r.Id },
                    label = { Text(r.Title) },
                )
            }
            TextButton(onClick = { newRecueil = true }) { Text("+ recueil") }
        }
        if (filtered.isEmpty()) {
            Text(if (poems.isEmpty()) "Aucun poème. « + » pour en écrire un." else "Aucun résultat.", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
        }
        val names = recueils.associate { it.Id to it.Title }
        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            items(filtered, key = { it.Id }) { p ->
                Column(Modifier.fillMaxWidth().clickable { openId = p.Id }.padding(horizontal = 12.dp, vertical = 7.dp)) {
                    Text(p.Title.ifBlank { "Sans titre" }, fontFamily = FontFamily.Serif, fontStyle = FontStyle.Italic, fontSize = 16.sp)
                    val first = p.Text.lineSequence().firstOrNull { it.isNotBlank() }
                    val sub = listOfNotNull(p.RecueilId?.let { names[it] }, first).joinToString(" · ")
                    if (sub.isNotEmpty()) Text(sub, fontSize = 11.sp, color = Palette.TextMuted, maxLines = 1)
                }
            }
        }
    }

    if (newRecueil) {
        var title by remember { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { newRecueil = false },
            title = { Text("Nouveau recueil") },
            text = { OutlinedTextField(title, { title = it }, singleLine = true) },
            confirmButton = {
                TextButton(onClick = {
                    val t = title.trim()
                    newRecueil = false
                    if (t.isNotEmpty()) scope.launch {
                        withContext(Dispatchers.IO) { db.recueilDao().add(Recueil(Title = t, CreatedAt = nowIso(), Summary = "")) }
                        reload()
                    }
                }) { Text("Créer") }
            },
            dismissButton = { TextButton(onClick = { newRecueil = false }) { Text("Annuler") } },
        )
    }
}

@Composable
private fun PoemLoader(db: AkashicDatabase, id: Int, recueils: List<Recueil>, onDeleted: () -> Unit) {
    var poem by remember { mutableStateOf<Poem?>(null) }
    LaunchedEffect(id) {
        awaitPendingWrites()
        poem = withContext(Dispatchers.IO) { db.poemDao().getById(id) }
    }
    poem?.let { PoemPage(db, it, recueils, onDeleted) }
}

@Composable
private fun PoemPage(db: AkashicDatabase, original: Poem, recueils: List<Recueil>, onDeleted: () -> Unit) {
    var p by remember { mutableStateOf(original) }
    var recueilMenu by remember { mutableStateOf(false) }
    var confirm by remember { mutableStateOf(false) }

    AutoSave(p) { v -> withContext(Dispatchers.IO) { db.poemDao().update(v) } }

    val look = p.look()
    val align = when (look.align) {
        "Center" -> TextAlign.Center
        "Right" -> TextAlign.Right
        "Justify" -> TextAlign.Justify
        else -> TextAlign.Left
    }
    val serif = !p.FontFamily.contains("sans", ignoreCase = true) && !p.FontFamily.contains("segoe", ignoreCase = true)
    val poemStyle = TextStyle(
        color = Palette.Text,
        fontFamily = if (serif) FontFamily.Serif else FontFamily.SansSerif,
        fontStyle = if (look.italic) FontStyle.Italic else FontStyle.Normal,
        fontWeight = if (look.bold) FontWeight.Bold else FontWeight.Normal,
        fontSize = (look.fontSize * 0.9).coerceIn(12.0, 32.0).sp,
        lineHeight = (look.fontSize * 1.35).coerceIn(18.0, 44.0).sp,
        textAlign = align,
    )

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 12.dp)) {
        BasicTextField(
            value = p.Title,
            onValueChange = { p = p.copy(Title = it) },
            textStyle = TextStyle(color = Palette.Text, fontFamily = FontFamily.Serif, fontSize = 22.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center),
            cursorBrush = SolidColor(Palette.Accent),
            modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box {
                TextButton(onClick = { recueilMenu = true }) {
                    Text("Recueil : " + (recueils.firstOrNull { it.Id == p.RecueilId }?.Title ?: "aucun") + " ▾", fontSize = 12.sp)
                }
                DropdownMenu(expanded = recueilMenu, onDismissRequest = { recueilMenu = false }) {
                    DropdownMenuItem(text = { Text("Aucun") }, onClick = { p = p.copy(RecueilId = null); recueilMenu = false })
                    recueils.forEach { r ->
                        DropdownMenuItem(text = { Text(r.Title) }, onClick = { p = p.copy(RecueilId = r.Id); recueilMenu = false })
                    }
                }
            }
        }
        // Phone formatting bar (alignment, italic, bold, size): writes the phone columns only.
        Row(Modifier.horizontalScroll(rememberScrollState()), verticalAlignment = Alignment.CenterVertically) {
            listOf("Left" to "⇤", "Center" to "≡", "Right" to "⇥", "Justify" to "☰").forEach { (key, glyph) ->
                FilterChip(selected = look.align == key, onClick = { p = p.withMobileAlign(key) }, label = { Text(glyph) })
                Spacer(Modifier.width(4.dp))
            }
            FilterChip(selected = look.italic, onClick = { p = p.withMobileItalic(!look.italic) }, label = { Text("I", fontStyle = FontStyle.Italic) })
            Spacer(Modifier.width(4.dp))
            FilterChip(selected = look.bold, onClick = { p = p.withMobileBold(!look.bold) }, label = { Text("B", fontWeight = FontWeight.Bold) })
            Spacer(Modifier.width(4.dp))
            TextButton(onClick = { p = p.withMobileSize((look.fontSize - 1).coerceAtLeast(12.0)) }) { Text("A−") }
            TextButton(onClick = { p = p.withMobileSize((look.fontSize + 1).coerceAtMost(36.0)) }) { Text("A+") }
            // Only once the phone has its own layout: back to the PC one, to start over.
            if (look.ownLayout) TextButton(onClick = { p = p.withPcLayout() }) { Text("Reprendre celle du PC", fontSize = 12.sp) }
        }
        BasicTextField(
            value = p.Text,
            onValueChange = { p = p.copy(Text = it, RichContent = "") },
            textStyle = poemStyle,
            cursorBrush = SolidColor(Palette.Accent),
            modifier = Modifier.fillMaxWidth().heightIn(min = 320.dp).padding(vertical = 12.dp),
            decorationBox = { inner ->
                if (p.Text.isEmpty()) Text("Écrire…", style = poemStyle.copy(color = Palette.TextFaint))
                inner()
            },
        )
        Field("Tags", p.Tags, { p = p.copy(Tags = it) })
        TextButton(onClick = { confirm = true }) { Text("Supprimer le poème", color = Palette.TextMuted) }
        Spacer(Modifier.height(24.dp))
    }

    if (confirm) {
        ConfirmDelete("« ${p.Title} »", onConfirm = {
            AppScope.launch {
                db.poemDao().deleteById(p.Id)
                withContext(Dispatchers.Main) { onDeleted() }
            }
        }, onDismiss = { confirm = false })
    }
}
