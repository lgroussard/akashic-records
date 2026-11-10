package com.akashicrecords.widgets

import android.content.Context
import android.graphics.Bitmap
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceModifier
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.action.actionParametersOf
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.layout.Alignment
import androidx.glance.layout.Column
import androidx.glance.layout.ContentScale
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontFamily
import androidx.glance.text.FontStyle
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextAlign
import androidx.glance.text.TextStyle
import com.akashicrecords.Launch
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Poem
import com.akashicrecords.data.Recipe
import com.akashicrecords.data.WatchlistItem
import com.akashicrecords.journaux.look
import com.akashicrecords.media.MediaFiles

// One widget per section (on top of Agenda, Journal and Musique). Each can be switched on or off
// in Réglages → Widgets. "Du jour" widgets show the same item all day; ↻ moves to the next one.

@Composable
private fun HeaderRow(title: String, shuffleKey: String?) {
    Row(GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(title, style = TextStyle(color = W.accent, fontSize = 12.sp, fontWeight = FontWeight.Bold), modifier = GlanceModifier.defaultWeight())
        if (shuffleKey != null) {
            Text(
                "↻",
                style = TextStyle(color = W.text, fontSize = 18.sp),
                modifier = GlanceModifier.padding(horizontal = 8.dp).clickable(actionRunCallback<ShuffleAction>(actionParametersOf(WIDGET_KEY to shuffleKey))),
            )
        }
    }
}

private fun openItem(context: Context, section: String, tab: String?, itemId: Int?) =
    W.open(context, section, tab).apply { if (itemId != null) putExtra(Launch.EXTRA_ITEM, itemId) }

private fun cover(context: Context, path: String?, side: Int): Bitmap? =
    path?.let { MediaFiles.resolve(context, it) }?.let { MediaFiles.decode(it, side) }

// ---- Poésie: a poem as a page, with the phone's own formatting ----

class PoemWidget : AkashicWidget<Poem?>() {
    override suspend fun load(context: Context, shift: Int): Poem? =
        pickOfDay(runCatching { AkashicDatabase.get(context).poemDao().getAll() }.getOrDefault(emptyList()).filter { it.Text.isNotBlank() }, shift)

    @Composable
    override fun Content(context: Context, data: Poem?) {
        WidgetCard(GlanceModifier.clickable(actionStartActivity(openItem(context, "Journaux", "Poetry", data?.Id)))) {
            HeaderRow("Poème du jour", if (data != null) "poem" else null)
            if (data == null) {
                Text("Aucun poème pour l'instant.", style = TextStyle(color = W.muted, fontSize = 13.sp))
                return@WidgetCard
            }
            val look = data.look()
            val align = when (look.align) { "Center" -> TextAlign.Center; "Right" -> TextAlign.End; else -> TextAlign.Start }
            Text(
                data.Title.ifBlank { "Sans titre" },
                style = TextStyle(color = W.text, fontSize = 16.sp, fontWeight = FontWeight.Bold, fontFamily = FontFamily.Serif, textAlign = TextAlign.Center),
                modifier = GlanceModifier.fillMaxWidth().padding(vertical = 4.dp),
                maxLines = 1,
            )
            Text(
                data.Text.trim(),
                style = TextStyle(
                    color = W.text,
                    fontSize = (look.fontSize * 0.7).coerceIn(12.0, 18.0).sp,
                    fontFamily = FontFamily.Serif,
                    fontStyle = if (look.italic) FontStyle.Italic else FontStyle.Normal,
                    fontWeight = if (look.bold) FontWeight.Bold else FontWeight.Normal,
                    textAlign = align,
                ),
                modifier = GlanceModifier.fillMaxWidth(),
                maxLines = 14,
            )
        }
    }
}

class PoemWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = PoemWidget()
}

// ---- Recettes: recipe of the day ----

data class RecipeData(val recipe: Recipe?, val photo: Bitmap?)

class RecipeWidget : AkashicWidget<RecipeData>() {
    override suspend fun load(context: Context, shift: Int): RecipeData {
        val r = pickOfDay(runCatching { AkashicDatabase.get(context).recipeDao().getAll() }.getOrDefault(emptyList()), shift)
        return RecipeData(r, cover(context, r?.CoverImagePath, 360))
    }

    @Composable
    override fun Content(context: Context, data: RecipeData) {
        val r = data.recipe
        WidgetCard(GlanceModifier.clickable(actionStartActivity(openItem(context, "Journaux", "Recipes", r?.Id)))) {
            HeaderRow("Recette du jour", if (r != null) "recipe" else null)
            if (r == null) {
                Text("Aucune recette pour l'instant.", style = TextStyle(color = W.muted, fontSize = 13.sp))
                return@WidgetCard
            }
            Spacer(GlanceModifier.height(4.dp))
            Row(GlanceModifier.fillMaxWidth()) {
                if (data.photo != null) {
                    Image(ImageProvider(data.photo), contentDescription = null, contentScale = ContentScale.Crop, modifier = GlanceModifier.size(64.dp).cornerRadius(10.dp))
                    Spacer(GlanceModifier.width(10.dp))
                }
                Column(GlanceModifier.defaultWeight()) {
                    Text(r.Title, style = TextStyle(color = W.text, fontSize = 15.sp, fontWeight = FontWeight.Bold), maxLines = 2)
                    if (r.Category.isNotBlank()) Text(r.Category, style = TextStyle(color = W.muted, fontSize = 12.sp), maxLines = 1)
                    if (r.Ingredients.isNotBlank()) Text(r.Ingredients.trim(), style = TextStyle(color = W.faint, fontSize = 11.sp), maxLines = 3)
                }
            }
        }
    }
}

class RecipeWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = RecipeWidget()
}

// ---- Collections: like the desktop Film du jour, one film AND one animated film from "À voir",
// each picked in its own list (Need ×3, Want ×2, Should ×1). ↻ moves both. ----

data class WatchPick(val item: WatchlistItem, val cover: Bitmap?)
data class WatchData(val film: WatchPick?, val animation: WatchPick?)

class WatchlistWidget : AkashicWidget<WatchData>() {
    override suspend fun load(context: Context, shift: Int): WatchData {
        val items = runCatching { AkashicDatabase.get(context).watchlistItemDao().getAll() }.getOrDefault(emptyList())
        fun pick(category: Int) = pickOfDay(items.filter { it.Category == category }, shift) { it.Priority + 1 }
            ?.let { WatchPick(it, cover(context, it.CoverImagePath, 240)) }
        return WatchData(pick(0), pick(1))
    }

    @Composable
    override fun Content(context: Context, data: WatchData) {
        WidgetCard(GlanceModifier.clickable(actionStartActivity(W.open(context, "Collections")))) {
            HeaderRow("À voir ce soir", if (data.film != null || data.animation != null) "watch" else null)
            if (data.film == null && data.animation == null) {
                Text("Liste « À voir » vide.", style = TextStyle(color = W.muted, fontSize = 13.sp))
                return@WidgetCard
            }
            Spacer(GlanceModifier.height(4.dp))
            Row(GlanceModifier.fillMaxWidth()) {
                Pick(data.animation, "Animation", GlanceModifier.defaultWeight())
                Spacer(GlanceModifier.width(10.dp))
                Pick(data.film, "Film", GlanceModifier.defaultWeight())
            }
        }
    }

    @Composable
    private fun Pick(p: WatchPick?, label: String, modifier: GlanceModifier) {
        Row(modifier, verticalAlignment = Alignment.Top) {
            if (p?.cover != null) {
                Image(ImageProvider(p.cover), contentDescription = null, contentScale = ContentScale.Crop, modifier = GlanceModifier.size(width = 44.dp, height = 64.dp).cornerRadius(6.dp))
                Spacer(GlanceModifier.width(6.dp))
            }
            Column {
                Text(label.uppercase(), style = TextStyle(color = W.faint, fontSize = 10.sp, fontWeight = FontWeight.Bold))
                Text(p?.item?.Title ?: "—", style = TextStyle(color = W.text, fontSize = 13.sp, fontWeight = FontWeight.Bold), maxLines = 3)
            }
        }
    }
}

class WatchlistWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = WatchlistWidget()
}

// ---- Organisation: the transitions pinned to the desktop, then active projects (progress) ----

class OrganisationWidget : AkashicWidget<List<String>>() {
    override suspend fun load(context: Context, shift: Int): List<String> = runCatching {
        val db = AkashicDatabase.get(context)
        val reminders = db.transitionDao().getAll().filter { it.ShowOnDesktop && it.ReminderText.isNotBlank() }.map { "• " + it.ReminderText }
        val projects = db.personalProjectDao().getAll().filter { it.Status == 0 }.map { p ->
            val tasks = db.projectTaskDao().forProject(p.Id)
            if (tasks.isEmpty()) p.Title else "${p.Title}  ${tasks.count { it.IsDone }}/${tasks.size}"
        }
        reminders + projects
    }.getOrDefault(emptyList())

    @Composable
    override fun Content(context: Context, data: List<String>) {
        WidgetCard(GlanceModifier.clickable(actionStartActivity(W.open(context, "Organisation")))) {
            HeaderRow("Organisation", null)
            Spacer(GlanceModifier.height(4.dp))
            if (data.isEmpty()) Text("Aucun projet actif.", style = TextStyle(color = W.muted, fontSize = 13.sp))
            data.take(6).forEach { Text(it, style = TextStyle(color = W.text, fontSize = 13.sp), maxLines = 1) }
        }
    }
}

class OrganisationWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = OrganisationWidget()
}

// ---- Archives: a photo of the day (favourites come up more often) ----

data class PhotoData(val title: String?, val bitmap: Bitmap?, val any: Boolean)

class PhotoWidget : AkashicWidget<PhotoData>() {
    override suspend fun load(context: Context, shift: Int): PhotoData {
        val photos = runCatching { AkashicDatabase.get(context).photoDao().getAll() }.getOrDefault(emptyList())
        val p = pickOfDay(photos, shift) { if (it.IsFavorite) 3 else 1 }
        return PhotoData(p?.Title, p?.let { cover(context, it.ImagePath, 600) }, photos.isNotEmpty())
    }

    @Composable
    override fun Content(context: Context, data: PhotoData) {
        WidgetCard(GlanceModifier.clickable(actionStartActivity(W.open(context, "Archives")))) {
            HeaderRow("Souvenir du jour", if (data.any) "photo" else null)
            if (data.bitmap == null) {
                Text(if (!data.any) "Aucune photo pour l'instant." else data.title ?: "Photo absente du téléphone", style = TextStyle(color = W.muted, fontSize = 13.sp))
            } else {
                Spacer(GlanceModifier.height(4.dp))
                Image(ImageProvider(data.bitmap), contentDescription = data.title, contentScale = ContentScale.Crop, modifier = GlanceModifier.fillMaxSize().cornerRadius(12.dp))
            }
        }
    }
}

class PhotoWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = PhotoWidget()
}
