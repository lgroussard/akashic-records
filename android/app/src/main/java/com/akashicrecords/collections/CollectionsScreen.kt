package com.akashicrecords.collections

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Artwork
import com.akashicrecords.data.ArtworkEvaluation
import com.akashicrecords.data.Observation
import com.akashicrecords.journaux.AutoSave
import com.akashicrecords.journaux.ConfirmDelete
import com.akashicrecords.journaux.nowIso
import com.akashicrecords.media.LocalImage
import com.akashicrecords.media.MediaFiles
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private val TIERS = listOf("S", "A", "B", "C", "D")

// Collections on the phone: one category's works as a grid of covers, grouped by tier; a tap opens the fiche
// (cover, title, tier, scores, observations); + adds a work. (Same tables as the PC: a PC import replaces them,
// until the server syncs both ways.)
@Composable
fun CollectionGrid(db: AkashicDatabase, category: Int) {
    val scope = rememberCoroutineScope()
    var open by remember { mutableStateOf<Int?>(null) }
    var works by remember { mutableStateOf<List<Artwork>>(emptyList()) }
    var version by remember { mutableStateOf(0) }
    LaunchedEffect(version, open, category) {
        if (open == null) works = withContext(Dispatchers.IO) { db.artworkDao().getAll() }.filter { it.Category == category }
    }

    open?.let { id -> ArtworkPage(db, id, onBack = { open = null; version++ }); return }

    Box(Modifier.fillMaxSize()) {
        if (works.isEmpty()) Text("Rien dans cette catégorie. Touche + pour ajouter une œuvre.", color = Palette.TextMuted, modifier = Modifier.padding(16.dp))
        LazyVerticalGrid(
            columns = GridCells.Adaptive(104.dp),
            contentPadding = PaddingValues(start = 10.dp, end = 10.dp, bottom = 88.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier.fillMaxSize(),
        ) {
            TIERS.forEachIndexed { tier, name ->
                val inTier = works.filter { it.Tier == tier }.sortedBy { it.Title.lowercase() }
                if (inTier.isEmpty()) return@forEachIndexed
                item(span = { GridItemSpan(maxLineSpan) }, key = "tier$tier") {
                    Text(
                        "Tier $name · ${inTier.size}", fontWeight = FontWeight.Bold,
                        color = Palette.tierColors.getOrElse(tier) { Palette.Accent }, modifier = Modifier.padding(top = 10.dp, start = 2.dp),
                    )
                }
                items(inTier, key = { it.Id }) { w -> CoverTile(w) { open = w.Id } }
            }
        }
        FloatingActionButton(
            onClick = {
                scope.launch {
                    open = withContext(Dispatchers.IO) {
                        db.artworkDao().add(Artwork(Title = "Nouvelle œuvre", Category = category, Tier = 2, CreatedAt = nowIso(), CoverImagePath = null, WatchPriority = 0)).toInt()
                    }
                }
            },
            containerColor = Palette.Accent,
            modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp),
        ) { Text("+", fontSize = 24.sp) }
    }
}

// A cover (2:3), or the title on a tinted card when there is none.
@Composable
private fun CoverTile(w: Artwork, onClick: () -> Unit) {
    // A cover whose file is not on this phone (imported without its media) shows the title, like no cover.
    val context = LocalContext.current
    val hasCover = remember(w.CoverImagePath) { MediaFiles.resolve(context, w.CoverImagePath)?.isFile == true }
    Column(Modifier.clickable(onClick = onClick)) {
        Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(8.dp)).background(Palette.Surface2), contentAlignment = Alignment.Center) {
            if (hasCover) LocalImage(w.CoverImagePath, Modifier.fillMaxSize(), maxSide = 400)
            else Text(w.Title, fontSize = 13.sp, textAlign = TextAlign.Center, color = Palette.TextMuted, modifier = Modifier.padding(8.dp), maxLines = 5, overflow = TextOverflow.Ellipsis)
        }
        Text(w.Title, fontSize = 12.sp, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 3.dp))
    }
}

@Composable
private fun ArtworkPage(db: AkashicDatabase, id: Int, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var work by remember { mutableStateOf<Artwork?>(null) }
    var scores by remember { mutableStateOf<List<Pair<String, Int>>>(emptyList()) }
    var observations by remember { mutableStateOf<List<Observation>>(emptyList()) }
    var confirm by remember { mutableStateOf(false) }
    var subject by remember { mutableStateOf("") }
    var content by remember { mutableStateOf("") }

    suspend fun reloadObservations() { observations = withContext(Dispatchers.IO) { db.observationDao().forArtwork(id) } }
    LaunchedEffect(id) {
        withContext(Dispatchers.IO) {
            work = db.artworkDao().getById(id)
            val axes = db.evaluationAxisDao().getAll().associate { it.Id to it.Name }
            scores = db.artworkEvaluationDao().forArtwork(id).map { (axes[it.AxisId] ?: "Axe") to it.Score }
        }
        reloadObservations()
    }
    val w = work ?: return
    AutoSave(w) { v -> withContext(Dispatchers.IO) { db.artworkDao().update(v) } }
    val pickCover = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri != null) scope.launch {
            val path = withContext(Dispatchers.IO) { runCatching { MediaFiles.import(context, uri, "covers") }.getOrNull() }
            if (path != null) work = w.copy(CoverImagePath = path)
        }
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp)) {
        TextButton(onClick = onBack) { Text("← Collection") }
        Row {
            Box(
                Modifier.width(120.dp).aspectRatio(2f / 3f).clip(RoundedCornerShape(8.dp)).background(Palette.Surface2).clickable { pickCover.launch("image/*") },
                contentAlignment = Alignment.Center,
            ) {
                if (MediaFiles.resolve(context, w.CoverImagePath)?.isFile == true) LocalImage(w.CoverImagePath, Modifier.fillMaxSize(), maxSide = 600)
                else Text("+ Couverture", fontSize = 12.sp, color = Palette.TextMuted)
            }
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                BasicTextField(
                    value = w.Title, onValueChange = { work = w.copy(Title = it) },
                    textStyle = TextStyle(color = Palette.Text, fontSize = 20.sp, fontWeight = FontWeight.Bold),
                    cursorBrush = SolidColor(Palette.Accent), modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(10.dp))
                Text("TIER", fontSize = 11.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold)
                Row {
                    TIERS.forEachIndexed { i, name ->
                        val color = Palette.tierColors.getOrElse(i) { Palette.Accent }
                        Box(
                            Modifier.padding(end = 6.dp, top = 4.dp).size(36.dp).clip(RoundedCornerShape(8.dp))
                                .background(if (w.Tier == i) color.copy(alpha = 0.35f) else Palette.Surface3)
                                .clickable { work = w.copy(Tier = i) },
                            contentAlignment = Alignment.Center,
                        ) { Text(name, fontWeight = FontWeight.Bold, color = color) }
                    }
                }
                if (w.CoverImagePath != null) TextButton(onClick = { work = w.copy(CoverImagePath = null) }) { Text("Retirer la couverture", fontSize = 12.sp, color = Palette.TextMuted) }
            }
        }

        if (scores.isNotEmpty()) {
            Text("NOTES", fontSize = 11.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 16.dp, bottom = 4.dp))
            scores.forEach { (axis, score) -> Text("$axis : $score", color = Palette.TextMuted) }
        }

        Text("OBSERVATIONS", fontSize = 11.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 16.dp, bottom = 4.dp))
        observations.forEach { o ->
            Row(Modifier.fillMaxWidth().padding(vertical = 4.dp).clip(RoundedCornerShape(8.dp)).background(Palette.Surface1).padding(10.dp)) {
                Column(Modifier.weight(1f)) {
                    if (o.Subject.isNotBlank()) Text(o.Subject, fontWeight = FontWeight.SemiBold)
                    Text(o.Content, color = Palette.TextMuted, fontSize = 14.sp)
                }
                TextButton(onClick = { scope.launch { withContext(Dispatchers.IO) { db.observationDao().deleteById(o.Id) }; reloadObservations() } }) {
                    Text("✕", color = Palette.TextFaint)
                }
            }
        }
        OutlinedTextField(subject, { subject = it }, label = { Text("Sujet") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(content, { content = it }, label = { Text("Observation") }, minLines = 2, modifier = Modifier.fillMaxWidth().padding(top = 4.dp))
        TextButton(
            enabled = content.isNotBlank(),
            onClick = {
                val s = subject.trim()
                val c = content.trim()
                subject = ""; content = ""
                scope.launch {
                    withContext(Dispatchers.IO) { db.observationDao().add(Observation(ArtworkId = id, Subject = s, Content = c, CreatedAt = nowIso())) }
                    reloadObservations()
                }
            },
        ) { Text("Ajouter l'observation") }

        TextButton(onClick = { confirm = true }, modifier = Modifier.padding(top = 16.dp)) { Text("Supprimer l'œuvre", color = Palette.TextMuted) }
        Spacer(Modifier.height(32.dp))
    }
    if (confirm) ConfirmDelete("« ${w.Title} », ses notes et ses observations", onConfirm = {
        confirm = false
        scope.launch {
            withContext(Dispatchers.IO) {
                observations.forEach { db.observationDao().deleteById(it.Id) }
                db.artworkEvaluationDao().forArtwork(id).forEach { db.artworkEvaluationDao().deleteById(it.Id) }
                db.artworkDao().deleteById(id)
            }
            onBack()
        }
    }, onDismiss = { confirm = false })
}
