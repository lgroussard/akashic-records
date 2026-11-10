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
import com.akashicrecords.journaux.JournauxSection
import com.akashicrecords.music.MiniPlayer
import com.akashicrecords.music.MusicSection
import com.akashicrecords.music.rememberPlayerConnection
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
                // Default text colour for every Text that sets none (M3 falls back to black otherwise).
                CompositionLocalProvider(LocalContentColor provides Palette.Text) {
                    AkashicApp(db, config)
                }
            }
        }
    }
}

// "Musique" is a header button (♪ floating player) on the desktop; on the phone it is a
// section of its own, with a persistent mini-player at the bottom of every section.
private val SECTIONS = listOf("Journaux", "Musique", "Organisation", "Collections", "Archives")

@Composable
fun AkashicApp(db: AkashicDatabase, config: ConfigStore) {
    val restored = remember { config.load().lastActiveSection }
    var section by remember {
        mutableStateOf(restored?.takeIf { SECTIONS.contains(it) } ?: "Journaux")
    }
    val player = rememberPlayerConnection()
    var showSettings by remember { mutableStateOf(false) }
    // Bumped after a PC import so every section re-reads the database.
    var dataVersion by remember { mutableStateOf(0) }

    Box(Modifier.fillMaxSize().background(Palette.AppBg).systemBarsPadding().imePadding()) {
        Column(Modifier.fillMaxSize()) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Row(Modifier.weight(1f).horizontalScroll(rememberScrollState())) {
                    SECTIONS.forEach { s ->
                        Spacer(Modifier.width(6.dp))
                        FilterChip(
                            selected = s == section,
                            onClick = { section = s; config.saveSection(s) },
                            label = { Text(s, maxLines = 1) },
                        )
                    }
                }
                TextButton(onClick = { showSettings = true }) { Text("⚙", fontSize = 18.sp) }
            }
            Box(Modifier.weight(1f).fillMaxWidth()) {
                key(dataVersion) {
                    Column(Modifier.fillMaxSize()) {
                        when (section) {
                            "Organisation" -> OrganisationSection(db, config)
                            "Journaux" -> JournauxSection(db, config)
                            "Musique" -> MusicSection(db, config, player)
                            "Collections" -> CollectionsSection(db, config)
                            "Archives" -> ArchivesSection(db, config)
                        }
                    }
                }
            }
            MiniPlayer(player, db) { section = "Musique"; config.saveSection("Musique") }
        }
    }

    if (showSettings) {
        SettingsDialog(db, onDismiss = { showSettings = false }, onImported = { dataVersion++ })
    }
}

// ---- Shared building blocks ----

@Composable
internal fun SectionTitle(title: String, subtitle: String? = null) {
    Column(Modifier.padding(horizontal = 12.dp, vertical = 6.dp)) {
        Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
        if (subtitle != null) {
            Text(subtitle, fontSize = 11.5.sp, color = Palette.TextMuted)
        }
    }
}

@Composable
internal fun SubTabs(options: List<Pair<String, String>>, active: String, onSelect: (String) -> Unit) {
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
internal fun CardBlock(content: @Composable ColumnScope.() -> Unit) {
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
