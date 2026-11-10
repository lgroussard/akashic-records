package com.akashicrecords.calendar

import androidx.compose.ui.graphics.Color
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Birthday
import com.akashicrecords.data.CalendarEvent
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.YearMonth

// Port of the desktop CalendarService: the three things worth showing on a calendar, merged into
// one agenda — birthdays (recurring MM-DD), calendar events (dated, optionally yearly, optionally
// multi-day) and project deadlines. The screen and the reminder worker read the same agenda.

enum class AgendaSource { Birthday, Event, Deadline }

// Ordinals match the desktop CalendarEventKind enum (stored as Int in CalendarEvent.Kind).
enum class EventKind(val label: String, val color: Color, val defaultLeadMinutes: Int) {
    Sortie("Sortie", Color(0xFFE0823C), 120),
    Plan("Plan", Color(0xFF4ADE80), 1440),
    Voyage("Voyage", Color(0xFF38BDF8), 10080), // 1 week before
    Rendezvous("Rendez-vous", Color(0xFFC084FC), 60),
    Autre("Autre", Color(0xFF94A3B8), 1440);

    companion object {
        fun of(ordinal: Int) = entries.getOrElse(ordinal) { Autre }
    }
}

val BirthdayColor = Color(0xFFF06CA8)
val DeadlineColor = Color(0xFFF2C14E)

// Desktop lead-time defaults (AppConfig): birthdays on the day (09:00), deadlines 2 days before.
const val BIRTHDAY_LEAD_MINUTES = 0
const val DEADLINE_LEAD_MINUTES = 2880

data class AgendaItem(
    val date: LocalDate,
    val title: String,
    val subtitle: String,
    val source: AgendaSource,
    val refId: Int,
    val kind: EventKind? = null,
    val hour: Int? = null,
    val minute: Int? = null,
    val isSpanContinuation: Boolean = false,
) {
    val color: Color
        get() = when (source) {
            AgendaSource.Birthday -> BirthdayColor
            AgendaSource.Deadline -> DeadlineColor
            AgendaSource.Event -> (kind ?: EventKind.Autre).color
        }

    val timeText: String get() = hour?.let { "%02d:%02d".format(it, minute ?: 0) } ?: ""

    // Same key as the desktop (Source:RefId:yyyy-MM-dd): one reminder per occurrence.
    val notificationKey: String get() = "$source:$refId:$date"

    // When the reminder should fire: lead minutes back from the clock time, or from 09:00 for
    // all-day items. Null = no reminder for this item.
    fun reminderTime(): LocalDateTime? {
        if (isSpanContinuation) return null // a multi-day event pings once, on its first day
        val lead = when (source) {
            AgendaSource.Birthday -> BIRTHDAY_LEAD_MINUTES
            AgendaSource.Deadline -> DEADLINE_LEAD_MINUTES
            AgendaSource.Event -> (kind ?: EventKind.Autre).defaultLeadMinutes
        }
        if (lead < 0) return null
        val reference = date.atTime(hour ?: 9, if (hour != null) minute ?: 0 else 0)
        return reference.minusMinutes(lead.toLong())
    }
}

object Agenda {
    // All items whose date falls in [from, to] (inclusive), recurring ones materialised per year.
    suspend fun range(db: AkashicDatabase, from: LocalDate, to: LocalDate): List<AgendaItem> {
        val items = ArrayList<AgendaItem>()

        for (b in db.birthdayDao().getAll()) {
            val date = anniversaryIn(b.Month, b.Day, from, to) ?: continue
            items += AgendaItem(
                date = date,
                title = birthdayTitle(b.Name),
                subtitle = b.BirthYear?.let { turningAge(it, date.year) } ?: "",
                source = AgendaSource.Birthday,
                refId = b.Id,
            )
        }

        for (e in db.calendarEventDao().getAll()) {
            val start = day(e.Date) ?: continue
            val kind = EventKind.of(e.Kind)
            if (e.RecurringYearly) {
                val date = anniversaryIn(start.monthValue, start.dayOfMonth, from, to) ?: continue
                items += eventItem(e, kind, date, first = true)
                continue
            }
            // Multi-day: one row per covered day inside the window, the hour on the first day only.
            val end = day(e.EndDate)?.takeIf { it > start } ?: start
            var d = maxOf(start, from)
            val last = minOf(end, to)
            while (d <= last) {
                items += eventItem(e, kind, d, first = d == start)
                d = d.plusDays(1)
            }
        }

        for (p in db.personalProjectDao().getAll()) {
            if (p.Status == 2) continue // archived
            val date = day(p.Deadline) ?: continue
            if (date < from || date > to) continue
            items += AgendaItem(
                date = date,
                title = p.Title,
                subtitle = if (p.Status == 1) "Échéance (projet terminé)" else "Échéance de projet",
                source = AgendaSource.Deadline,
                refId = p.Id,
            )
        }

        return items.sortedWith(compareBy<AgendaItem>({ it.date }, { it.hour ?: -1 }, { it.title.lowercase() }))
    }

    suspend fun month(db: AkashicDatabase, ym: YearMonth) = range(db, ym.atDay(1), ym.atEndOfMonth())

    private fun eventItem(e: CalendarEvent, kind: EventKind, date: LocalDate, first: Boolean) = AgendaItem(
        date = date,
        title = e.Title,
        subtitle = e.Location,
        source = AgendaSource.Event,
        refId = e.Id,
        kind = kind,
        hour = if (first) e.StartHour else null,
        minute = if (first) e.StartMinute else null,
        isSpanContinuation = !first,
    )

    // Date part of a desktop DateTime.ToString("O") ("2026-09-12T00:00:00.0000000") or a plain date.
    fun day(iso: String?): LocalDate? = iso?.takeIf { it.length >= 10 }?.let { runCatching { LocalDate.parse(it.take(10)) }.getOrNull() }

    private fun anniversaryIn(month: Int, dayOfMonth: Int, from: LocalDate, to: LocalDate): LocalDate? {
        for (year in from.year..to.year) {
            val candidate = safeDate(year, month, dayOfMonth)
            if (candidate in from..to) return candidate
        }
        return null
    }

    // 29 Feb in a non-leap year lands on 28 Feb: kept visible, never dropped.
    fun safeDate(year: Int, month: Int, dayOfMonth: Int): LocalDate {
        val ym = YearMonth.of(year, month.coerceIn(1, 12))
        return ym.atDay(dayOfMonth.coerceIn(1, ym.lengthOfMonth()))
    }

    private fun turningAge(birthYear: Int, year: Int): String {
        val age = year - birthYear
        return when {
            age <= 0 -> ""
            age == 1 -> "1 an"
            else -> "$age ans"
        }
    }

    // French elision, as on the desktop: "Anniversaire d'Alyssa", "Anniversaire de Louison".
    fun birthdayTitle(name: String): String {
        val n = name.trim()
        if (n.isEmpty()) return "Anniversaire"
        return if (n[0].lowercaseChar() in "aeiouyàâäéèêëïîôöùûüÿh") "Anniversaire d'$n" else "Anniversaire de $n"
    }

    fun birthdayOf(b: Birthday) = birthdayTitle(b.Name)
}
