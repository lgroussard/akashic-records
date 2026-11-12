package com.akashicrecords.tickets

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import com.akashicrecords.calendar.Agenda
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.CalendarEvent
import com.akashicrecords.data.Ticket
import com.akashicrecords.journaux.nowIso
import com.akashicrecords.media.MediaFiles
import java.io.File
import java.time.LocalDate
import java.util.UUID

// Billets: files shared from SNCF Connect, Trainline, a mail… copied into media\tickets and attached to a day (and an
// event when there is one). PDFs are drawn as pictures (PdfRenderer), one bitmap per page.
object TicketStore {
    // Bumped on every add / delete, so the calendar's chips follow (wherever the change came from: share, list, viewer).
    var changes by androidx.compose.runtime.mutableIntStateOf(0)
        private set

    // What a shared or picked file is: its type (PDF or image) and a readable default name.
    data class Incoming(val uri: Uri, val mime: String, val name: String)

    // `sharedType` = the type the sharing app announced, used when the file itself does not say.
    fun describe(context: Context, uri: Uri, sharedType: String? = null): Incoming? {
        val resolver = context.contentResolver
        val name = runCatching {
            resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
                if (c.moveToFirst()) c.getString(0) else null
            }
        }.getOrNull().orEmpty()
        val mime = resolver.getType(uri) ?: when {
            name.endsWith(".pdf", true) -> "application/pdf"
            name.matches(Regex(".*\\.(jpe?g|png|webp)$", RegexOption.IGNORE_CASE)) -> "image/*"
            sharedType == "application/pdf" || sharedType?.startsWith("image/") == true -> sharedType
            else -> return null
        }
        if (mime != "application/pdf" && !mime.startsWith("image/")) return null
        return Incoming(uri, if (mime == "application/pdf") mime else "image", name.substringBeforeLast('.').trim())
    }

    // Copies the file into the app and adds the ticket. Event = the one it belongs to, or null for the day alone.
    suspend fun add(context: Context, db: AkashicDatabase, item: Incoming, title: String, date: LocalDate, event: CalendarEvent?): Ticket {
        val path = if (item.mime == "application/pdf") copyPdf(context, item.uri) else MediaFiles.import(context, item.uri, "tickets")
        val t = Ticket(
            Title = title.trim(), FilePath = path, MimeType = item.mime, Date = date.toString(),
            EventId = event?.Id, EventTitle = event?.Title.orEmpty(), CreatedAt = nowIso(),
        )
        return t.copy(Id = db.ticketDao().add(t).toInt()).also { bump() }
    }

    private fun bump() = android.os.Handler(android.os.Looper.getMainLooper()).post { changes++ }

    private fun copyPdf(context: Context, uri: Uri): String {
        val dir = File(context.filesDir, "media/tickets").apply { mkdirs() }
        val name = UUID.randomUUID().toString().replace("-", "") + ".pdf"
        context.contentResolver.openInputStream(uri)!!.use { input -> File(dir, name).outputStream().use { input.copyTo(it) } }
        return "media\\tickets\\$name"
    }

    suspend fun delete(context: Context, db: AkashicDatabase, t: Ticket) {
        db.ticketDao().deleteById(t.Id)
        MediaFiles.delete(context, t.FilePath)
        bump()
    }

    // The ticket as pictures: the image itself, or each PDF page drawn on white at about `width` px wide.
    fun render(context: Context, t: Ticket, width: Int): List<Bitmap> {
        val file = MediaFiles.resolve(context, t.FilePath)?.takeIf { it.isFile } ?: return emptyList()
        if (!t.isPdf) return listOfNotNull(MediaFiles.decode(file, 2400))
        return runCatching {
            ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use { fd ->
                PdfRenderer(fd).use { pdf ->
                    (0 until minOf(pdf.pageCount, 12)).map { i ->
                        pdf.openPage(i).use { page ->
                            val w = width.coerceIn(600, 2000)
                            val h = (w.toFloat() * page.height / page.width).toInt()
                            Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888).also {
                                it.eraseColor(Color.WHITE)
                                page.render(it, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                            }
                        }
                    }
                }
            }
        }.getOrDefault(emptyList())
    }

    // Events a ticket can go with: those not over yet (multi-day ones until their last day), soonest first.
    suspend fun upcomingEvents(db: AkashicDatabase, today: LocalDate = LocalDate.now()): List<Pair<CalendarEvent, LocalDate>> =
        db.calendarEventDao().getAll().mapNotNull { e ->
            val start = Agenda.day(e.Date) ?: return@mapNotNull null
            val end = Agenda.day(e.EndDate)?.takeIf { it > start } ?: start
            when {
                e.RecurringYearly -> null // birthdays-like yearly events: not trips
                end < today -> null
                else -> e to start
            }
        }.sortedBy { it.second }

    // Today's tickets, or else the next day that has some: what the « Billets » shortcut opens on.
    suspend fun current(db: AkashicDatabase, today: LocalDate = LocalDate.now()): List<Ticket> {
        val ahead = db.ticketDao().from(today.toString())
        val first = ahead.firstOrNull()?.Date ?: return emptyList()
        return ahead.filter { it.Date == first }
    }
}
