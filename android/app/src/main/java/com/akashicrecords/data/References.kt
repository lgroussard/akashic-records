package com.akashicrecords.data

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Update

// ---- Références (Journaux): image boards, in place of Pinterest ----
//
// Each device keeps its own boards for now: the phone's are phone-owned (DesktopImport leaves both tables
// alone, like the books) and the PC's stay on the PC, until the server syncs them. Same two tables on both
// sides (SchemaInitializer on the PC), so that sync has nothing to translate.
//
// Images are copied locally: <filesDir>/references/<file> on the phone (outside media/, which an import fills).
// BoardId null = « À trier » (shared or found, not filed yet).
@Entity(tableName = "RefBoard")
data class RefBoard(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val CreatedAt: String,
)

@Entity(tableName = "RefImage")
data class RefImage(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val BoardId: Int?,
    val ImagePath: String, // "references/<file>"
    val Title: String,
    val Author: String,
    val Source: String, // "Are.na", "Wikimedia Commons", "Art Institute of Chicago", "Partage"
    val SourceUrl: String?, // the image itself
    val PageUrl: String?, // where it was found (a pin, a Commons file page, a museum page…)
    val Width: Int,
    val Height: Int,
    val Note: String,
    val CreatedAt: String,
)

@Dao
interface RefBoardDao {
    @Insert
    suspend fun add(e: RefBoard): Long

    @Update
    suspend fun update(e: RefBoard)

    @Query("SELECT * FROM RefBoard ORDER BY Title COLLATE NOCASE")
    suspend fun getAll(): List<RefBoard>

    @Query("DELETE FROM RefBoard WHERE Id = :id")
    suspend fun deleteById(id: Int)
}

@Dao
interface RefImageDao {
    @Insert
    suspend fun add(e: RefImage): Long

    @Update
    suspend fun update(e: RefImage)

    @Query("SELECT * FROM RefImage ORDER BY CreatedAt DESC, Id DESC")
    suspend fun getAll(): List<RefImage>

    @Query("SELECT * FROM RefImage WHERE BoardId = :boardId ORDER BY CreatedAt DESC, Id DESC")
    suspend fun forBoard(boardId: Int): List<RefImage>

    @Query("SELECT * FROM RefImage WHERE BoardId IS NULL ORDER BY CreatedAt DESC, Id DESC")
    suspend fun unsorted(): List<RefImage>

    @Query("SELECT COUNT(*) FROM RefImage WHERE SourceUrl = :url")
    suspend fun countWithSource(url: String): Int

    @Query("UPDATE RefImage SET BoardId = NULL WHERE BoardId = :boardId")
    suspend fun unfileBoard(boardId: Int)

    @Query("DELETE FROM RefImage WHERE Id = :id")
    suspend fun deleteById(id: Int)
}
