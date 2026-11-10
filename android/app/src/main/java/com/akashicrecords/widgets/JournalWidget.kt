package com.akashicrecords.widgets

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceModifier
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.layout.Spacer
import androidx.glance.layout.height
import androidx.glance.text.FontStyle
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import com.akashicrecords.calendar.Agenda
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.JournalEntry
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

data class JournalData(val today: LocalDate, val entry: JournalEntry?)

// Today's journal page: its title and first lines if written, an invitation otherwise.
class JournalWidget : AkashicWidget<JournalData>() {
    override suspend fun load(context: Context, shift: Int): JournalData {
        val today = LocalDate.now()
        val entry = runCatching {
            AkashicDatabase.get(context).journalEntryDao().getAll().firstOrNull { Agenda.day(it.EntryDate) == today }
        }.getOrNull()
        return JournalData(today, entry)
    }

    @Composable
    override fun Content(context: Context, data: JournalData) {
        val entry = data.entry
        WidgetCard(GlanceModifier.clickable(actionStartActivity(W.open(context, "Journaux", "Personal")))) {
            WidgetHeader("Journal · " + data.today.format(DateTimeFormatter.ofPattern("d MMMM", Locale.FRENCH)))
            Spacer(GlanceModifier.height(4.dp))
            if (entry == null || (entry.Title.isBlank() && entry.Text.isBlank())) {
                Text("Écrire la journée…", style = TextStyle(color = W.muted, fontSize = 14.sp, fontStyle = FontStyle.Italic))
            } else {
                if (entry.Title.isNotBlank()) Text(entry.Title, style = TextStyle(color = W.text, fontSize = 14.sp, fontWeight = FontWeight.Bold), maxLines = 1)
                if (entry.Text.isNotBlank()) Text(entry.Text.trim(), style = TextStyle(color = W.muted, fontSize = 12.sp), maxLines = 4)
            }
        }
    }
}

class JournalWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = JournalWidget()
}
