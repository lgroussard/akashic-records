package com.akashicrecords.data

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Update

// ---- Billets (phone only: the PC has the same table, declared in SchemaInitializer, but no screen) ----
//
// A train ticket, boarding pass, concert ticket… kept as its file (PDF or image) in media\tickets, attached to a day and,
// optionally, to a calendar event. Date is always set (the event's first day when attached to one), so a ticket whose
// event disappears — a PC import replaces the events — still shows on its day. EventTitle is the event's name at the
// time, for the same reason. Phone-owned: DesktopImport leaves the table alone.
@Entity(tableName = "Ticket")
data class Ticket(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val FilePath: String,
    val MimeType: String,
    val Date: String, // yyyy-MM-dd
    val EventId: Int?,
    val EventTitle: String,
    val CreatedAt: String,
) {
    val isPdf: Boolean get() = MimeType == "application/pdf"
}

@Dao
interface TicketDao {
    @Insert
    suspend fun add(t: Ticket): Long

    @Update
    suspend fun update(t: Ticket)

    @Query("SELECT * FROM Ticket ORDER BY Date, CreatedAt")
    suspend fun getAll(): List<Ticket>

    @Query("SELECT * FROM Ticket WHERE Date >= :from ORDER BY Date, CreatedAt")
    suspend fun from(from: String): List<Ticket>

    @Query("SELECT * FROM Ticket WHERE Date < :before ORDER BY Date DESC, CreatedAt")
    suspend fun before(before: String): List<Ticket>

    @Query("DELETE FROM Ticket WHERE Id = :id")
    suspend fun deleteById(id: Int)
}
