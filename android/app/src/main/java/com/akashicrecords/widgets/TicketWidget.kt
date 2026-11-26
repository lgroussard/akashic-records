package com.akashicrecords.widgets

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceModifier
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.height
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import com.akashicrecords.Launch
import com.akashicrecords.calendar.TicketColor
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Ticket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneId

// The next ticket, only when it matters: from an hour before its time until half an hour after (the inspector comes
// after the departure, as in TicketStore.currentIndex). The rest of the time the widget stays a faint, empty frame.
// Tickets without a time never show. It changes on its own: an alarm is armed for the next moment it has to (a ticket
// coming into or out of its window), re-armed on every render; edits, syncs and the 15-min reminder work refresh it too.
object NextTicket {
    const val BEFORE_MINUTES = 60L
    const val AFTER_MINUTES = 30L

    fun startOf(t: Ticket): LocalDateTime? {
        val h = t.Hour ?: return null
        val day = runCatching { LocalDate.parse(t.Date) }.getOrNull() ?: return null
        return day.atTime(h, t.Minute ?: 0)
    }

    // The ticket to show now (null = none), and when that changes next (null = never, as things stand).
    suspend fun state(db: AkashicDatabase, now: LocalDateTime = LocalDateTime.now()): Pair<Pair<Ticket, LocalDateTime>?, LocalDateTime?> {
        val timed = db.ticketDao().from(now.minusMinutes(AFTER_MINUTES).toLocalDate().toString())
            .mapNotNull { t -> startOf(t)?.let { t to it } }
            .sortedBy { it.second }
        val shown = timed.firstOrNull { (_, at) -> !now.isBefore(at.minusMinutes(BEFORE_MINUTES)) && now.isBefore(at.plusMinutes(AFTER_MINUTES)) }
        val next = timed.flatMap { (_, at) -> listOf(at.minusMinutes(BEFORE_MINUTES), at.plusMinutes(AFTER_MINUTES)) }
            .filter { it.isAfter(now) }.minOrNull()
        return shown to next
    }

    // Exact when Android lets the app (USE_EXACT_ALARM, declared for this), else as close as it allows.
    fun arm(context: Context, at: LocalDateTime?) {
        val alarms = context.getSystemService(AlarmManager::class.java)
        val intent = PendingIntent.getBroadcast(
            context, 7, Intent(context, TicketWidgetAlarmReceiver::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        if (at == null) { alarms.cancel(intent); return }
        val ms = at.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli() + 2_000 // just past the minute
        runCatching {
            if (Build.VERSION.SDK_INT < 31 || alarms.canScheduleExactAlarms()) alarms.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, ms, intent)
            else alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, ms, intent)
        }.onFailure { alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, ms, intent) }
    }

    suspend fun hasWidget(context: Context) =
        runCatching { GlanceAppWidgetManager(context).getGlanceIds(TicketWidget::class.java).isNotEmpty() }.getOrDefault(false)
}

data class TicketData(val ticket: Ticket?, val at: LocalDateTime?)

class TicketWidget : AkashicWidget<TicketData>() {
    override suspend fun load(context: Context, shift: Int): TicketData {
        val (shown, next) = runCatching { NextTicket.state(AkashicDatabase.get(context)) }.getOrDefault(null to null)
        NextTicket.arm(context.applicationContext, next)
        return TicketData(shown?.first, shown?.second)
    }

    @Composable
    override fun Content(context: Context, data: TicketData) {
        val t = data.ticket
        if (t == null) {
            // Empty: a barely-there frame, tapping it opens the list of tickets.
            Box(
                GlanceModifier.fillMaxSize().clickable(actionStartActivity(W.open(context, Launch.TICKETS))),
                contentAlignment = Alignment.Center,
            ) {
                Text("🎫", style = TextStyle(color = W.faint, fontSize = 14.sp))
            }
            return
        }
        val open = W.open(context, Launch.TICKETS).putExtra(Launch.EXTRA_ITEM, t.Id)
        WidgetCard(GlanceModifier.clickable(actionStartActivity(open))) {
            Text("🎫 BILLET · ${t.timeText}", style = TextStyle(color = ColorProvider(TicketColor), fontSize = 12.sp, fontWeight = FontWeight.Bold))
            Spacer(GlanceModifier.height(4.dp))
            Text(t.Title, style = TextStyle(color = W.text, fontSize = 16.sp, fontWeight = FontWeight.Bold), maxLines = 2)
            if (t.EventTitle.isNotBlank()) Text(t.EventTitle, style = TextStyle(color = W.muted, fontSize = 12.sp), maxLines = 1)
            Spacer(GlanceModifier.height(4.dp))
            Text("Toucher pour l'afficher", style = TextStyle(color = W.faint, fontSize = 11.sp))
        }
    }
}

class TicketWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget = TicketWidget()
}

// The moment a ticket comes into or out of view: the widget is drawn again (which arms the next one).
class TicketWidgetAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val pending = goAsync()
        CoroutineScope(Dispatchers.IO).launch {
            try {
                Widgets.refreshTicket(context.applicationContext)
            } finally {
                pending.finish()
            }
        }
    }
}
