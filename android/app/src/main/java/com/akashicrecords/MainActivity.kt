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
import com.akashicrecords.data.CalendarEventLink
import com.akashicrecords.data.ConfigStore
import com.akashicrecords.data.Modules
import com.akashicrecords.references.SharedRef
import com.akashicrecords.references.ShareToReferencesSheet
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
    // An image or link shared from another app (Share → Akashic), shown in a sheet over the app.
    private var share by mutableStateOf<SharedRef?>(null)
    // Tickets shared to « Billet Akashic », shown in the attach sheet.
    private var ticketShare by mutableStateOf<List<com.akashicrecords.tickets.TicketStore.Incoming>?>(null)
    // Opened just for a share: close afterwards, back to the app that shared.
    private var openedForShare = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val db = AkashicDatabase.get(applicationContext)
        val config = ConfigStore(applicationContext)
        Reminders.schedule(applicationContext)
        // Sync with the hub, when this phone is paired (settings): steady background pace from here on.
        com.akashicrecords.sync.SyncService.init(applicationContext)
        if (com.akashicrecords.sync.SyncService.state.value.paired) com.akashicrecords.sync.SyncService.schedule(applicationContext)
        launch = Launch.from(intent)
        ticketShare = sharedTickets(intent)
        share = if (!isTicketShare(intent)) SharedRef.from(intent) else null
        openedForShare = (share != null || ticketShare != null) && savedInstanceState == null
        setContent {
            MaterialTheme(colorScheme = AkashicColorScheme, typography = AkashicTypography) {
                // Default text colour for every Text that sets none (M3 falls back to black otherwise).
                CompositionLocalProvider(LocalContentColor provides Palette.Text) {
                    AkashicApp(db, config, launch) { launch = null }
                    share?.let { s ->
                        ShareToReferencesSheet(db, s) {
                            share = null
                            if (openedForShare) finish()
                        }
                    }
                    ticketShare?.let { items ->
                        com.akashicrecords.tickets.AttachTicketsSheet(db, items) {
                            ticketShare = null
                            if (openedForShare) finish()
                        }
                    }
                }
            }
        }
    }

    // Coming to the front: fetch what the other devices wrote meanwhile, at once (not through Android's job
    // queue, which may wait).
    override fun onStart() {
        super.onStart()
        com.akashicrecords.sync.SyncService.runNow(applicationContext)
    }

    // Leaving the app: the widgets reflect what was just written (journal, agenda), and it goes to the hub
    // (a few seconds later, once the last auto-save has landed).
    override fun onStop() {
        super.onStop()
        lifecycleScope.launch { Widgets.refreshAll(applicationContext) }
        // What is typed and not auto-saved yet is written now, before Android may kill the process; then the
        // sync is queued (WorkManager: it runs even once the app is closed).
        val app = applicationContext
        com.akashicrecords.journaux.AppScope.launch {
            runCatching { kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Main) { com.akashicrecords.journaux.PendingEdits.flushAll() } }
            // Sent right now, while the app is still awake: handed to Android's job queue, it could wait for
            // minutes once the screen is off (sleep, Samsung's battery saver). The queue stays as a backup.
            com.akashicrecords.sync.SyncService.runNow(app)
            com.akashicrecords.sync.SyncService.requestSoon(app, delaySeconds = 30)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        launch = Launch.from(intent)
        if (isTicketShare(intent)) sharedTickets(intent)?.let { ticketShare = it }
        else SharedRef.from(intent)?.let { share = it }
    }

    private fun isTicketShare(intent: Intent?) = intent?.component?.className?.endsWith(".ShareTicket") == true

    // Files shared to « Billet Akashic » (the .ShareTicket alias): one or several PDFs / pictures. Nothing usable: says
    // so (never falls back to the Références sheet).
    private fun sharedTickets(intent: Intent?): List<com.akashicrecords.tickets.TicketStore.Incoming>? {
        if (intent == null || !isTicketShare(intent)) return null
        @Suppress("DEPRECATION")
        val uris = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra<android.net.Uri>(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<android.net.Uri>(Intent.EXTRA_STREAM).orEmpty()
            else -> emptyList()
        }
        val items = uris.mapNotNull { com.akashicrecords.tickets.TicketStore.describe(applicationContext, it, intent.type) }
        if (items.isEmpty()) android.widget.Toast.makeText(this, "Ce partage ne contient ni PDF ni image.", android.widget.Toast.LENGTH_LONG).show()
        return items.takeIf { it.isNotEmpty() }
    }
}

// A deep link into a section: from a reminder notification (Calendrier + day) or a launcher
// shortcut (section + sub-tab).
data class Launch(val section: String, val tab: String?, val date: LocalDate?, val item: Int? = null) {
    companion object {
        const val EXTRA_TAB = "akashic.tab"
        const val EXTRA_ITEM = "akashic.item" // a poem or recipe id, opened directly
        const val PLAYER = "Player" // section value opening the full player (music notification)
        const val TICKETS = "Billets" // section value opening the tickets on today's (shortcut)

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
private val MORE = listOf("Organisation", "Collections", "Archives", "Jeux")
private val SECTIONS = NAV.map { it.section } + MORE

@OptIn(ExperimentalMaterial3Api::class, androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
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
    var gameTab by remember { mutableStateOf<String?>(null) } // a game opened from its home-screen icon or widget
    var gameItem by remember { mutableStateOf<Int?>(null) } // …and the grid to open in it (widget)
    var journalItem by remember { mutableStateOf<Int?>(null) }
    val player = rememberPlayerConnection()
    var showSettings by remember { mutableStateOf(false) }
    var showMore by remember { mutableStateOf(false) }
    // The full player (from the mini-player, or a link to the old Queue / Similar tabs); null = closed.
    var fullPlayer by remember { mutableStateOf<Boolean?>(null) }
    var miniCollapsed by remember { mutableStateOf(config.miniPlayerCollapsed()) }
    // The Billets list over the app: null = closed, true = opened by the shortcut (straight to today's tickets).
    var ticketsHub by remember { mutableStateOf<Boolean?>(null) }
    var ticketFocus by remember { mutableStateOf<Int?>(null) } // a ticket to open in it (the next-ticket widget)
    // Bumped after a PC import (and on deep links) so the section re-reads its state.
    var dataVersion by remember { mutableStateOf(0) }

    fun go(s: String) {
        section = s
        config.saveSection(s)
        // A deep-linked poem/recipe/day/tab applies once, not to the next visits of the section.
        journalItem = null
        calendarDate = null
        musicTab = null
        gameTab = null
        gameItem = null
    }

    // The open section was just switched off in the settings: move to the first one still on.
    LaunchedEffect(disabled) {
        if (section !in enabledSections) enabledSections.firstOrNull()?.let { go(it) }
    }

    fun handle(l: Launch) {
        // The « Billets » shortcut: today's tickets (or the next ones) at once, over whatever screen was open.
        if (l.section == Launch.TICKETS) {
            ticketFocus = l.item
            ticketsHub = true
            return
        }
        // The music notification: the full player on « À suivre », over whatever screen was open.
        if (l.section == Launch.PLAYER) {
            if (Modules.isEnabled(disabled, "Musique")) fullPlayer = false
            return
        }
        if (l.section in SECTIONS && Modules.isEnabled(disabled, l.section)) {
            go(l.section)
            when (l.section) {
                "Calendrier" -> calendarDate = l.date
                "Journaux" -> {
                    l.tab?.let { config.saveJournauxTab(it) }
                    journalItem = l.item
                }
                "Musique" -> musicTab = l.tab
                "Jeux" -> { gameTab = l.tab; gameItem = l.item }
            }
            dataVersion++
        }
    }
    LaunchedEffect(launch) {
        val l = launch ?: return@LaunchedEffect
        handle(l)
        onLaunchHandled()
    }
    // A link of a calendar event or birthday (made on the PC) → the item in its module. Poems and recipes open
    // on the item itself; the other kinds open their module on the right tab.
    // The same coordinates serve the Fil of an artistic project (kind + id of the item an observation is about).
    fun openItem(kind: String, id: Int, category: Int?) {
        val target = when (kind) {
            "Poem" -> Launch("Journaux", "Poetry", null, id)
            "Recipe" -> Launch("Journaux", "Recipes", null, id)
            "JournalEntry" -> Launch("Journaux", "Personal", null, null)
            "ArtisticProject" -> Launch("Journaux", "Artistic", null, null)
            "RefImage" -> Launch("Journaux", "References", null, null)
            "Artwork", "Observation" -> {
                category?.let { config.saveCollectionsCategory(it.toString()) }
                Launch("Collections", null, null, null)
            }
            "Photo" -> Launch("Archives", null, null, null)
            "PersonalProject" -> { config.saveOrganisationTab("Projets"); Launch("Organisation", null, null, null) }
            "Transition" -> { config.saveOrganisationTab("Transitions"); Launch("Organisation", null, null, null) }
            "MusicTrack" -> Launch("Musique", null, null, null)
            else -> null
        }
        target?.let { handle(it) }
    }
    fun openCalendarLink(link: CalendarEventLink) = openItem(link.Kind, link.PrimaryId, link.Category)

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
                // Folded (⌄), it is a slim strip docked here: nothing floats over the page being read.
                if (Modules.isEnabled(disabled, "Musique")) {
                    if (miniCollapsed) {
                        com.akashicrecords.music.MiniStrip(player) { miniCollapsed = false; config.saveMiniPlayerCollapsed(false) }
                    } else {
                        MiniPlayer(player, db, onCollapse = { miniCollapsed = true; config.saveMiniPlayerCollapsed(true) }) { fullPlayer = false }
                    }
                }
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
        // Something arrived from another device: these sections are drawn again from the database (the journal
        // and the music reload themselves; reading and games keep their place). Not while the keyboard is open:
        // what is being typed is never thrown away, the reload waits until it closes.
        val synced by com.akashicrecords.sync.SyncService.dataVersion.collectAsState()
        // The keyboard's real height on screen (isImeVisible stays true on some phones once it has been opened).
        val typing = WindowInsets.ime.getBottom(androidx.compose.ui.platform.LocalDensity.current) > 0
        var shownSync by remember { mutableStateOf(synced) }
        LaunchedEffect(synced, typing) { if (!typing) shownSync = synced }
        val syncKey = if (section in setOf("Organisation", "Calendrier", "Collections", "Archives")) shownSync else 0
        Box(Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding).imePadding()) {
            key(dataVersion, syncKey) {
                Column(Modifier.fillMaxSize().padding(top = 4.dp)) {
                    when (section) {
                        "Organisation" -> OrganisationSection(db, config)
                        "Journaux" -> JournauxSection(db, config, openItem = journalItem, onOpenItem = { kind, id, category -> openItem(kind, id, category) })
                        "Musique" -> MusicSection(db, config, player, initialTab = musicTab, onOpenPlayer = { similar -> fullPlayer = similar })
                        "Calendrier" -> CalendarSection(db, openDate = calendarDate, onOpenLink = { openCalendarLink(it) }, onOpenTickets = { ticketsHub = false })
                        "Collections" -> CollectionsSection(db, config)
                        "Lecture" -> com.akashicrecords.reading.LectureSection(db, config)
                        "Archives" -> ArchivesSection(db, config)
                        "Jeux" -> com.akashicrecords.games.GamesSection(db, disabled, openGame = gameTab, openItem = gameItem)
                    }
                }
            }
        }
    }

    ticketsHub?.let { current ->
        com.akashicrecords.tickets.TicketsHub(
            db, onClose = { ticketsHub = null; ticketFocus = null }, openCurrent = current, openId = ticketFocus.takeIf { current },
        )
    }

    fullPlayer?.let { similar ->
        com.akashicrecords.music.FullPlayer(db, player, startOnSimilar = similar) { fullPlayer = null }
    }

    if (showMore) {
        ModalBottomSheet(onDismissRequest = { showMore = false }, containerColor = Palette.Surface1) {
            Column(Modifier.padding(bottom = 24.dp)) {
                if ("Organisation" in moreItems) MoreRow("Organisation", "Projets et transitions") { showMore = false; go("Organisation") }
                if ("Collections" in moreItems) MoreRow("Collections", "Films, séries, livres, jeux — par tier") { showMore = false; go("Collections") }
                if ("Archives" in moreItems) MoreRow("Archives", "Photos et archive familiale") { showMore = false; go("Archives") }
                if ("Jeux" in moreItems) MoreRow("Jeux", "Mots croisés") { showMore = false; go("Jeux") }
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

    // Each its own list of cards, opening a page to tick, add and edit (organisation/OrganisationScreen.kt).
    Box(Modifier.fillMaxSize()) {
        if (tab == "Projets") com.akashicrecords.organisation.ProjectsTab(db) else com.akashicrecords.organisation.TransitionsTab(db)
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
    // Covers grouped by tier, a fiche per work, + to add one (collections/CollectionsScreen.kt).
    com.akashicrecords.collections.CollectionGrid(db, cat)
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
        // Gallery, full-screen viewer, favourites, adding from the phone (archives/PhotosScreen.kt).
        Box(Modifier.fillMaxSize()) { com.akashicrecords.archives.PhotosTab(db) }
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
