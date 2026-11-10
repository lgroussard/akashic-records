package com.akashicrecords

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Create
import androidx.compose.material.icons.filled.DateRange
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Star
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import com.akashicrecords.calendar.CalendarSection
import com.akashicrecords.calendar.Reminders
import com.akashicrecords.widgets.Widgets
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import java.time.LocalDate
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
import com.akashicrecords.data.Modules
import com.akashicrecords.journaux.JournauxSection
import com.akashicrecords.music.MiniPlayer
import com.akashicrecords.music.MusicSection
import com.akashicrecords.music.rememberPlayerConnection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

// Mobile entry point, rendered over the same Room database as the desktop schema (100% local
// until the hub exists). The desktop header + section switcher becomes a bottom navigation bar:
// the four everyday sections, plus "Plus" for Organisation, Archives and the settings.
class MainActivity : ComponentActivity() {
    // Latest launch request (notification tap, launcher shortcut): section + optional extras.
    private var launch by mutableStateOf<Launch?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val db = AkashicDatabase.get(applicationContext)
        val config = ConfigStore(applicationContext)
        Reminders.schedule(applicationContext)
        launch = Launch.from(intent)
        setContent {
            MaterialTheme(colorScheme = AkashicColorScheme, typography = AkashicTypography) {
                // Default text colour for every Text that sets none (M3 falls back to black otherwise).
                CompositionLocalProvider(LocalContentColor provides Palette.Text) {
                    AkashicApp(db, config, launch) { launch = null }
                }
            }
        }
    }

    // Leaving the app: the widgets reflect what was just written (journal, agenda).
    override fun onStop() {
        super.onStop()
        lifecycleScope.launch { Widgets.refreshAll(applicationContext) }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        launch = Launch.from(intent)
    }
}

// A deep link into a section: from a reminder notification (Calendrier + day) or a launcher
// shortcut (section + sub-tab).
data class Launch(val section: String, val tab: String?, val date: LocalDate?, val item: Int? = null) {
    companion object {
        const val EXTRA_TAB = "akashic.tab"
        const val EXTRA_ITEM = "akashic.item" // a poem or recipe id, opened directly

        fun from(intent: Intent?): Launch? {
            val section = intent?.getStringExtra(Reminders.EXTRA_SECTION) ?: return null
            val date = intent.getStringExtra(Reminders.EXTRA_DATE)?.let { runCatching { LocalDate.parse(it) }.getOrNull() }
            val item = intent.getIntExtra(EXTRA_ITEM, -1).takeIf { it > 0 }
            return Launch(section, intent.getStringExtra(EXTRA_TAB), date, item)
        }
    }
}

// icon = a Material vector, or iconRes = one of the app drawables (Lecture's open book is not in the core icon set).
private data class NavItem(val section: String, val label: String, val icon: ImageVector? = null, val iconRes: Int = 0)

// "Musique" is a floating window on the desktop; on the phone it is a section of its own, with
// the mini-player above the navigation bar everywhere.
private val NAV = listOf(
    NavItem("Journaux", "Journaux", Icons.Filled.Create),
    NavItem("Musique", "Musique", Icons.Filled.PlayArrow),
    NavItem("Calendrier", "Calendrier", Icons.Filled.DateRange),
    NavItem("Lecture", "Lecture", iconRes = R.drawable.g_book),
)
// Collections moved here when Lecture took its place in the bar (phone only).
private val MORE = listOf("Organisation", "Collections", "Archives")
private val SECTIONS = NAV.map { it.section } + MORE

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AkashicApp(db: AkashicDatabase, config: ConfigStore, launch: Launch?, onLaunchHandled: () -> Unit) {
    val context = LocalContext.current
    // Modules switched off in the settings (this phone only): the bar, the "Plus" sheet and the sub-tabs follow.
    var disabled by remember { mutableStateOf(config.disabledModules()) }
    val navItems = NAV.filter { Modules.isEnabled(disabled, it.section) }
    val moreItems = MORE.filter { Modules.isEnabled(disabled, it) }
    val enabledSections = navItems.map { it.section } + moreItems
    val restored = remember { config.load().lastActiveSection }
    var section by remember {
        mutableStateOf(restored?.takeIf { it in enabledSections } ?: enabledSections.firstOrNull() ?: "Journaux")
    }
    var calendarDate by remember { mutableStateOf<LocalDate?>(null) }
    var musicTab by remember { mutableStateOf<String?>(null) }
    var journalItem by remember { mutableStateOf<Int?>(null) }
    val player = rememberPlayerConnection()
    var showSettings by remember { mutableStateOf(false) }
    var showMore by remember { mutableStateOf(false) }
    // Bumped after a PC import (and on deep links) so the section re-reads its state.
    var dataVersion by remember { mutableStateOf(0) }

    fun go(s: String) {
        section = s
        config.saveSection(s)
        // A deep-linked poem/recipe/day/tab applies once, not to the next visits of the section.
        journalItem = null
        calendarDate = null
        musicTab = null
    }

    // The open section was just switched off in the settings: move to the first one still on.
    LaunchedEffect(disabled) {
        if (section !in enabledSections) enabledSections.firstOrNull()?.let { go(it) }
    }

    LaunchedEffect(launch) {
        val l = launch ?: return@LaunchedEffect
        if (l.section in SECTIONS && Modules.isEnabled(disabled, l.section)) {
            go(l.section)
            when (l.section) {
                "Calendrier" -> calendarDate = l.date
                "Journaux" -> {
                    l.tab?.let { config.saveJournauxTab(it) }
                    journalItem = l.item
                }
                "Musique" -> musicTab = l.tab
            }
            dataVersion++
        }
        onLaunchHandled()
    }

    // Reminders need the notification permission (Android 13+); asked once, at first launch.
    val askNotifications = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {}
    LaunchedEffect(Unit) {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    Scaffold(
        containerColor = Palette.AppBg,
        contentWindowInsets = WindowInsets.systemBars,
        bottomBar = {
            Column {
                // Musique switched off: no mini-player either (its tap would open a hidden section).
                if (Modules.isEnabled(disabled, "Musique")) MiniPlayer(player, db) { go("Musique") }
                NavigationBar(containerColor = Palette.Surface1, tonalElevation = 0.dp) {
                    navItems.forEach { item ->
                        NavigationBarItem(
                            selected = section == item.section,
                            onClick = { go(item.section) },
                            icon = {
                                if (item.iconRes != 0) Icon(androidx.compose.ui.res.painterResource(item.iconRes), contentDescription = item.label)
                                else Icon(item.icon!!, contentDescription = item.label)
                            },
                            label = { Text(item.label, fontSize = 11.sp, maxLines = 1) },
                            colors = navColors(),
                        )
                    }
                    NavigationBarItem(
                        selected = section in MORE,
                        onClick = { showMore = true },
                        icon = { Icon(Icons.Filled.Menu, contentDescription = "Plus") },
                        label = { Text(if (section in MORE) section else "Plus", fontSize = 11.sp, maxLines = 1) },
                        colors = navColors(),
                    )
                }
            }
        },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding).imePadding()) {
            key(dataVersion) {
                Column(Modifier.fillMaxSize().padding(top = 4.dp)) {
                    when (section) {
                        "Organisation" -> OrganisationSection(db, config)
                        "Journaux" -> JournauxSection(db, config, openItem = journalItem)
                        "Musique" -> MusicSection(db, config, player, initialTab = musicTab)
                        "Calendrier" -> CalendarSection(db, openDate = calendarDate)
                        "Collections" -> CollectionsSection(db, config)
                        "Lecture" -> com.akashicrecords.reading.LectureSection(db, config)
                        "Archives" -> ArchivesSection(db, config)
                    }
                }
            }
        }
    }

    if (showMore) {
        ModalBottomSheet(onDismissRequest = { showMore = false }, containerColor = Palette.Surface1) {
            Column(Modifier.padding(bottom = 24.dp)) {
                if ("Organisation" in moreItems) MoreRow("Organisation", "Projets et transitions") { showMore = false; go("Organisation") }
                if ("Collections" in moreItems) MoreRow("Collections", "Films, séries, livres, jeux — par tier") { showMore = false; go("Collections") }
                if ("Archives" in moreItems) MoreRow("Archives", "Photos et archive familiale") { showMore = false; go("Archives") }
                HorizontalDivider(color = Palette.BorderStrong, modifier = Modifier.padding(vertical = 6.dp))
                MoreRow("Réglages", "Modules, widgets, import des données du PC") { showMore = false; showSettings = true }
            }
        }
    }

    if (showSettings) {
        SettingsDialog(
            db, config, disabled,
            onModulesChanged = { disabled = it; dataVersion++ },
            onDismiss = { showSettings = false },
            onImported = { dataVersion++ },
        )
    }
}

@Composable
private fun navColors() = NavigationBarItemDefaults.colors(
    selectedIconColor = Palette.Text,
    selectedTextColor = Palette.Text,
    indicatorColor = Palette.AccentSoft,
    unselectedIconColor = Palette.TextMuted,
    unselectedTextColor = Palette.TextMuted,
)

@Composable
private fun MoreRow(title: String, subtitle: String, onClick: () -> Unit) {
    Column(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 24.dp, vertical = 12.dp)) {
        Text(title, fontSize = 16.sp)
        Text(subtitle, fontSize = 12.sp, color = Palette.TextMuted)
    }
}

// ---- Shared building blocks ----

// Sub-section switcher (desktop sub-tabs), kept to one compact scrollable row.
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
    val disabled = remember { config.disabledModules() }
    var tab by remember { mutableStateOf(Modules.pickTab(disabled, "Organisation", restored, "Projets")) }
    SubTabs(
        Modules.visibleTabs(disabled, "Organisation", listOf("Projets" to "Projets", "Transitions" to "Transitions")),
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
    val disabled = remember { config.disabledModules() }
    var cat by remember {
        val saved = restored?.toIntOrNull()?.let { Modules.COLLECTION_KEYS.getOrNull(it) }
        mutableStateOf(Modules.COLLECTION_KEYS.indexOf(Modules.pickTab(disabled, "Collections", saved, "Film")).coerceAtLeast(0))
    }
    Row(
        Modifier.fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 12.dp),
    ) {
        COLLECTION_CATEGORIES.filter { (_, ord) -> Modules.isEnabled(disabled, "Collections", Modules.COLLECTION_KEYS[ord]) }.forEach { (label, ord) ->
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
    val disabled = remember { config.disabledModules() }
    var tab by remember { mutableStateOf(Modules.pickTab(disabled, "Archives", restored, "Photos")) }
    SubTabs(
        Modules.visibleTabs(disabled, "Archives", listOf("Photos" to "Photos", "Archive familiale" to "Familiale")),
        tab,
    ) { tab = it; config.saveArchivesTab(it) }

    if (tab == "Photos") {
        val photos = remember { mutableStateOf<List<com.akashicrecords.data.Photo>>(emptyList()) }
        LaunchedEffect(db) { withContext(Dispatchers.IO) { photos.value = db.photoDao().getAll() } }
        if (photos.value.isEmpty()) {
            Text("Aucune photo. Elles arrivent avec l'import des données du PC (Plus → Réglages).", color = Palette.TextMuted, modifier = Modifier.padding(16.dp))
        }
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
        // Same as the desktop: the family-blog archive (scraper) is not built yet. Calendar events
        // and birthdays live in the Calendrier section, not here.
        Text(
            "Archive familiale : à venir (l'import du blog familial n'est pas encore construit, ni sur PC ni ici).",
            color = Palette.TextMuted,
            modifier = Modifier.padding(16.dp),
        )
    }
}
