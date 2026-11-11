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
                "Références" to "References",
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
                    "References" -> com.akashicrecords.references.ReferencesJournal(db)
                }
            }
        }
    }
}

