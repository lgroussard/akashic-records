package com.akashicrecords.widgets

import android.content.Context
import android.graphics.Bitmap
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceModifier
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.LinearProgressIndicator
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.layout.Alignment
import androidx.glance.layout.Column
import androidx.glance.layout.ContentScale
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import com.akashicrecords.Launch
import com.akashicrecords.crossword.Puzzle
import com.akashicrecords.crossword.Thumbnail
import com.akashicrecords.data.AkashicDatabase

// ---- Jeux → Mots croisés: the grid in progress, at a glance ----
//
// Its picture (squares already filled in blue), name and progress; a tap opens it where it was left. With no grid in
// progress, a tap opens the crosswords to start a new one.
data class CrosswordData(val id: Int?, val title: String, val share: Float, val thumb: Bitmap?, val finishedCount: Int)

class CrosswordWidget : AkashicWidget<CrosswordData>() {
    override suspend fun load(context: Context, shift: Int): CrosswordData {
        val games = runCatching { AkashicDatabase.get(context).crosswordGameDao().getAll() }.getOrDefault(emptyList())
        val finished = games.count { it.CompletedAt != null }
        val g = games.firstOrNull { it.CompletedAt == null } ?: return CrosswordData(null, "", 0f, null, finished)
        val thumb = runCatching { Thumbnail.render(Puzzle.fromJson(g.PuzzleJson), g.Progress, 270) }.getOrNull()
        return CrosswordData(g.Id, g.Title, Thumbnail.filled(g.Progress), thumb, finished)
    }

    @Composable
    override fun Content(context: Context, data: CrosswordData) {
        val open = W.open(context, "Jeux", "Crosswords").apply { data.id?.let { putExtra(Launch.EXTRA_ITEM, it) } }
        WidgetCard(GlanceModifier.clickable(actionStartActivity(open))) {
            Text("Mots croisés", style = TextStyle(color = W.accent, fontSize = 12.sp, fontWeight = FontWeight.Bold))
            Spacer(GlanceModifier.height(6.dp))
            if (data.id == null) {
                Text("Pas de grille en cours", style = TextStyle(color = W.text, fontSize = 15.sp))
                Text(
                    if (data.finishedCount > 0) "${data.finishedCount} grille(s) terminée(s) · touche pour en commencer une"
                    else "Touche pour commencer une grille",
                    style = TextStyle(color = W.muted, fontSize = 12.sp),
                )
                return@WidgetCard
            }
            Row(GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                data.thumb?.let {
                    Image(ImageProvider(it), contentDescription = null, contentScale = ContentScale.Fit, modifier = GlanceModifier.size(72.dp).cornerRadius(6.dp))
                    Spacer(GlanceModifier.width(12.dp))
                }
                Column(GlanceModifier.defaultWeight()) {
                    Text(data.title, style = TextStyle(color = W.text, fontSize = 15.sp, fontWeight = FontWeight.Bold), maxLines = 1)
                    Text("${(data.share * 100).toInt()} % rempli · touche pour reprendre", style = TextStyle(color = W.muted, fontSize = 12.sp), maxLines = 2)
                    Spacer(GlanceModifier.height(6.dp))
                    LinearProgressIndicator(progress = data.share, modifier = GlanceModifier.fillMaxWidth().height(4.dp), color = W.accent, backgroundColor = W.faint)
                }
            }
        }
    }
}

class CrosswordWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = CrosswordWidget()
}
