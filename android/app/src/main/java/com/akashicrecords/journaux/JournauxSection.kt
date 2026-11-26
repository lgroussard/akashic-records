package com.akashicrecords.journaux

import androidx.compose.foundation.layout.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.akashicrecords.SubTabs
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ConfigStore
import com.akashicrecords.data.Modules

// What used to be one "Journaux" section with five tabs, split in two (2026-11) so each keeps two or three:
//  - "Quotidien" (section key still "Journaux"): the everyday, personal journal and recipes (with the shopping list);
//  - "Art": poetry, the artistic archive and references.
// Each tab keeps its own paradigm, as on the desktop.
@Composable
fun JournauxSection(db: AkashicDatabase, config: ConfigStore, openItem: Int? = null, onOpenItem: (String, Int, Int?) -> Unit = { _, _, _ -> }) =
    TabbedJournals(db, config, "Journaux", listOf("Journal personnel" to "Personal", "Recettes" to "Recipes"), "Personal",
        restored = config.load().journauxActiveTab, save = config::saveJournauxTab, openItem = openItem, onOpenItem = onOpenItem)

@Composable
fun ArtSection(db: AkashicDatabase, config: ConfigStore, openTab: String? = null, openItem: Int? = null, onOpenItem: (String, Int, Int?) -> Unit = { _, _, _ -> }) =
    TabbedJournals(db, config, "Art", listOf("Poésie" to "Poetry", "Archive artistique" to "Artistic", "Références" to "References"), "Poetry",
        restored = openTab ?: config.artTab(), save = config::saveArtTab, openItem = openItem, onOpenItem = onOpenItem)

/** True for the tabs that now live in "Art" (links and widgets that still say "Journaux" are sent there). */
fun isArtTab(tab: String?) = tab in setOf("Poetry", "Artistic", "References")

@Composable
private fun TabbedJournals(
    db: AkashicDatabase, config: ConfigStore, section: String, tabs: List<Pair<String, String>>, default: String,
    restored: String?, save: (String?) -> Unit, openItem: Int?, onOpenItem: (String, Int, Int?) -> Unit,
) {
    // Sub-tabs switched off in the settings are hidden; a saved tab that is now off (or now in the other section)
    // falls back to the first one on.
    val disabled = remember { config.disabledModules() }
    val own = tabs.map { it.second }
    var tab by remember(restored) { mutableStateOf(Modules.pickTab(disabled, section, restored?.takeIf { it in own }, default)) }
    Column(Modifier.fillMaxSize()) {
        SubTabs(Modules.visibleTabs(disabled, section, tabs), tab) { tab = it; save(it) }
        Spacer(Modifier.height(4.dp))
        Box(Modifier.weight(1f)) {
            Column(Modifier.fillMaxSize()) {
                when (tab) {
                    "Personal" -> PersonalJournal(db)
                    "Recipes" -> RecipesJournal(db, openItem)
                    "Poetry" -> PoetryJournal(db, openItem)
                    "Artistic" -> ArtisticJournal(db, onOpenItem)
                    "References" -> com.akashicrecords.references.ReferencesJournal(db)
                }
            }
        }
    }
}
