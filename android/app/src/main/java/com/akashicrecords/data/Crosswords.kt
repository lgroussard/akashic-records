package com.akashicrecords.data

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Update

// ---- Mots croisés (phone only: the PC has the same table, declared in SchemaInitializer, but no screen) ----
//
// One grid being played or finished. PuzzleJson = the grid and its clues (crossword.Puzzle.toJson); Progress = the
// letters typed so far, row after row ('.' = empty square, '#' = black one). Source: "Générée" or the imported file's
// name. Phone-owned: DesktopImport leaves the table alone.
@Entity(tableName = "CrosswordGame")
data class CrosswordGame(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val Source: String,
    val PuzzleJson: String,
    val Progress: String,
    val CreatedAt: String,
    val UpdatedAt: String,
    val CompletedAt: String?,
)

@Dao
interface CrosswordGameDao {
    @Insert
    suspend fun add(e: CrosswordGame): Long

    @Update
    suspend fun update(e: CrosswordGame)

    @Query("SELECT * FROM CrosswordGame WHERE Id = :id")
    suspend fun getById(id: Int): CrosswordGame?

    // In progress first, then the most recently played.
    @Query("SELECT * FROM CrosswordGame ORDER BY (CompletedAt IS NOT NULL), UpdatedAt DESC")
    suspend fun getAll(): List<CrosswordGame>

    // The answers of the latest grids, so a new grid avoids reusing their words.
    @Query("SELECT PuzzleJson FROM CrosswordGame ORDER BY CreatedAt DESC LIMIT :count")
    suspend fun latestPuzzles(count: Int): List<String>

    @Query("DELETE FROM CrosswordGame WHERE Id = :id")
    suspend fun deleteById(id: Int)
}
