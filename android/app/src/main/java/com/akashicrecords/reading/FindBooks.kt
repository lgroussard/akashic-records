package com.akashicrecords.reading

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ConfigStore
import com.akashicrecords.music.RemoteImage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// "Trouver des livres": searches the built-in sources and the user's own OPDS catalogues at once (as you type, after
// a short pause), one section per source; "Télécharger" puts the book straight on the shelf. "Sources" switches
// sources on/off, adds or removes catalogues, browses them, and takes a direct link to an .epub.
@Composable
fun FindBooks(db: AkashicDatabase, config: ConfigStore, onOpen: (Int) -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val keyboard = LocalSoftwareKeyboardController.current
    // Kept for the life of the app (FindState), not only of this screen: opening a book and coming back keeps it.
    var query by remember { mutableStateOf(FindState.query) }
    LaunchedEffect(query) { FindState.query = query }
    var catalogs by remember { mutableStateOf(config.lectureCatalogs().map { UserCatalog(it.first, it.second) }) }
    var off by remember { mutableStateOf(config.lectureSourcesOff()) }
    // Results per source, in display order; null = still searching.
    var results by remember { mutableStateOf<List<Pair<String, List<CatalogBook>?>>>(emptyList()) }
    var downloading by remember { mutableStateOf<Set<String>>(emptySet()) }
    var added by remember { mutableStateOf<Map<String, Int>>(emptyMap()) }
    var message by remember { mutableStateOf<String?>(null) }
    var showSources by remember { mutableStateOf(false) }
    var expanded by remember(query) { mutableStateOf<Set<String>>(emptySet()) }
    var browsing by remember { mutableStateOf<Pair<UserCatalog, List<String>>?>(null) } // catalogue + page stack

    val sources = Catalog.BUILT_IN + catalogs.map { it.name }

    // What the words name (anything / a title / an author) and the language wanted; kept while the app runs.
    var mode by remember { mutableStateOf(FindState.mode) }
    var language by remember { mutableStateOf(FindState.language) }
    LaunchedEffect(mode, language) { FindState.mode = mode; FindState.language = language }

    LaunchedEffect(query, off, catalogs, mode, language) {
        val text = query.trim()
        if (text.length < 3) { results = emptyList(); return@LaunchedEffect }
        delay(600)
        val q = BookQuery(text, mode, language)
        // Wikisource is French only: hidden when another language is asked.
        val active = sources.filter { it !in off && !(it == Catalog.WIKISOURCE && language != null && language != "fr") }
        results = active.map { it to null }
        // Each source fills its section as soon as it answers (they are not equally fast).
        active.map { s ->
            async(Dispatchers.IO) {
                val found = runCatching {
                    when (s) {
                        Catalog.GUTENBERG -> Catalog.gutenberg(q)
                        Catalog.WIKISOURCE -> Catalog.wikisource(q)
                        Catalog.ARCHIVE -> Catalog.archive(q)
                        else -> catalogs.firstOrNull { it.name == s }?.let { Catalog.searchUserCatalog(it, text) }.orEmpty()
                    }.let { Catalog.rank(q, it) }
                }.getOrDefault(emptyList())
                withContext(Dispatchers.Main) { results = results.map { if (it.first == s) s to found else it } }
            }
        }.forEach { it.await() }
    }

    fun keyOf(b: CatalogBook) = b.source + "|" + (b.downloadUrl ?: b.archiveId) + "|" + b.title

    fun download(b: CatalogBook) {
        val key = keyOf(b)
        if (key in downloading) return
        downloading = downloading + key
        message = null
        scope.launch {
            try {
                val book = ReadingStore.download(context, db, ReadingStore.Hints(b.title, b.author, b.coverUrl)) { file -> Catalog.download(b, file) }
                added = added + (key to book.Id)
                message = "« ${book.Title} » ajouté à ta bibliothèque."
            } catch (e: Exception) {
                android.util.Log.w("AkashicCatalog", "download failed: ${b.title}", e)
                message = "« ${b.title} » : ${e.message ?: "téléchargement impossible."}"
            } finally {
                downloading = downloading - key
            }
        }
    }

    @Composable
    fun BookRow(b: CatalogBook) {
        val key = keyOf(b)
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            RemoteImage(b.coverUrl, Modifier.size(width = 40.dp, height = 58.dp).clip(RoundedCornerShape(3.dp)))
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text(b.title, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(
                    listOfNotNull(b.author.takeIf { it.isNotBlank() }, b.language?.uppercase(), b.source).joinToString(" · "),
                    fontSize = 11.sp, color = Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis,
                )
            }
            when {
                added[key] != null -> TextButton(onClick = { onOpen(added.getValue(key)) }) { Text("Lire") }
                key in downloading -> Text("…", modifier = Modifier.padding(12.dp), color = Palette.TextMuted)
                else -> TextButton(onClick = { download(b) }) { Text("⤓", fontSize = 18.sp) }
            }
        }
    }

    // --- Browsing one of the user's catalogues, section by section ---
    val browse = browsing
    if (browse != null) {
        val (catalog, stack) = browse
        var page by remember(stack) { mutableStateOf<OpdsPage?>(null) }
        var failed by remember(stack) { mutableStateOf(false) }
        LaunchedEffect(stack) {
            page = withContext(Dispatchers.IO) { Catalog.browse(catalog, stack.last()) }
            failed = page == null
        }
        BackHandler { browsing = if (stack.size > 1) catalog to stack.dropLast(1) else null }
        Column(Modifier.fillMaxSize()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = { browsing = if (stack.size > 1) catalog to stack.dropLast(1) else null }) { Text("←") }
                Text(page?.title?.ifBlank { null } ?: catalog.name, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            message?.let { Text(it, fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.padding(horizontal = 12.dp)) }
            val p = page
            when {
                failed -> Text("Ce catalogue ne répond pas (adresse, réseau ou connexion requise).", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
                p == null -> Text("Chargement…", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
                else -> LazyColumn(Modifier.fillMaxSize()) {
                    items(p.sections) { (title, url) ->
                        Text(
                            "$title  ›",
                            modifier = Modifier.fillMaxWidth().clickable { browsing = catalog to (stack + url) }.padding(horizontal = 16.dp, vertical = 12.dp),
                        )
                    }
                    items(p.books, key = { keyOf(it) }) { BookRow(it) }
                    p.next?.let { next -> item { TextButton(onClick = { browsing = catalog to (stack + next) }, modifier = Modifier.padding(8.dp)) { Text("Page suivante ›") } } }
                    if (p.sections.isEmpty() && p.books.isEmpty()) item { Text("Page vide.", color = Palette.TextMuted, modifier = Modifier.padding(12.dp)) }
                }
            }
        }
        return
    }

    // --- Search ---
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                query, { query = it },
                placeholder = { Text(when (mode) { SearchMode.AUTHOR -> "Nom de l'auteur"; SearchMode.TITLE -> "Titre du livre"; SearchMode.ALL -> "Titre, auteur…" }) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { keyboard?.hide() }),
                modifier = Modifier.weight(1f),
            )
            TextButton(onClick = { showSources = true }) { Text("Sources") }
        }
        // What to search for, and in which language.
        Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            listOf(SearchMode.ALL to "Tout", SearchMode.TITLE to "Titre", SearchMode.AUTHOR to "Auteur").forEach { (m, label) ->
                FilterChip(selected = mode == m, onClick = { mode = m }, label = { Text(label) }, modifier = Modifier.padding(end = 6.dp))
            }
            Text("·", color = Palette.TextFaint, modifier = Modifier.padding(horizontal = 4.dp))
            listOf(null to "Toutes langues", "fr" to "Français", "en" to "English").forEach { (l, label) ->
                FilterChip(selected = language == l, onClick = { language = l }, label = { Text(label) }, modifier = Modifier.padding(end = 6.dp))
            }
        }
        Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp)) {
            sources.forEach { s ->
                FilterChip(
                    selected = s !in off,
                    onClick = { off = if (s in off) off - s else off + s; config.saveLectureSourcesOff(off) },
                    label = { Text(s, maxLines = 1) },
                    modifier = Modifier.padding(end = 6.dp),
                )
            }
        }
        message?.let { Text(it, fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.padding(horizontal = 12.dp, vertical = 2.dp)) }
        if (query.trim().length < 3) {
            Text(
                "Cherche un titre ou un auteur dans les bibliothèques libres (Project Gutenberg, Wikisource, Internet Archive) " +
                    "et dans tes propres catalogues (« Sources »). ⤓ ajoute le livre à ta bibliothèque.",
                color = Palette.TextMuted, fontSize = 13.sp, modifier = Modifier.padding(12.dp),
            )
        }
        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            results.forEach { (source, books) ->
                item(key = "h|$source") {
                    Text(
                        source.uppercase() + when { books == null -> "  · recherche…"; books.isEmpty() -> "  · rien"; else -> "" },
                        fontSize = 11.sp, fontWeight = FontWeight.Bold, color = Palette.TextFaint,
                        modifier = Modifier.padding(start = 12.dp, top = 12.dp, bottom = 2.dp),
                    )
                }
                // 6 per source at first, so every source shows up without a long scroll; "tout voir" opens the rest.
                val list = books.orEmpty()
                val shown = if (source in expanded) list else list.take(6)
                items(shown, key = { "b|" + keyOf(it) }) { BookRow(it) }
                if (list.size > shown.size) item(key = "more|$source") {
                    TextButton(onClick = { expanded = expanded + source }, modifier = Modifier.padding(start = 4.dp)) { Text("Tout voir (${list.size})") }
                }
            }
            // Every source answered and none has the book: say why instead of leaving an empty page.
            if (results.isNotEmpty() && results.all { it.second?.isEmpty() == true }) item(key = "why") {
                Text(
                    "Rien trouvé dans ces sources. Gutenberg et Wikisource n'ont que le domaine public (en France, les " +
                        "auteurs morts depuis plus de 70 ans) ; Internet Archive a aussi des dépôts plus récents, mais pas " +
                        "tout. Un livre acheté en epub (souvent sans DRM) s'importe avec « + » dans Ma bibliothèque.",
                    color = Palette.TextMuted, fontSize = 13.sp, modifier = Modifier.padding(12.dp),
                )
            }
            item { Spacer(Modifier.height(24.dp)) }
        }
    }

    if (showSources) {
        SourcesDialog(
            catalogs = catalogs,
            onCatalogs = { catalogs = it; config.saveLectureCatalogs(it.map { c -> c.name to c.url }) },
            onBrowse = { c -> showSources = false; browsing = c to listOf(c.url) },
            onDirectLink = { url ->
                showSources = false
                download(CatalogBook("Lien direct", url.substringAfterLast('/').substringBefore('?').ifBlank { url }, "", null, url))
            },
            onDismiss = { showSources = false },
        )
    }
}

// The user's catalogues (OPDS addresses, like the download links of the music module) and a direct .epub link.
@Composable
private fun SourcesDialog(
    catalogs: List<UserCatalog>,
    onCatalogs: (List<UserCatalog>) -> Unit,
    onBrowse: (UserCatalog) -> Unit,
    onDirectLink: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    var name by remember { mutableStateOf("") }
    var url by remember { mutableStateOf("") }
    var link by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Sources") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text(
                    "Intégrées : Project Gutenberg, Wikisource (français) et Internet Archive — des livres libres de droits.",
                    fontSize = 12.sp, color = Palette.TextMuted,
                )
                Text("Mes catalogues (OPDS)", fontSize = 15.sp, modifier = Modifier.padding(top = 12.dp))
                Text(
                    "L'adresse d'un catalogue OPDS : serveur Calibre, bibliothèque, éditeur, compte d'abonnement… " +
                        "La recherche les inclut s'ils en proposent une ; « Parcourir » les ouvre rubrique par rubrique.",
                    fontSize = 11.sp, color = Palette.TextFaint,
                )
                catalogs.forEach { c ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(c.name)
                            Text(c.url, fontSize = 10.sp, color = Palette.TextFaint, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        TextButton(onClick = { onBrowse(c) }) { Text("Parcourir") }
                        TextButton(onClick = { onCatalogs(catalogs - c) }) { Text("✕", color = Palette.TextMuted) }
                    }
                }
                OutlinedTextField(name, { name = it }, label = { Text("Nom") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(url, { url = it }, label = { Text("Adresse du catalogue") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                TextButton(
                    onClick = {
                        val u = url.trim()
                        val n = name.trim().ifBlank { runCatching { java.net.URL(u).host }.getOrDefault(u) }
                        if (u.startsWith("http") && catalogs.none { it.name == n }) onCatalogs(catalogs + UserCatalog(n, u))
                        name = ""; url = ""
                    },
                    enabled = url.trim().startsWith("http"),
                ) { Text("Ajouter le catalogue") }

                Text("Lien direct", fontSize = 15.sp, modifier = Modifier.padding(top = 12.dp))
                Text("L'adresse d'un fichier .epub : il est téléchargé dans ta bibliothèque.", fontSize = 11.sp, color = Palette.TextFaint)
                OutlinedTextField(link, { link = it }, label = { Text("https://…/livre.epub") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                TextButton(onClick = { onDirectLink(link.trim()) }, enabled = link.trim().startsWith("http")) { Text("Télécharger") }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Fermer") } },
    )
}

// The last search of "Trouver des livres", kept while the app runs (the screen is rebuilt when a book is opened).
internal object FindState {
    var query = ""
    var mode = SearchMode.ALL
    var language: String? = "fr"
}
