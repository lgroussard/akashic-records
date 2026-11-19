package com.akashicrecords.calendar

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.LocalDate
import java.time.LocalDateTime

// What the reminder alarm is armed for, and the key that decides whether an occurrence already rang.
class NextReminderTest {

    private val day = LocalDate.of(2026, 11, 20)
    private val now = day.atTime(10, 0)

    private fun event(kind: EventKind, date: LocalDate, hour: Int? = null, minute: Int? = null, id: Int = 1, continuation: Boolean = false) =
        AgendaItem(date, "Titre", "", AgendaSource.Event, id, kind, hour, minute, continuation)

    @Test
    fun picksTheEarliestReminderStillToCome() {
        val items = listOf(
            event(EventKind.Rendezvous, day, 18, 30, id = 1), // 1 h before: 17:30
            event(EventKind.Sortie, day, 14, 0, id = 2), // 2 h before: 12:00
            event(EventKind.Rendezvous, day, 10, 30, id = 3), // 09:30, already due
        )
        assertEquals(day.atTime(12, 0), nextReminderAt(items, now))
    }

    @Test
    fun aReminderDueRightNowIsNotToCome() {
        assertNull(nextReminderAt(listOf(event(EventKind.Rendezvous, day, 11, 0)), now))
    }

    @Test
    fun allDayItemsCountFromNineOClock() {
        val birthday = AgendaItem(day.plusDays(1), "Anniversaire d'Alyssa", "", AgendaSource.Birthday, 4)
        val deadline = AgendaItem(day.plusDays(5), "Projet", "", AgendaSource.Deadline, 5) // 2 days before, 09:00
        assertEquals(day.plusDays(1).atTime(9, 0), nextReminderAt(listOf(deadline, birthday), now))
        assertEquals(day.plusDays(3).atTime(9, 0), nextReminderAt(listOf(deadline), now))
    }

    @Test
    fun followingDaysOfAMultiDayEventDoNotRing() {
        val items = listOf(event(EventKind.Voyage, day.plusDays(1), continuation = true))
        assertNull(nextReminderAt(items, now))
    }

    @Test
    fun nothingLeftMeansNoAlarm() {
        assertNull(nextReminderAt(emptyList(), now))
        assertNull(nextReminderAt(listOf(event(EventKind.Plan, day)), now)) // the day before at 09:00: past
    }

    @Test
    fun movingAnEventToAnotherHourChangesItsNotifiedKey() {
        val before = event(EventKind.Rendezvous, day, 14, 0)
        val after = event(EventKind.Rendezvous, day, 16, 0)
        assertEquals(before.notificationKey, after.notificationKey) // same occurrence
        assertNotEquals(before.notifiedKey(before.reminderTime()!!), after.notifiedKey(after.reminderTime()!!))
    }

    @Test
    fun aTimedEventAnnouncedDaysAheadRingsAgainAnHourBefore() {
        val trip = event(EventKind.Voyage, day.plusDays(2), 14, 30) // a week before, then 13:30 on the day
        assertEquals(listOf(day.minusDays(5).atTime(14, 30), day.plusDays(2).atTime(13, 30)), trip.reminderTimes())
        assertEquals(day.plusDays(2).atTime(13, 30), nextReminderAt(listOf(trip), now))
        // Already reminded close to its hour, or no hour at all: one reminder only.
        assertEquals(1, event(EventKind.Sortie, day, 20, 0).reminderTimes().size)
        assertEquals(1, event(EventKind.Voyage, day.plusDays(2)).reminderTimes().size)
    }

    @Test
    fun aTicketWithATimeRingsAnHourBeforeItLeaves() {
        val ticket = AgendaItem(day, "Aller", "Lyon", AgendaSource.Ticket, 7, hour = 14, minute = 32)
        assertEquals(listOf(day.atTime(13, 32)), ticket.reminderTimes())
        assertEquals(day.atTime(13, 32), nextReminderAt(listOf(ticket), now))
    }

    // Reminders prunes the notified set on the date that ends each key.
    @Test
    fun theNotifiedKeyEndsWithTheDate() {
        val item = event(EventKind.Rendezvous, day, 14, 0)
        val key = item.notifiedKey(item.reminderTime()!!)
        assertEquals(day, LocalDate.parse(key.substringAfterLast(':')))
        assertEquals(LocalDateTime.of(2026, 11, 20, 13, 0), item.reminderTime())
    }
}
