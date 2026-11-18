package com.akashicrecords.journaux

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Observation
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

// Observations on any item (Observation.OwnerKind / OwnerId). On the phone they are read-only outside the
// Collections fiche and the book highlights: they are written on the PC and arrive with an import.

// Reference images are not shared between the devices yet (RefBoard / RefImage are phone-owned, DesktopImport skips
// them), so an observation the PC attached to one of ITS images carries an id that means another image here, or
// none. Until the sync carries those tables, such an observation is shown without resolving its image, and the
// phone's image viewer shows no observations. Turn this on when both sides hold the same RefImage rows.
internal const val REF_IMAGES_SHARED = false

// The item an observation is about: what a Fil card and the canvas label show, and where a tap goes
// (kind + id are the coordinates MainActivity's openItem understands; category is the Collections one).
internal data class ObservationOwner(val kind: String, val id: Int, val title: String, val imagePath: String? = null, val category: Int? = null)

// Owner kinds in the order the Fil shows them, with their French headers (same grouping as the PC).
internal val OWNER_GROUPS = listOf(
    "Artwork" to "Œuvres",
    "JournalEntry" to "Journal",
    "Poem" to "Poèmes",
    "Recipe" to "Recettes",
    "Photo" to "Photos",
    "RefImage" to "Références",
    "MusicTrack" to "Musique",
)

// Must be called off the main thread.
internal suspend fun ownerOf(db: AkashicDatabase, o: Observation): ObservationOwner {
    val id = o.OwnerId
    fun missing(what: String) = ObservationOwner(o.OwnerKind, id, "$what introuvable")
    return when (o.OwnerKind) {
        "Artwork" -> db.artworkDao().getById(id)?.let { ObservationOwner("Artwork", id, it.Title, it.CoverImagePath, it.Category) } ?: missing("Œuvre")
        "JournalEntry" -> db.journalEntryDao().getById(id)?.let { e ->
            val day = datePart(e.EntryDate)?.let(::frenchDate)
            ObservationOwner("JournalEntry", id, listOfNotNull(e.Title.ifBlank { null }, day).joinToString(" · ").ifBlank { "Entrée de journal" }, db.journalPhotoDao().forEntry(id).firstOrNull()?.ImagePath)
        } ?: missing("Entrée de journal")
        "Poem" -> db.poemDao().getById(id)?.let { ObservationOwner("Poem", id, it.Title.ifBlank { "Sans titre" }, it.ImagePath) } ?: missing("Poème")
        "Recipe" -> db.recipeDao().getById(id)?.let { ObservationOwner("Recipe", id, it.Title, it.CoverImagePath) } ?: missing("Recette")
        "Photo" -> db.photoDao().getById(id)?.let { ObservationOwner("Photo", id, it.Title.ifBlank { "Photo" }, it.ImagePath) } ?: missing("Photo")
        "RefImage" ->
            if (!REF_IMAGES_SHARED) ObservationOwner("RefImage", id, "Image de référence (sur le PC)")
            else db.refImageDao().getById(id)?.let { ObservationOwner("RefImage", id, it.Title.ifBlank { "Image de référence" }, it.ImagePath) } ?: missing("Image de référence")
        "MusicTrack" -> db.musicTrackDao().getById(id)?.let {
            ObservationOwner("MusicTrack", id, listOf(it.Title, it.Artist).filter { s -> s.isNotBlank() }.joinToString(" — "), it.CoverImagePath)
        } ?: missing("Morceau")
        else -> ObservationOwner(o.OwnerKind, id, o.OwnerKind)
    }
}

// The observations of one item, subject + text, nothing editable. Takes no room when the item has none.
@Composable
internal fun ObservationsBlock(db: AkashicDatabase, kind: String, ownerId: Int, modifier: Modifier = Modifier) {
    var observations by remember(kind, ownerId) { mutableStateOf<List<Observation>>(emptyList()) }
    LaunchedEffect(kind, ownerId) {
        observations = withContext(Dispatchers.IO) { db.observationDao().forOwner(kind, ownerId) }
    }
    if (observations.isEmpty()) return
    Column(modifier.fillMaxWidth()) {
        Text("OBSERVATIONS", fontSize = 11.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 12.dp, bottom = 4.dp))
        observations.forEach { o ->
            Column(Modifier.fillMaxWidth().padding(vertical = 4.dp).clip(RoundedCornerShape(8.dp)).background(Palette.Surface1).padding(10.dp)) {
                if (o.Subject.isNotBlank()) Text(o.Subject, fontWeight = FontWeight.SemiBold)
                Text(o.Content, color = Palette.TextMuted, fontSize = 14.sp)
            }
        }
    }
}
