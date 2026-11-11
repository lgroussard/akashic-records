package com.akashicrecords.crossword

import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.CrosswordGame
import com.akashicrecords.journaux.ConfirmDelete
import com.akashicrecords.journaux.nowIso
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Plus → Jeux → Mots croisés (phone only): grids generated from the bundled word lists, or imported as .ipuz files.
// [onExit]: back to the list of games.
@Composable
fun CrosswordSection(db: AkashicDatabase, onExit: () -> Unit = {}, initialOpen: Int? = null) {
    var openId by remember(initialOpen) { mutableStateOf(initialOpen) }
    val id = openId
    if (id == null) GameList(db, onOpen = { openId = it }, onExit = onExit)
    else GameScreen(db, id, onBack = { openId = null })
}

// ---- List of grids ----

@Composable
private fun GameList(db: AkashicDatabase, onOpen: (Int) -> Unit, onExit: () -> Unit) {
    BackHandler(onBack = onExit)
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var games by remember { mutableStateOf<List<CrosswordGame>?>(null) }
    var version by remember { mutableStateOf(0) }
    var generating by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf<CrosswordGame?>(null) }

    LaunchedEffect(version) {
        games = withContext(Dispatchers.IO) {
            val dao = db.crosswordGameDao()
            // Grids made before they were numbered were called "Grille 9×9": they get their number, in creation order.
            val all = dao.getAll()
            val old = Regex("^Grille \\d+×\\d+$")
            if (all.any { old.matches(it.Title) }) {
                all.sortedBy { it.CreatedAt }.forEachIndexed { i, g ->
                    if (old.matches(g.Title)) dao.update(g.copy(Title = "Grille n°${i + 1}"))
                }
                dao.getAll()
            } else all
        }
    }

    // A generated grid is named by its number ("Grille n°12"); an imported one keeps its own title.
    suspend fun save(puzzle: Puzzle, source: String, title: String? = null): Int = withContext(Dispatchers.IO) {
        val now = nowIso()
        val name = title ?: "Grille n°${db.crosswordGameDao().getAll().size + 1}"
        db.crosswordGameDao().add(
            CrosswordGame(
                Title = name, Source = source, PuzzleJson = puzzle.toJson(),
                Progress = puzzle.solution.joinToString("\n") { row -> row.map { if (it == '#') '#' else '.' }.joinToString("") },
                CreatedAt = now, UpdatedAt = now, CompletedAt = null,
            ),
        ).toInt()
    }

    // One button: the size (7 to 9) is picked at random.
    fun newGrid() {
        if (generating) return
        generating = true
        val size = (7..9).random()
        scope.launch {
            val puzzle = withContext(Dispatchers.Default) {
                val words = WordList.load(context)
                // Words of the last 15 grids are left out, so the next ones do not repeat them.
                val recent = withContext(Dispatchers.IO) { db.crosswordGameDao().latestPuzzles(15) }
                    .flatMap { runCatching { Puzzle.fromJson(it).clues.map { c -> c.answer } }.getOrDefault(emptyList()) }.toSet()
                Generator.generate(words, size, avoid = recent)
            }
            val id = save(puzzle, "Générée")
            generating = false
            onOpen(id)
        }
    }

    val importer = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            val result = withContext(Dispatchers.IO) {
                runCatching {
                    val name = uri.lastPathSegment?.substringAfterLast('/')?.substringBeforeLast('.') ?: "Grille importée"
                    val text = context.contentResolver.openInputStream(uri)!!.use { it.readBytes().decodeToString() }
                    Ipuz.parse(text, name).also { require(it.clues.isNotEmpty()) { "aucune définition dans ce fichier" } } to name
                }
            }
            result.onSuccess { (puzzle, name) -> onOpen(save(puzzle, "Importée", title = puzzle.title.ifBlank { name })) }
                .onFailure { Toast.makeText(context, "Fichier illisible (.ipuz attendu) : ${it.message}", Toast.LENGTH_LONG).show() }
        }
    }

    Column(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
        TextButton(onClick = onExit, modifier = Modifier.padding(top = 4.dp)) { Text("← Jeux") }
        val list = games
        val current = list?.firstOrNull { it.CompletedAt == null }
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(bottom = 4.dp)) {
            Button(onClick = { newGrid() }, enabled = !generating) { Text(if (generating) "Création…" else "Nouvelle grille") }
            Spacer(Modifier.width(8.dp))
            // The grid in progress, one tap away.
            if (current != null) OutlinedButton(onClick = { onOpen(current.Id) }) { Text("Continuer") }
            if (generating) {
                Spacer(Modifier.width(8.dp))
                CircularProgressIndicator(Modifier.size(22.dp), color = Palette.Accent, strokeWidth = 2.dp)
            }
        }

        when {
            list == null -> {}
            list.isEmpty() -> Text("Aucune grille pour l'instant : touche « Nouvelle grille ».", color = Palette.TextMuted, modifier = Modifier.padding(top = 16.dp))
            else -> LazyColumn(Modifier.weight(1f).padding(top = 8.dp)) {
                items(list, key = { it.Id }) { g -> GameRow(g, onOpen = { onOpen(g.Id) }, onDelete = { deleting = g }) }
            }
        }
        TextButton(onClick = { importer.launch(arrayOf("*/*")) }) { Text("Importer une grille (.ipuz)…", fontSize = 13.sp) }
        Spacer(Modifier.weight(if (list.isNullOrEmpty()) 1f else 0.001f))
        Text(
            "Définitions : Akashic et Wiktionnaire (contributeurs du Wiktionnaire, licence CC BY-SA).",
            fontSize = 10.sp, color = Palette.TextFaint, modifier = Modifier.padding(vertical = 8.dp),
        )
    }

    deleting?.let { g ->
        ConfirmDelete("la grille « ${g.Title} »", onConfirm = {
            deleting = null
            scope.launch { withContext(Dispatchers.IO) { db.crosswordGameDao().deleteById(g.Id) }; version++ }
        }, onDismiss = { deleting = null })
    }
}

private val SHORT_DATE = java.time.format.DateTimeFormatter.ofPattern("d MMM", java.util.Locale.FRENCH)

// A grid of the list: its picture (filled squares in blue), name, size and date, and how far along it is.
@Composable
private fun GameRow(g: CrosswordGame, onOpen: () -> Unit, onDelete: () -> Unit) {
    val puzzle = remember(g.Id) { runCatching { Puzzle.fromJson(g.PuzzleJson) }.getOrNull() }
    val thumb by produceState<android.graphics.Bitmap?>(null, g.Id, g.Progress) {
        value = puzzle?.let { p -> withContext(Dispatchers.Default) { Thumbnail.render(p, g.Progress, 180) } }
    }
    val done = g.CompletedAt != null
    val share = Thumbnail.filled(g.Progress)
    Row(
        Modifier.fillMaxWidth().padding(vertical = 5.dp).clip(RoundedCornerShape(12.dp)).background(Palette.Surface1)
            .clickable(onClick = onOpen).padding(10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(60.dp).clip(RoundedCornerShape(6.dp)).background(Palette.Surface3), contentAlignment = Alignment.Center) {
            thumb?.let { androidx.compose.foundation.Image(it.asImageBitmap(), contentDescription = null, modifier = Modifier.fillMaxSize()) }
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(g.Title, fontSize = 16.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            val date = com.akashicrecords.journaux.datePart(g.CreatedAt)?.format(SHORT_DATE).orEmpty()
            Text(
                listOfNotNull(puzzle?.let { "${it.cols} × ${it.rows}" }, date.ifBlank { null }, g.Source.takeIf { it != "Générée" }?.lowercase())
                    .joinToString(" · "),
                fontSize = 12.sp, color = Palette.TextMuted,
            )
            Spacer(Modifier.height(6.dp))
            if (done) Text("✓ Terminée", fontSize = 12.sp, color = Palette.Accent)
            else Row(verticalAlignment = Alignment.CenterVertically) {
                LinearProgressIndicator(progress = { share }, Modifier.weight(1f).height(4.dp), color = Palette.Accent)
                Text("  ${(share * 100).toInt()} %", fontSize = 11.sp, color = Palette.TextMuted)
            }
        }
        TextButton(onClick = onDelete) { Text("✕", color = Palette.TextFaint) }
    }
}

// ---- Playing a grid ----

@Composable
private fun GameScreen(db: AkashicDatabase, id: Int, onBack: () -> Unit) {
    val game by produceState<CrosswordGame?>(null, id) { value = withContext(Dispatchers.IO) { db.crosswordGameDao().getById(id) } }
    BackHandler(onBack = onBack)
    val g = game ?: return
    val puzzle = remember(g.Id) { Puzzle.fromJson(g.PuzzleJson) }
    Playing(db, g, puzzle, onBack)
}

@Composable
private fun Playing(db: AkashicDatabase, game: CrosswordGame, puzzle: Puzzle, onBack: () -> Unit) {
    val context = LocalContext.current
    val rows = puzzle.rows
    val cols = puzzle.cols
    // Letters typed, row-major ('.' = empty).
    val letters = remember(game.Id) {
        val saved = game.Progress.split('\n')
        mutableStateListOf<Char>().apply {
            for (r in 0 until rows) for (c in 0 until cols) {
                val ch = saved.getOrNull(r)?.getOrNull(c) ?: '.'
                add(if (puzzle.isBlack(r, c)) '#' else if (ch in 'A'..'Z') ch else '.')
            }
        }
    }
    fun at(r: Int, c: Int) = letters[r * cols + c]
    val firstClue = puzzle.clues.minByOrNull { it.number * 2 + if (it.across) 0 else 1 }
    var row by remember(game.Id) { mutableStateOf(firstClue?.row ?: 0) }
    var col by remember(game.Id) { mutableStateOf(firstClue?.col ?: 0) }
    var across by remember(game.Id) { mutableStateOf(firstClue?.across ?: true) }
    var wrong by remember(game.Id) { mutableStateOf<Set<Int>>(emptySet()) }
    var completed by remember(game.Id) { mutableStateOf(game.CompletedAt != null) }
    var revealAll by remember { mutableStateOf(false) }

    val clue = puzzle.clueAt(row, col, across) ?: puzzle.clueAt(row, col, !across)
    val wordCells = clue?.cells()?.toSet() ?: emptySet()
    val ordered = remember(puzzle) { puzzle.clues.sortedWith(compareBy({ !it.across }, { it.number })) }

    // Progress saved shortly after each change; the grid is finished when every letter is right.
    val progress = (0 until rows).joinToString("\n") { r -> (0 until cols).map { c -> at(r, c) }.joinToString("") }
    LaunchedEffect(progress) {
        delay(400)
        val solved = (0 until rows).all { r -> (0 until cols).all { c -> puzzle.isBlack(r, c) || at(r, c) == puzzle.letter(r, c) } }
        if (solved && !completed) {
            completed = true
            Toast.makeText(context, "Bravo, grille terminée !", Toast.LENGTH_LONG).show()
        }
        withContext(Dispatchers.IO) {
            db.crosswordGameDao().update(
                game.copy(Progress = progress, UpdatedAt = nowIso(), CompletedAt = if (solved) game.CompletedAt ?: nowIso() else null),
            )
        }
    }

    fun select(r: Int, c: Int) {
        if (puzzle.isBlack(r, c)) return
        if (r == row && c == col) {
            if (puzzle.clueAt(r, c, !across) != null) across = !across
        } else {
            row = r; col = c
            if (puzzle.clueAt(r, c, across) == null) across = !across
        }
    }
    fun goTo(target: Clue) {
        across = target.across
        // The first empty square of the word, else its start.
        val (r, c) = target.cells().firstOrNull { (r, c) -> at(r, c) == '.' } ?: (target.row to target.col)
        row = r; col = c
    }
    fun type(ch: Char) {
        letters[row * cols + col] = ch
        wrong = wrong - (row * cols + col)
        val cells = clue?.cells() ?: return
        val i = cells.indexOf(row to col)
        cells.getOrNull(i + 1)?.let { (r, c) -> row = r; col = c }
    }
    fun erase() {
        val index = row * cols + col
        if (letters[index] != '.') { letters[index] = '.'; return }
        val cells = clue?.cells() ?: return
        val i = cells.indexOf(row to col)
        cells.getOrNull(i - 1)?.let { (r, c) -> row = r; col = c; letters[r * cols + c] = '.' }
    }

    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 4.dp)) {
            TextButton(onClick = onBack) { Text("← Grilles") }
            Text(
                if (completed) "${game.Title} · terminée" else game.Title,
                fontSize = 14.sp, color = if (completed) Palette.Accent else Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis,
            )
        }

        // The grid
        // The grid takes what the clue, the helps and the keyboard leave: it shrinks on a short screen instead of
        // pushing the keyboard (and its ⌫) off the bottom.
        BoxWithConstraints(Modifier.fillMaxWidth().weight(1f).padding(horizontal = 12.dp), contentAlignment = Alignment.Center) {
            val cell = minOf(maxWidth / cols, maxHeight / rows, 48.dp)
            Column(Modifier.border(1.dp, Palette.TextFaint)) {
                for (r in 0 until rows) Row {
                    for (c in 0 until cols) {
                        val black = puzzle.isBlack(r, c)
                        val index = r * cols + c
                        val selected = r == row && c == col
                        val inWord = (r to c) in wordCells
                        Box(
                            Modifier.size(cell)
                                .background(
                                    when {
                                        black -> Color(0xFF0A0A0E)
                                        selected -> Palette.Accent.copy(alpha = 0.55f)
                                        inWord -> Palette.Accent.copy(alpha = 0.18f)
                                        else -> Color(0xFFEDEDF2)
                                    },
                                )
                                .border(0.5.dp, Color(0xFF55556A))
                                .clickable(enabled = !black) { select(r, c) },
                        ) {
                            puzzle.numbers[r to c]?.let { n ->
                                Text("$n", fontSize = 8.sp, color = Color(0xFF33334A), modifier = Modifier.padding(start = 2.dp))
                            }
                            val ch = letters[index]
                            if (!black && ch != '.') {
                                Text(
                                    ch.toString(), fontSize = (cell.value * 0.5f).sp, fontWeight = FontWeight.SemiBold,
                                    color = if (index in wrong) Color(0xFFD03030) else Color(0xFF15151F),
                                    textAlign = TextAlign.Center, modifier = Modifier.align(Alignment.Center),
                                )
                            }
                        }
                    }
                }
            }
        }

        // Current clue, with the previous / next one
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 8.dp).clip(RoundedCornerShape(8.dp))
                .background(Palette.Surface1),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TextButton(onClick = {
                val i = ordered.indexOf(clue)
                ordered.getOrNull(if (i <= 0) ordered.lastIndex else i - 1)?.let(::goTo)
            }) { Text("‹", fontSize = 20.sp) }
            Text(
                clue?.let { "${it.number} ${if (it.across) "→" else "↓"}  ${it.text.ifBlank { "(sans définition)" }}" } ?: "",
                fontSize = 15.sp, modifier = Modifier.weight(1f).padding(vertical = 6.dp), maxLines = 3, overflow = TextOverflow.Ellipsis,
            )
            TextButton(onClick = {
                val i = ordered.indexOf(clue)
                ordered.getOrNull(if (i < 0 || i >= ordered.lastIndex) 0 else i + 1)?.let(::goTo)
            }) { Text("›", fontSize = 20.sp) }
        }

        // Help
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), horizontalArrangement = Arrangement.SpaceEvenly) {
            TextButton(onClick = {
                wrong = letters.indices.filter { i -> letters[i] in 'A'..'Z' && letters[i] != puzzle.solution[i / cols][i % cols] }.toSet()
                Toast.makeText(context, if (wrong.isEmpty()) "Aucune erreur" else "${wrong.size} lettre(s) fausse(s)", Toast.LENGTH_SHORT).show()
            }) { Text("Vérifier") }
            TextButton(onClick = { type(puzzle.letter(row, col)) }) { Text("Une lettre") }
            TextButton(onClick = { clue?.cells()?.forEach { (r, c) -> letters[r * cols + c] = puzzle.letter(r, c) } }) { Text("Le mot") }
            TextButton(onClick = { revealAll = true }) { Text("Solution", color = Palette.TextMuted) }
        }

        Keyboard(onLetter = ::type, onErase = ::erase)
    }

    if (revealAll) {
        AlertDialog(
            onDismissRequest = { revealAll = false },
            containerColor = Palette.Surface1,
            title = { Text("Afficher la solution ?") },
            text = { Text("Toute la grille sera remplie.") },
            confirmButton = {
                TextButton(onClick = {
                    revealAll = false
                    for (r in 0 until rows) for (c in 0 until cols) if (!puzzle.isBlack(r, c)) letters[r * cols + c] = puzzle.letter(r, c)
                }) { Text("Afficher") }
            },
            dismissButton = { TextButton(onClick = { revealAll = false }) { Text("Annuler") } },
        )
    }
}

// A–Z keyboard (AZERTY): crosswords have no accents, and the grid keeps the screen instead of the system keyboard.
@Composable
private fun Keyboard(onLetter: (Char) -> Unit, onErase: () -> Unit) {
    val rows = listOf("AZERTYUIOP", "QSDFGHJKLM", "WXCVBN")
    Column(Modifier.fillMaxWidth().background(Palette.Surface1).padding(horizontal = 3.dp, vertical = 6.dp)) {
        rows.forEachIndexed { i, keys ->
            Row(Modifier.fillMaxWidth().padding(vertical = 3.dp), horizontalArrangement = Arrangement.Center) {
                keys.forEach { k -> Key(k.toString(), Modifier.weight(1f)) { onLetter(k) } }
                if (i == rows.lastIndex) {
                    Key("⌫  Effacer", Modifier.weight(4f), highlight = true) { onErase() }
                }
            }
        }
    }
}

@Composable
private fun Key(label: String, modifier: Modifier, highlight: Boolean = false, onClick: () -> Unit) {
    Box(
        modifier.padding(horizontal = 2.dp).height(46.dp).clip(RoundedCornerShape(6.dp))
            .background(if (highlight) Palette.Accent.copy(alpha = 0.35f) else Palette.Surface3).clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) { Text(label, fontSize = 18.sp) }
}
