package com.akashicrecords.widgets

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceModifier
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import com.akashicrecords.calendar.Agenda
import com.akashicrecords.calendar.AgendaItem
import com.akashicrecords.data.AkashicDatabase
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Locale

private val HEADER = DateTimeFormatter.ofPattern("EEEE d MMMM", Locale.FRENCH)
private val SHORT = DateTimeFormatter.ofPattern("EEE d", Locale.FRENCH)

data class AgendaData(val today: LocalDate, val todays: List<AgendaItem>, val next: List<AgendaItem>)

// "Aujourd'hui" + the next two weeks, from the same agenda as the app.
class AgendaWidget : AkashicWidget<AgendaData>() {
    override suspend fun load(context: Context, shift: Int): AgendaData {
        val today = LocalDate.now()
        val all = runCatching { Agenda.range(AkashicDatabase.get(context), today, today.plusDays(14)) }.getOrDefault(emptyList())
            .filter { !it.isSpanContinuation || it.date == today }
        return AgendaData(today, all.filter { it.date == today }, all.filter { it.date > today }.take(4))
    }

    @Composable
    override fun Content(context: Context, data: AgendaData) {
        WidgetCard(GlanceModifier.clickable(actionStartActivity(W.open(context, "Calendrier")))) {
            WidgetHeader(data.today.format(HEADER).replaceFirstChar { it.uppercase() })
            Spacer(GlanceModifier.height(4.dp))
            if (data.todays.isEmpty()) {
                Text("Rien aujourd'hui", style = TextStyle(color = W.muted, fontSize = 13.sp))
            } else {
                data.todays.take(3).forEach { Line(it, null) }
                if (data.todays.size > 3) Text("+${data.todays.size - 3}", style = TextStyle(color = W.faint, fontSize = 11.sp))
            }
            if (data.next.isNotEmpty()) {
                Spacer(GlanceModifier.height(6.dp))
                Text("À VENIR", style = TextStyle(color = W.faint, fontSize = 10.sp, fontWeight = FontWeight.Bold))
                data.next.forEach { Line(it, it.date.format(SHORT)) }
            }
        }
    }

    @Composable
    private fun Line(item: AgendaItem, day: String?) {
        Row(GlanceModifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(GlanceModifier.size(8.dp).cornerRadius(4.dp).background(ColorProvider(item.color))) {}
            Spacer(GlanceModifier.width(8.dp))
            val prefix = listOfNotNull(day, item.timeText.takeIf { it.isNotEmpty() }).joinToString(" ")
            Text((if (prefix.isNotEmpty()) "$prefix  " else "") + item.title, style = TextStyle(color = W.text, fontSize = 13.sp), maxLines = 1)
        }
    }
}

class AgendaWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = AgendaWidget()
}
