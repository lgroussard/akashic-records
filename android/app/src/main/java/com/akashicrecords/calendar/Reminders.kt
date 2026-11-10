package com.akashicrecords.calendar

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.akashicrecords.MainActivity
import com.akashicrecords.R
import com.akashicrecords.data.AkashicDatabase
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.util.concurrent.TimeUnit

// Phone version of the desktop calendar toasts (MainWindow.CheckCalendarReminders):
//   * a morning summary of today's agenda, once a day (from 08:00);
//   * a reminder per item at its lead time (per kind, desktop defaults), birthdays on the day at
//     09:00 as a heads-up notification.
// Runs in WorkManager every 15 minutes (so a reminder can be a few minutes late), plus right after
// an edit. Notified occurrences are remembered so each one pings once.
object Reminders {
    private const val CHANNEL_AGENDA = "agenda"
    private const val CHANNEL_BIRTHDAYS = "birthdays"
    private const val PREFS = "akashic_reminders"
    private const val KEY_NOTIFIED = "notified"
    private const val KEY_SUMMARY_DAY = "summaryDay"
    private const val KEY_SUMMARY_AT = "summaryAt"
    private const val SUMMARY_HOUR = 8

    const val EXTRA_SECTION = "akashic.section"
    const val EXTRA_DATE = "akashic.date"

    fun schedule(context: Context) {
        ensureChannels(context)
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(
            "akashic-reminders",
            ExistingPeriodicWorkPolicy.KEEP,
            PeriodicWorkRequestBuilder<ReminderWorker>(15, TimeUnit.MINUTES).build(),
        )
    }

    // After an add/edit: a reminder due now fires without waiting for the next period.
    fun checkNow(context: Context) {
        WorkManager.getInstance(context).enqueueUniqueWork(
            "akashic-reminders-now",
            ExistingWorkPolicy.REPLACE,
            OneTimeWorkRequestBuilder<ReminderWorker>().build(),
        )
    }

    private fun ensureChannels(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_AGENDA, "Calendrier", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Programme du jour et rappels d'événements"
            },
        )
        nm.createNotificationChannel(
            // HIGH: shown as a pop-up over whatever is on screen.
            NotificationChannel(CHANNEL_BIRTHDAYS, "Anniversaires", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Le jour d'un anniversaire"
            },
        )
    }

    suspend fun check(context: Context) {
        ensureChannels(context)
        val db = AkashicDatabase.get(context)
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val notified = prefs.getStringSet(KEY_NOTIFIED, emptySet())!!.toMutableSet()
        val now = LocalDateTime.now()
        val today = now.toLocalDate()

        // Morning summary, once per day.
        var summaryAt = prefs.getString(KEY_SUMMARY_AT, null)?.let { runCatching { LocalDateTime.parse(it) }.getOrNull() }
        if (prefs.getString(KEY_SUMMARY_DAY, null) != today.toString() && now.toLocalTime() >= LocalTime.of(SUMMARY_HOUR, 0)) {
            val todays = Agenda.range(db, today, today)
            prefs.edit().putString(KEY_SUMMARY_DAY, today.toString()).putString(KEY_SUMMARY_AT, now.toString()).apply()
            summaryAt = now
            if (todays.isNotEmpty()) notifySummary(context, todays)
        }

        for (item in Agenda.range(db, today, today.plusDays(35))) {
            val key = item.notificationKey
            if (key in notified) continue
            val at = item.reminderTime() ?: continue
            if (at > now) continue
            // Covered by today's summary if it was already due when the summary went out.
            val coveredBySummary = item.date == today && summaryAt != null && summaryAt.toLocalDate() == today && at <= summaryAt
            notified += key
            if (coveredBySummary && item.source != AgendaSource.Birthday) continue
            // Long past (phone off for hours): mark quietly, it is not a reminder any more —
            // except a birthday, which still pops up for as long as it is that day.
            val birthdayToday = item.source == AgendaSource.Birthday && item.date == today
            if (at < now.minusHours(3) && !birthdayToday) continue
            notifyItem(context, item)
        }

        // Keys older than two days are dropped so the set cannot grow forever.
        val cutoff = today.minusDays(2)
        notified.removeAll { k -> runCatching { LocalDate.parse(k.substringAfterLast(':')) < cutoff }.getOrDefault(true) }
        prefs.edit().putStringSet(KEY_NOTIFIED, notified).apply()
    }

    private fun canNotify(context: Context) =
        Build.VERSION.SDK_INT < 33 ||
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    private fun openCalendar(context: Context, date: LocalDate, requestCode: Int): PendingIntent {
        val intent = Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
            putExtra(EXTRA_SECTION, "Calendrier")
            putExtra(EXTRA_DATE, date.toString())
        }
        return PendingIntent.getActivity(context, requestCode, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }

    private fun line(item: AgendaItem) = listOf(item.timeText, item.title, item.subtitle).filter { it.isNotBlank() }.joinToString(" · ")

    private fun notifySummary(context: Context, items: List<AgendaItem>) {
        if (!canNotify(context)) return
        val style = NotificationCompat.InboxStyle()
        items.take(6).forEach { style.addLine(line(it)) }
        if (items.size > 6) style.setSummaryText("+${items.size - 6}")
        val n = NotificationCompat.Builder(context, CHANNEL_AGENDA)
            .setSmallIcon(R.drawable.ic_calendar)
            .setContentTitle("Aujourd'hui")
            .setContentText(items.joinToString(", ") { it.title })
            .setStyle(style)
            .setContentIntent(openCalendar(context, items.first().date, 0))
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(context).notify(1000, n)
    }

    private fun notifyItem(context: Context, item: AgendaItem) {
        if (!canNotify(context)) return
        val birthday = item.source == AgendaSource.Birthday
        val whenText = when (item.date) {
            LocalDate.now() -> if (item.timeText.isNotEmpty()) "Aujourd'hui à ${item.timeText}" else "Aujourd'hui"
            LocalDate.now().plusDays(1) -> "Demain" + if (item.timeText.isNotEmpty()) " à ${item.timeText}" else ""
            else -> "Le ${item.date.dayOfMonth}/${item.date.monthValue}" + if (item.timeText.isNotEmpty()) " à ${item.timeText}" else ""
        }
        val text = listOf(whenText, item.subtitle).filter { it.isNotBlank() }.joinToString(" · ")
        val n = NotificationCompat.Builder(context, if (birthday) CHANNEL_BIRTHDAYS else CHANNEL_AGENDA)
            .setSmallIcon(if (birthday) R.drawable.ic_cake else R.drawable.ic_calendar)
            .setContentTitle(if (birthday) "🎂 ${item.title}" else item.title)
            .setContentText(text)
            .setPriority(if (birthday) NotificationCompat.PRIORITY_HIGH else NotificationCompat.PRIORITY_DEFAULT)
            .setCategory(if (birthday) NotificationCompat.CATEGORY_REMINDER else NotificationCompat.CATEGORY_EVENT)
            .setContentIntent(openCalendar(context, item.date, item.notificationKey.hashCode()))
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(context).notify(item.notificationKey.hashCode(), n)
    }
}

class ReminderWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        Reminders.check(applicationContext)
        return Result.success()
    }
}
