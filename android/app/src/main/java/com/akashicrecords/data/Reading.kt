package com.akashicrecords.data

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Update

// ---- Lecture (phone only: the PC has the same two tables, declared in SchemaInitializer, but no screen) ----
//
// The epub itself lives in <filesDir>/books/<file>, outside media/ (which a PC import fills).
// Locator = where reading stopped, "<chapter index>:<progression 0..1 in the chapter>".
// ArtworkId = the book's fiche in Collections → Livres (created on the first highlight, or linked by hand).
// Both tables are phone-owned: DesktopImport leaves them alone and re-links them afterwards
// (ReadingLinks.relinkAfterImport), since it refills Artwork/Observation from the PC with other Ids.
@Entity(tableName = "Book")
data class Book(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val Author: String,
    val FilePath: String,
    val CoverPath: String?,
    val AddedAt: String,
    val LastReadAt: String?,
    val Locator: String?,
    val Progress: Double,
    val ArtworkId: Int?,
)

// A highlighted passage. Text is kept here in full, so the observation it feeds (ObservationId, in the book's
// Collections fiche) can always be recreated. Locator = "<start>-<end>" character offsets in the chapter's text.
@Entity(tableName = "BookHighlight")
data class BookHighlight(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val BookId: Int,
    val Chapter: Int,
    val Text: String,
    val Note: String,
    val Locator: String,
    val CreatedAt: String,
    val ObservationId: Int?,
)

@Dao
interface BookDao {
    @Insert
    suspend fun add(e: Book): Long

    @Update
    suspend fun update(e: Book)

    @Query("DELETE FROM Book WHERE Id = :id")
    suspend fun deleteById(id: Int)

    // Last read first, then the newest additions.
    @Query("SELECT * FROM Book ORDER BY LastReadAt IS NULL, LastReadAt DESC, AddedAt DESC")
    suspend fun getAll(): List<Book>

    @Query("SELECT * FROM Book WHERE Id = :id")
    suspend fun getById(id: Int): Book?
}

@Dao
interface BookHighlightDao {
    @Insert
    suspend fun add(e: BookHighlight): Long

    @Update
    suspend fun update(e: BookHighlight)

    @Query("DELETE FROM BookHighlight WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("DELETE FROM BookHighlight WHERE BookId = :bookId")
    suspend fun deleteForBook(bookId: Int)

    @Query("SELECT * FROM BookHighlight WHERE BookId = :bookId ORDER BY Chapter, CreatedAt")
    suspend fun forBook(bookId: Int): List<BookHighlight>

    @Query("SELECT * FROM BookHighlight")
    suspend fun getAll(): List<BookHighlight>
}
