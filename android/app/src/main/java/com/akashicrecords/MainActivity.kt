package com.akashicrecords

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ConfigStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

// Mobile entry point. Mirrors the desktop header + section-switcher paradigm:
// four nav sections (Organisation / Journaux / Collections / Archives), each keeping
// its own sub-tab paradigm, rendered over the same Room database. The Pi hub is offline
// for now, so everything reads the on-device SQLite copy directly (100% local).
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val db = AkashicDatabase.get(applicationContext)
        val config = ConfigStore(applicationContext)
        setContent {
            MaterialTheme(colorScheme = AkashicColorScheme, typography = AkashicTypography) {
                AkashicApp(db, config)
            }
        }
    }
}

private val SECTIONS = listOf("Organisation", "Journaux", "Collections", "Archives")

@Composable
fun AkashicApp(db: AkashicDatabase, config: ConfigStore) {
    val restored = remember { config.load().lastActiveSection }
    var section by remember {
        mutableStateOf(restored?.takeIf { SECTIONS.contains(it) } ?: "Journaux")
    }

    Box(Modifier.fillMaxSize().background(Palette.AppBg)) {
        Column(Modifier.fillMaxSize()) {
            Row(
                Modifier.fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 8.dp, vertical = 6.dp),
            ) {
                SECTIONS.forEach { s ->
                    Spacer(Modifier.width(6.dp))
                    FilterChip(
                        selected = s == section,
                        onClick = { section = s; config.saveSection(s) },
                        label = { Text(s, maxLines = 1) },
                    )
                }
            }
            when (section) {
                "Organisation" -> OrganisationSection(db, config)
                "Journaux" -> JournauxSection(db, config)
                "Collections" -> CollectionsSection(db, config)
                "Archives" -> ArchivesSection(db, config)
            }
        }
    }
}

// ---- Shared building blocks ----

@Composable
private fun SectionTitle(title: String, subtitle: String? = null) {
    Column(Modifier.padding(horizontal = 12.dp, vertical = 6.dp)) {
        Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
        if (subtitle != null) {
            Text(subtitle, fontSize = 11.5.sp, color = Palette.TextMuted)
        }
    }
}

@Composable
private fun SubTabs(options: List<Pair<String, String>>, active: String, onSelect: (String) -> Unit) {
    Row(
        Modifier.fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 12.dp),
    ) {
        options.forEach { (label, tag) ->
            Spacer(Modifier.width(4.dp))
            FilterChip(
                selected = tag == active,
                onClick = { onSelect(tag) },
                label = { Text(label, maxLines = 1) },
            )
        }
    }
}

@Composable
private fun CardBlock(content: @Composable ColumnScope.() -> Unit) {
    Column(
        Modifier.fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 4.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(Palette.Surface1)
            .border(1.dp, Palette.BorderStrong, RoundedCornerShape(10.dp))
            .padding(12.dp),
        content = content,
    )
}

@Composable
private fun TierPill(tier: Int) {
    val label = listOf("S", "A", "B", "C", "D").getOrElse(tier) { "?" }
    val color = Palette.tierColors.getOrElse(tier) { Palette.Accent }
    Text(
        label,
        color = color,
        fontWeight = FontWeight.Bold,
        fontSize = 11.sp,
        modifier = Modifier
            .clip(RoundedCornerShape(6.dp))
            .background(Palette.Surface3)
            .padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

// ---- Organisation ----

@Composable
private fun OrganisationSection(db: AkashicDatabase, config: ConfigStore) {
    val restored = remember { config.load().organisationActiveTab }
    var tab by remember { mutableStateOf(restored ?: "Projets") }
    SectionTitle("Organisation", "Projets et transitions — suivi de progression graduée.")
    SubTabs(
        listOf("Projets" to "Projets", "Transitions" to "Transitions"),
        tab,
    ) { tab = it; config.saveOrganisationTab(it) }

    if (tab == "Projets") {
        val projects = remember { mutableStateOf<List<com.akashicrecords.data.PersonalProject>>(emptyList()) }
        LaunchedEffect(db) { withContext(Dispatchers.IO) { projects.value = db.personalProjectDao().getAll() } }
        var selected by remember { mutableStateOf<Int?>(null) }
        OrganisationList(projects.value.map { it.Id to "${it.Title}  ·  ${statusLabel(it.Status)}" }, selected) { selected = it }
        selected?.let { id -> OrganisationProjectDetail(db, id) }
    } else {
        val transitions = remember { mutableStateOf<List<com.akashicrecords.data.Transition>>(emptyList()) }
        LaunchedEffect(db) { withContext(Dispatchers.IO) { transitions.value = db.transitionDao().getAll() } }
        var selected by remember { mutableStateOf<Int?>(null) }
        OrganisationList(transitions.value.map { it.Id to it.Title }, selected) { selected = it }
        selected?.let { id -> OrganisationTransitionDetail(db, id) }
    }
}

private fun statusLabel(status: Int) = listOf("Actif", "Terminé", "Archivé").getOrElse(status) { "?" }

@Composable
private fun OrganisationList(rows: List<Pair<Int, String>>, selected: Int?, onSelect: (Int) -> Unit) {
    LazyColumn(Modifier.fillMaxWidth()) {
        items(rows) { (id, label) ->
            CardBlock {
                Text(
                    label,
                    fontWeight = if (id == selected) FontWeight.Bold else FontWeight.Normal,
                    modifier = Modifier.clickable { onSelect(id) },
                )
            }
        }
    }
}

@Composable
private fun OrganisationProjectDetail(db: AkashicDatabase, id: Int) {
    val project = remember { mutableStateOf<com.akashicrecords.data.PersonalProject?>(null) }
    val tasks = remember { mutableStateOf<List<com.akashicrecords.data.ProjectTask>>(emptyList()) }
    LaunchedEffect(db, id) {
        withContext(Dispatchers.IO) {
            project.value = db.personalProjectDao().getById(id)
            tasks.value = db.projectTaskDao().forProject(id)
        }
    }
    CardBlock {
        Text(project.value?.Description ?: "", color = Palette.TextMuted)
        Spacer(Modifier.height(6.dp))
        tasks.value.forEach { t -> Text("${if (t.IsDone) "☑" else "☐"}  ${t.Text}") }
    }
}

@Composable
private fun OrganisationTransitionDetail(db: AkashicDatabase, id: Int) {
    val tr = remember { mutableStateOf<com.akashicrecords.data.Transition?>(null) }
    val steps = remember { mutableStateOf<List<com.akashicrecords.data.TransitionStep>>(emptyList()) }
    LaunchedEffect(db, id) {
        withContext(Dispatchers.IO) {
            tr.value = db.transitionDao().getById(id)
            steps.value = db.transitionStepDao().forTransition(id)
        }
    }
    CardBlock {
        tr.value?.let {
            Text("Actuel : ${it.CurrentState}", color = Palette.TextMuted)
            Text("Cible : ${it.DesiredState}", color = Palette.TextMuted)
        }
        Spacer(Modifier.height(6.dp))
        steps.value.forEach { s -> Text("${if (s.IsDone) "☑" else "☐"}  ${s.Text}") }
    }
}

// ---- Journaux ----

@Composable
private fun JournauxSection(db: AkashicDatabase, config: ConfigStore) {
    val restored = remember { config.load().journauxActiveTab }
    var tab by remember { mutableStateOf(restored ?: "Personal") }
    SectionTitle("Journaux", "Journal personnel, recettes, poésie et archive artistique.")
    SubTabs(
        listOf(
            "Journal personnel" to "Personal",
            "Recettes" to "Recipes",
            "Poésie" to "Poetry",
            "Archive artistique" to "Artistic",
        ),
        tab,
    ) { tab = it; config.saveJournauxTab(it) }

    when (tab) {
        "Personal" -> PersonalJournal(db)
        "Recipes" -> RecipesJournal(db)
        "Poetry" -> PoetryJournal(db)
        "Artistic" -> ArtisticJournal(db)
    }
}

@Composable
private fun PersonalJournal(db: AkashicDatabase) {
    val entries = remember { mutableStateOf<List<com.akashicrecords.data.JournalEntry>>(emptyList()) }
    LaunchedEffect(db) { withContext(Dispatchers.IO) { entries.value = db.journalEntryDao().getAll() } }
    var selected by remember { mutableStateOf<Int?>(null) }
    LazyColumn(Modifier.fillMaxWidth()) {
        items(entries.value) { e ->
            CardBlock {
                Text(
                    "${e.EntryDate} — ${e.Title}",
                    fontWeight = if (e.Id == selected) FontWeight.Bold else FontWeight.Normal,
                    modifier = Modifier.clickable { selected = e.Id },
                )
                if (e.Id == selected) {
                    Spacer(Modifier.height(4.dp))
                    Text(e.Text, color = Palette.TextMuted)
                    if (e.Tags.isNotBlank()) {
                        Spacer(Modifier.height(4.dp))
                        Text(e.Tags, fontSize = 11.sp, color = Palette.TextFaint)
                    }
                }
            }
        }
    }
}

@Composable
private fun RecipesJournal(db: AkashicDatabase) {
    val recipes = remember { mutableStateOf<List<com.akashicrecords.data.Recipe>>(emptyList()) }
    LaunchedEffect(db) { withContext(Dispatchers.IO) { recipes.value = db.recipeDao().getAll() } }
    var selected by remember { mutableStateOf<Int?>(null) }
    LazyColumn(Modifier.fillMaxWidth()) {
        items(recipes.value) { r ->
            CardBlock {
                Text(
                    r.Title,
                    fontWeight = if (r.Id == selected) FontWeight.Bold else FontWeight.Normal,
                    modifier = Modifier.clickable { selected = r.Id },
                )
                if (r.Id == selected) {
                    Spacer(Modifier.height(4.dp))
                    if (r.Ingredients.isNotBlank()) Text("Ingrédients : ${r.Ingredients}", color = Palette.TextMuted)
                    if (r.Instructions.isNotBlank()) Text("Préparation : ${r.Instructions}", color = Palette.TextMuted)
                    if (r.Notes.isNotBlank()) Text(r.Notes, fontSize = 11.sp, color = Palette.TextFaint)
                }
            }
        }
    }
}

@Composable
private fun PoetryJournal(db: AkashicDatabase) {
    val recueils = remember { mutableStateOf<List<com.akashicrecords.data.Recueil>>(emptyList()) }
    val poems = remember { mutableStateOf<List<com.akashicrecords.data.Poem>>(emptyList()) }
    var recueilId by remember { mutableStateOf<Int?>(null) }
    LaunchedEffect(db) { withContext(Dispatchers.IO) { recueils.value = db.recueilDao().getAll() } }
    LaunchedEffect(db, recueilId) {
        recueilId?.let { id -> withContext(Dispatchers.IO) { poems.value = db.poemDao().forRecueil(id) } }
    }
    var selectedPoem by remember { mutableStateOf<Int?>(null) }
    LazyColumn(Modifier.fillMaxWidth()) {
        items(recueils.value) { rec ->
            CardBlock {
                Text(
                    rec.Title,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.clickable { recueilId = rec.Id },
                )
                if (rec.Summary.isNotBlank()) Text(rec.Summary, fontSize = 11.sp, color = Palette.TextMuted)
            }
        }
        items(poems.value) { p ->
            CardBlock {
                Text(
                    p.Title,
                    fontWeight = if (p.Id == selectedPoem) FontWeight.Bold else FontWeight.Normal,
                    modifier = Modifier.clickable { selectedPoem = p.Id },
                )
                if (p.Id == selectedPoem) {
                    Spacer(Modifier.height(4.dp))
                    Text(
                        p.Text,
                        color = Palette.TextMuted,
                        textAlign = when (p.TextAlignment.lowercase()) {
                            "center" -> TextAlign.Center
                            "right" -> TextAlign.Right
                            else -> TextAlign.Start
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun ArtisticJournal(db: AkashicDatabase) {
    val projects = remember { mutableStateOf<List<com.akashicrecords.data.ArtisticProject>>(emptyList()) }
    LaunchedEffect(db) { withContext(Dispatchers.IO) { projects.value = db.artisticProjectDao().getAll() } }
    var selected by remember { mutableStateOf<Int?>(null) }
    val elements = remember { mutableStateOf<List<com.akashicrecords.data.CanvasElement>>(emptyList()) }
    LaunchedEffect(db, selected) {
        selected?.let { id -> withContext(Dispatchers.IO) { elements.value = db.canvasElementDao().forProject(id) } }
    }
    LazyColumn(Modifier.fillMaxWidth()) {
        items(projects.value) { p ->
            CardBlock {
                Text(
                    p.Title,
                    fontWeight = if (p.Id == selected) FontWeight.Bold else FontWeight.Normal,
                    modifier = Modifier.clickable { selected = p.Id },
                )
                if (p.Id == selected) {
                    Spacer(Modifier.height(4.dp))
                    elements.value.forEach { el ->
                        val body = el.TextContent ?: el.ImagePath ?: "Référence"
                        Text("· ${canvasTypeLabel(el.Type)} : $body", fontSize = 12.sp, color = Palette.TextMuted)
                    }
                }
            }
        }
    }
}

private fun canvasTypeLabel(type: Int) = listOf("Texte", "Image", "Référence").getOrElse(type) { "?" }

// ---- Collections ----

// Ordinals match the desktop ArtworkCategory enum: Film, FilmAnimation, Anime, Livre, VideoGame, TvSeries.
private val COLLECTION_CATEGORIES = listOf(
    "Films" to 0,
    "Films d'animation" to 1,
    "Anime/manga" to 2,
    "Livres" to 3,
    "Jeux vidéo" to 4,
    "Séries" to 5,
)

@Composable
private fun CollectionsSection(db: AkashicDatabase, config: ConfigStore) {
    val restored = remember { config.load().collectionsActiveCategory }
    var cat by remember { mutableStateOf(restored?.toIntOrNull() ?: 0) }
    SectionTitle("Collections", "Œuvres classées par tier — S en tête.")
    Row(
        Modifier.fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 12.dp),
    ) {
        COLLECTION_CATEGORIES.forEach { (label, ord) ->
            Spacer(Modifier.width(4.dp))
            FilterChip(
                selected = ord == cat,
                onClick = { cat = ord; config.saveCollectionsCategory(ord.toString()) },
                label = { Text(label, maxLines = 1) },
            )
        }
    }
    val all = remember { mutableStateOf<List<com.akashicrecords.data.Artwork>>(emptyList()) }
    LaunchedEffect(db) { withContext(Dispatchers.IO) { all.value = db.artworkDao().getAll() } }
    var selected by remember { mutableStateOf<Int?>(null) }
    val tierNames = listOf("S", "A", "B", "C", "D")
    LazyColumn(Modifier.fillMaxWidth()) {
        tierNames.forEachIndexed { tierIdx, tierName ->
            val inTier = all.value.filter { it.Category == cat && it.Tier == tierIdx }
            if (inTier.isNotEmpty()) {
                item {
                    Text(
                        "Tier $tierName",
                        fontWeight = FontWeight.Bold,
                        color = Palette.Accent2,
                        modifier = Modifier.padding(start = 12.dp, top = 8.dp),
                    )
                }
                items(inTier) { w ->
                    CardBlock {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            TierPill(w.Tier)
                            Spacer(Modifier.width(8.dp))
                            Text(
                                w.Title,
                                fontWeight = if (w.Id == selected) FontWeight.Bold else FontWeight.Normal,
                                modifier = Modifier.clickable { selected = w.Id },
                            )
                        }
                        if (w.Id == selected) {
                            Spacer(Modifier.height(4.dp))
                            ArtworkDetailInner(db, w.Id)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ArtworkDetailInner(db: AkashicDatabase, artworkId: Int) {
    val axes = remember { mutableStateOf<Map<Int, String>>(emptyMap()) }
    val evals = remember { mutableStateOf<List<com.akashicrecords.data.ArtworkEvaluation>>(emptyList()) }
    val obs = remember { mutableStateOf<List<com.akashicrecords.data.Observation>>(emptyList()) }
    LaunchedEffect(db, artworkId) {
        withContext(Dispatchers.IO) {
            axes.value = db.evaluationAxisDao().getAll().associate { it.Id to it.Name }
            evals.value = db.artworkEvaluationDao().forArtwork(artworkId)
            obs.value = db.observationDao().forArtwork(artworkId)
        }
    }
    evals.value.forEach { e ->
        Text("${axes.value[e.AxisId] ?: "Axe"} : ${e.Score}", fontSize = 12.sp, color = Palette.TextMuted)
    }
    obs.value.forEach { o ->
        Spacer(Modifier.height(4.dp))
        Text("${o.Subject} — ${o.Content}", fontSize = 12.sp, color = Palette.TextMuted)
    }
}

// ---- Archives ----

@Composable
private fun ArchivesSection(db: AkashicDatabase, config: ConfigStore) {
    val restored = remember { config.load().archivesActiveTab }
    var tab by remember { mutableStateOf(restored ?: "Photos") }
    SectionTitle("Archives", "Photos et archive familiale.")
    SubTabs(
        listOf("Photos" to "Photos", "Archive familiale" to "Familiale"),
        tab,
    ) { tab = it; config.saveArchivesTab(it) }

    if (tab == "Photos") {
        val photos = remember { mutableStateOf<List<com.akashicrecords.data.Photo>>(emptyList()) }
        LaunchedEffect(db) { withContext(Dispatchers.IO) { photos.value = db.photoDao().getAll() } }
        LazyColumn(Modifier.fillMaxWidth()) {
            items(photos.value) { p ->
                CardBlock {
                    Text(p.Title.ifBlank { p.ImagePath })
                    if (p.IsFavorite) Text("★ favori", fontSize = 11.sp, color = Palette.Accent2)
                    if (p.Description.isNotBlank()) Text(p.Description, fontSize = 11.sp, color = Palette.TextMuted)
                }
            }
        }
    } else {
        val events = remember { mutableStateOf<List<com.akashicrecords.data.CalendarEvent>>(emptyList()) }
        val birthdays = remember { mutableStateOf<List<com.akashicrecords.data.Birthday>>(emptyList()) }
        LaunchedEffect(db) {
            withContext(Dispatchers.IO) {
                events.value = db.calendarEventDao().getAll()
                birthdays.value = db.birthdayDao().getAll()
            }
        }
        LazyColumn(Modifier.fillMaxWidth()) {
            items(events.value) { e ->
                CardBlock {
                    Text("${e.Date}  ${e.Title}", fontWeight = FontWeight.Bold)
                    e.StartHour?.let {
                        val m = e.StartMinute ?: 0
                        Text("$it:${m.toString().padStart(2, '0')} · ${e.Location}", fontSize = 11.sp, color = Palette.TextMuted)
                    }
                }
            }
            items(birthdays.value) { b ->
                CardBlock { Text("${b.Name} — ${b.Day}/${b.Month}", fontSize = 12.sp, color = Palette.TextMuted) }
            }
        }
    }
}
