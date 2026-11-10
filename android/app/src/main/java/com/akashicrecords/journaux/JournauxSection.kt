package com.akashicrecords.journaux

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.CardBlock
import com.akashicrecords.Palette
import com.akashicrecords.SubTabs
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ArtisticProject
import com.akashicrecords.data.CanvasElement
import com.akashicrecords.data.ConfigStore
import com.akashicrecords.data.Modules
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

// Journaux: four tabs, each with its own paradigm (as on the desktop). Personal journal,
// recipes and poetry are fully editable; the artistic canvas stays read-only on the phone.
@Composable
fun JournauxSection(db: AkashicDatabase, config: ConfigStore, openItem: Int? = null) {
    val restored = remember { config.load().journauxActiveTab }
    // Sub-tabs switched off in the settings are hidden; a saved tab that is now off falls back to the first one on.
    val disabled = remember { config.disabledModules() }
    var tab by remember { mutableStateOf(Modules.pickTab(disabled, "Journaux", restored, "Personal")) }
    Column(Modifier.fillMaxSize()) {
        SubTabs(
            Modules.visibleTabs(disabled, "Journaux", listOf(
                "Journal personnel" to "Personal",
                "Poésie" to "Poetry",
                "Recettes" to "Recipes",
                "Archive artistique" to "Artistic",
            )),
            tab,
        ) { tab = it; config.saveJournauxTab(it) }
        Spacer(Modifier.height(4.dp))
        Box(Modifier.weight(1f)) {
            Column(Modifier.fillMaxSize()) {
                when (tab) {
                    "Personal" -> PersonalJournal(db)
                    "Recipes" -> RecipesJournal(db, openItem)
                    "Poetry" -> PoetryJournal(db, openItem)
                    "Artistic" -> ArtisticJournal(db)
                }
            }
        }
    }
}

// Read-only list of canvas projects and their elements (the infinite canvas is desktop-only).
@Composable
private fun ArtisticJournal(db: AkashicDatabase) {
    var projects by remember { mutableStateOf<List<ArtisticProject>>(emptyList()) }
    LaunchedEffect(db) { projects = withContext(Dispatchers.IO) { db.artisticProjectDao().getAll() } }
    var selected by remember { mutableStateOf<Int?>(null) }
    var elements by remember { mutableStateOf<List<CanvasElement>>(emptyList()) }
    LaunchedEffect(db, selected) {
        selected?.let { id -> elements = withContext(Dispatchers.IO) { db.canvasElementDao().forProject(id) } }
    }
    if (projects.isEmpty()) {
        Text("Aucun projet artistique (ils se créent sur le PC).", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
        return
    }
    LazyColumn(Modifier.fillMaxWidth()) {
        items(projects) { p ->
            CardBlock {
                Text(
                    p.Title,
                    fontWeight = if (p.Id == selected) FontWeight.Bold else FontWeight.Normal,
                    modifier = Modifier.clickable { selected = if (selected == p.Id) null else p.Id },
                )
                if (p.Id == selected) {
                    Spacer(Modifier.height(4.dp))
                    elements.forEach { el ->
                        val body = el.TextContent ?: el.ImagePath ?: "Référence"
                        Text("· ${canvasTypeLabel(el.Type)} : $body", fontSize = 12.sp, color = Palette.TextMuted)
                    }
                }
            }
        }
    }
}

private fun canvasTypeLabel(type: Int) = listOf("Texte", "Image", "Référence").getOrElse(type) { "?" }
